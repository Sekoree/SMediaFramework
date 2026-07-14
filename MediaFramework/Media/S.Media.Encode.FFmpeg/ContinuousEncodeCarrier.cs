using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace S.Media.Encode.FFmpeg;

/// <summary>
/// Keeps an encode session on one continuous wall-clock program timeline. From <see cref="Start"/> until
/// disposal it supplies opaque black video and silence whenever playback is not producing that leg, and
/// exposes activity-aware sink wrappers that hand off without changing the carrier clock.
/// </summary>
/// <remarks>
/// This is shared policy for live streams and continuous file recordings. Content-only recordings should
/// attach directly to <see cref="FFmpegEncodeSession"/> instead: they deliberately collapse idle time.
/// </remarks>
public sealed class ContinuousEncodeCarrier : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger(
        "S.Media.Encode.FFmpeg.ContinuousEncodeCarrier");

    private readonly FFmpegEncodeVideoSink? _encodeVideoSink;
    private readonly IReadOnlyList<FFmpegEncodeAudioSink> _encodeAudioSinks;
    private readonly VideoFormat _format;
    private readonly bool _hasVideo;
    private readonly int _audioSampleRate;
    private readonly int _rate;
    private readonly long _videoIdleGraceTicks;
    private readonly long _audioIdleGraceTicks;
    private readonly Lock _handoffGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _blackPlane;
    private readonly float[][] _silenceByLeg;
    private readonly IVideoOutput? _playbackVideoSink;
    private readonly IReadOnlyList<IAudioOutput> _playbackAudioSinks;
    private readonly IAudioOutput? _playbackCombinedAudioSink;
    private readonly long[] _lastRealAudioSubmit;
    private readonly long[] _audioSilenceDebtFrames;
    private readonly bool[] _audioFilling;
    private Thread? _thread;
    private long _lastRealVideoSubmit = long.MinValue;
    private bool _videoRouteActive;
    private bool _audioRouteActive;
    private bool _videoFilling;
    private bool _started;
    private bool _disposed;

    /// <param name="width">Locked output width; required when the session contains video.</param>
    /// <param name="height">Locked output height; required when the session contains video.</param>
    /// <param name="fps">Locked output cadence; required when the session contains video.</param>
    public ContinuousEncodeCarrier(FFmpegEncodeSession session, int width, int height, int fps)
    {
        ArgumentNullException.ThrowIfNull(session);
        _encodeVideoSink = session.VideoSink;
        _encodeAudioSinks = session.AudioSinks;
        _hasVideo = _encodeVideoSink is not null;
        if (_hasVideo && (width <= 0 || height <= 0 || fps <= 0))
            throw new ArgumentException(
                "A continuous video carrier requires an explicit output width, height, and frame rate.");

        var w = Math.Max(2, width & ~1);
        var h = Math.Max(2, height & ~1);
        // The carrier clock also paces audio-only silence, so retain a modest cadence without video.
        _rate = fps > 0 ? fps : 30;
        _format = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(_rate, 1));
        _audioSampleRate = _encodeAudioSinks.Count > 0 ? _encodeAudioSinks[0].Format.SampleRate : 0;
        _videoIdleGraceTicks = (long)Math.Ceiling(
            Stopwatch.Frequency * Math.Max(0.25, 3.0 / _rate));
        _audioIdleGraceTicks = (long)Math.Ceiling(
            Stopwatch.Frequency * Math.Max(0.1, 3.0 / _rate));

        // Opaque black BGRA (B=G=R=0, A=255), shared read-only by all generated frames.
        _blackPlane = _hasVideo ? new byte[w * 4 * h] : [];
        for (var i = 3; i < _blackPlane.Length; i += 4)
            _blackPlane[i] = 255;

        // One independent silence buffer/activity clock/debt counter per encoded audio track. Debt
        // retains the short handoff grace: once a route really falls silent we emit the intervening
        // samples too, rather than shortening the program timeline at every stop.
        _silenceByLeg = new float[_encodeAudioSinks.Count][];
        _lastRealAudioSubmit = new long[_encodeAudioSinks.Count];
        _audioSilenceDebtFrames = new long[_encodeAudioSinks.Count];
        _audioFilling = new bool[_encodeAudioSinks.Count];
        Array.Fill(_lastRealAudioSubmit, long.MinValue);
        var playbackAudio = new IAudioOutput[_encodeAudioSinks.Count];
        for (var i = 0; i < _encodeAudioSinks.Count; i++)
        {
            var leg = _encodeAudioSinks[i];
            _silenceByLeg[i] = new float[
                Math.Max(1, (int)Math.Ceiling(_audioSampleRate / (double)_rate)) * leg.Format.Channels];
            playbackAudio[i] = new ActivityAudioOutput(this, leg, i, combined: false);
        }

        _playbackAudioSinks = playbackAudio;
        _playbackCombinedAudioSink = session.CombinedAudioSink is { } combined
            ? new ActivityAudioOutput(this, combined, legIndex: -1, combined: true)
            : null;
        _playbackVideoSink = _encodeVideoSink is { } video
            ? new ActivityVideoOutput(this, video)
            : null;
    }

    public IVideoOutput? VideoSink => _playbackVideoSink;
    public IReadOnlyList<IAudioOutput> AudioSinks => _playbackAudioSinks;
    public IAudioOutput? CombinedAudioSink => _playbackCombinedAudioSink;

    /// <summary>
    /// Declares which sides currently have a playback owner. Acquisition does not suppress filler until
    /// an actual sample arrives; release resumes filler immediately and starts a fresh handoff epoch.
    /// </summary>
    public void SetPlaybackActive(bool videoActive, bool audioActive)
    {
        lock (_handoffGate)
        {
            if (_disposed)
                return;
            if (videoActive && !_videoRouteActive)
                _lastRealVideoSubmit = long.MinValue;
            if (audioActive && !_audioRouteActive)
            {
                Array.Fill(_lastRealAudioSubmit, long.MinValue);
                Array.Clear(_audioSilenceDebtFrames);
            }
            _videoRouteActive = videoActive;
            _audioRouteActive = audioActive;
            if (!videoActive)
                _lastRealVideoSubmit = long.MinValue;
            if (!audioActive)
            {
                Array.Fill(_lastRealAudioSubmit, long.MinValue);
                Array.Clear(_audioSilenceDebtFrames);
            }
        }
    }

    public void Start()
    {
        lock (_handoffGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _started = true;
            // Configure before starting the driver so the stream/file shape is locked from frame one.
            if (_hasVideo)
                _encodeVideoSink!.Configure(_format);
            _thread = new Thread(Run) { IsBackground = true, Name = "ContinuousEncodeCarrier" };
            _thread.Start();
        }
    }

    private void Run()
    {
        var token = _cts.Token;
        var intervalStopwatchTicks = Stopwatch.Frequency / (double)_rate;
        var nextDeadline = Stopwatch.GetTimestamp();
        long audioRemainder = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Serialize filler with activity-reporting wrappers. This makes every handoff an
                // ordered sequence on the encode intake rather than a race between driver threads.
                lock (_handoffGate)
                {
                    var activityNow = Stopwatch.GetTimestamp();
                    var fillVideo = _hasVideo && ShouldFillVideo(activityNow);
                    if (fillVideo != _videoFilling)
                    {
                        _videoFilling = fillVideo;
                        Trace.LogDebug("continuous carrier video filler {State}", fillVideo ? "active" : "yielded");
                    }
                    if (fillVideo)
                    {
                        _encodeVideoSink!.SubmitLive(
                            new VideoFrame(TimeSpan.Zero, _format, [_blackPlane], [_format.Width * 4]));
                    }

                    audioRemainder += _audioSampleRate;
                    var audioFrames = _rate > 0 ? (int)(audioRemainder / _rate) : 0;
                    audioRemainder -= (long)audioFrames * _rate;
                    for (var leg = 0; leg < _encodeAudioSinks.Count; leg++)
                    {
                        if (audioFrames > 0)
                            _audioSilenceDebtFrames[leg] += audioFrames;
                        var fillAudio = audioFrames > 0 && ShouldFillAudio(leg, activityNow);
                        if (fillAudio != _audioFilling[leg])
                        {
                            _audioFilling[leg] = fillAudio;
                            Trace.LogDebug(
                                "continuous carrier audio track {Track} filler {State}",
                                leg + 1, fillAudio ? "active" : "yielded");
                        }
                        if (fillAudio)
                        {
                            SubmitSilenceDebt(leg);
                        }
                    }
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "continuous encode carrier stopped after a filler submission failed");
                break;
            }

            // Absolute pacing without catch-up bursts: preserve phase while on time, but after a stall
            // rebase one interval ahead rather than emitting an unbounded tight burst.
            nextDeadline += (long)Math.Round(intervalStopwatchTicks);
            var now = Stopwatch.GetTimestamp();
            if (nextDeadline <= now)
                nextDeadline = now + (long)Math.Round(intervalStopwatchTicks);
            var delay = TimeSpan.FromSeconds((nextDeadline - now) / (double)Stopwatch.Frequency);
            if (token.WaitHandle.WaitOne(delay))
                break;
        }
    }

    private void SubmitSilenceDebt(int leg)
    {
        var remainingFrames = _audioSilenceDebtFrames[leg];
        if (remainingFrames <= 0)
            return;

        var channels = _encodeAudioSinks[leg].Format.Channels;
        var scratch = _silenceByLeg[leg];
        var scratchFrames = scratch.Length / channels;
        while (remainingFrames > 0)
        {
            var frames = (int)Math.Min(remainingFrames, scratchFrames);
            _encodeAudioSinks[leg].Submit(scratch.AsSpan(0, frames * channels));
            remainingFrames -= frames;
        }
        _audioSilenceDebtFrames[leg] = 0;
    }

    private bool ShouldFillVideo(long now) =>
        !_videoRouteActive
        || _lastRealVideoSubmit == long.MinValue
        || now - _lastRealVideoSubmit >= _videoIdleGraceTicks;

    private bool ShouldFillAudio(int leg, long now) =>
        !_audioRouteActive
        || _lastRealAudioSubmit[leg] == long.MinValue
        || now - _lastRealAudioSubmit[leg] >= _audioIdleGraceTicks;

    private void SubmitRealVideo(FFmpegEncodeVideoSink sink, VideoFrame frame)
    {
        lock (_handoffGate)
        {
            // Arrival time is the carrier coordinate. A stopped composition may keep producing
            // genuinely new black/held frames with one frozen media PTS.
            sink.SubmitLive(frame);
            _lastRealVideoSubmit = Stopwatch.GetTimestamp();
        }
    }

    private void SubmitRealAudio(IAudioOutput sink, int legIndex, bool combined, ReadOnlySpan<float> samples)
    {
        lock (_handoffGate)
        {
            sink.Submit(samples);
            var now = Stopwatch.GetTimestamp();
            if (combined)
            {
                Array.Fill(_lastRealAudioSubmit, now);
                Array.Clear(_audioSilenceDebtFrames);
            }
            else
            {
                _lastRealAudioSubmit[legIndex] = now;
                _audioSilenceDebtFrames[legIndex] = 0;
            }
        }
    }

    private sealed class ActivityVideoOutput(ContinuousEncodeCarrier owner, FFmpegEncodeVideoSink inner)
        : IVideoOutput
    {
        public VideoFormat Format => inner.Format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => inner.AcceptedPixelFormats;
        public void Configure(VideoFormat format) => inner.Configure(format);
        public void Submit(VideoFrame frame) => owner.SubmitRealVideo(inner, frame);
    }

    private sealed class ActivityAudioOutput(
        ContinuousEncodeCarrier owner, IAudioOutput inner, int legIndex, bool combined)
        : IAudioOutput, IAudioOutputChannelCapabilities
    {
        public AudioFormat Format => inner.Format;
        public AudioOutputChannelCapabilities ChannelCapabilities =>
            inner is IAudioOutputChannelCapabilities capabilities
                ? capabilities.ChannelCapabilities
                : AudioOutputChannelCapabilities.Fixed(Format.Channels);

        public void Submit(ReadOnlySpan<float> packedSamples) =>
            owner.SubmitRealAudio(inner, legIndex, combined, packedSamples);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_handoffGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            thread = _thread;
            _thread = null;
        }

        try { thread?.Join(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        _cts.Dispose();
    }
}
