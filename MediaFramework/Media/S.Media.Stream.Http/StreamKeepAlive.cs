using System.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Stream.Http;

/// <summary>
/// Streams a valid BLACK video + SILENT audio signal at the stream's locked format from go-live, so a
/// client that connects before any media is routed sees a running stream instead of a stalled one. It
/// yields the moment real playback drives the sinks (the runtime flips <see cref="SetPlaybackActive"/>
/// on acquire/release), and resumes when playback stops - the stream never goes dead between tracks.
/// Runs on one background thread, paced to the target frame rate.
/// </summary>
internal sealed class StreamKeepAlive : IDisposable
{
    private readonly FFmpegEncodeSession _session;
    private readonly VideoFormat _format;
    private readonly bool _hasVideo;
    private readonly int _audioSampleRate;
    private readonly int _audioChannels;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _blackPlane;
    private readonly float[] _silence;
    private Thread? _thread;
    private volatile bool _videoActive;
    private volatile bool _audioActive;
    private long _ptsTicks;
    private bool _disposed;

    public StreamKeepAlive(FFmpegEncodeSession session, int width, int height, int fps, int audioSampleRate, int audioChannels)
    {
        _session = session;
        _hasVideo = width > 0 && height > 0;
        var w = Math.Max(2, width & ~1);
        var h = Math.Max(2, height & ~1);
        // The frame-rate clock paces both black frames AND silence chunks, so an audio-only stream still
        // needs a sane cadence - default to 30 fps when no video declares one.
        var rate = fps > 0 ? fps : 30;
        _format = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(rate, 1));
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;

        // Opaque black BGRA (B=G=R=0, A=255): a shared, reused buffer - the frames are read-only fillers.
        _blackPlane = _hasVideo ? new byte[w * 4 * h] : [];
        for (var i = 3; i < _blackPlane.Length; i += 4)
            _blackPlane[i] = 255;
        // One frame of interleaved silence at the mix rate.
        _silence = audioChannels > 0 ? new float[Math.Max(1, audioSampleRate / Math.Max(1, rate)) * audioChannels] : [];
    }

    public void SetPlaybackActive(bool videoActive, bool audioActive)
    {
        _videoActive = videoActive;
        _audioActive = audioActive;
    }

    public void Start()
    {
        // Configure the video sink at the locked format up front so the encoder output is fixed from
        // frame one - later real playback re-keys only the sws INPUT (the output stays locked). Audio-
        // only streams have no video sink to configure.
        if (_hasVideo)
            _session.VideoSink!.Configure(_format);
        _thread = new Thread(Run) { IsBackground = true, Name = "StreamKeepAlive" };
        _thread.Start();
    }

    private void Run()
    {
        var token = _cts.Token;
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _format.FrameRate.Numerator));
        var started = Stopwatch.GetTimestamp();
        long frame = 0;
        var combinedAudio = _session.CombinedAudioSink;

        while (!token.IsCancellationRequested)
        {
            // Only fill when playback ISN'T driving the sink (else we'd interleave black with real frames).
            if (_hasVideo && !_videoActive)
            {
                try
                {
                    var pts = TimeSpan.FromTicks(_ptsTicks);
                    _session.VideoSink!.Submit(new VideoFrame(pts, _format, [_blackPlane], [_format.Width * 4]));
                }
                catch
                {
                    // Session tearing down - stop filling.
                    break;
                }
            }

            // Advance the shared PTS clock every frame so black and real frames stay monotonic across the swap.
            _ptsTicks += frameInterval.Ticks;

            if (!_audioActive && combinedAudio is not null && _silence.Length > 0)
            {
                try { combinedAudio.Submit(_silence); }
                catch { break; }
            }

            frame++;
            // Pace to wall clock: sleep until this frame's scheduled time (real-time cadence for viewers).
            var sleepTicks = frame * frameInterval.Ticks - Stopwatch.GetElapsedTime(started).Ticks;
            if (sleepTicks > 0 && token.WaitHandle.WaitOne(TimeSpan.FromTicks(sleepTicks)))
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        try { _thread?.Join(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        _cts.Dispose();
    }
}
