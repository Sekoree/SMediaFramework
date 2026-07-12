using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Threading;
using S.Media.Encode.FFmpeg.Internal;
using S.Media.Encode.FFmpeg.Sinks;

namespace S.Media.Encode.FFmpeg;

/// <summary>
/// One encode-and-mux session: a video sink (<see cref="IVideoOutput"/>) and N audio sinks
/// (<see cref="IAudioOutput"/>, one per configured track) feed bounded queues; a single encode worker
/// thread owns every FFmpeg context, encodes, and fans the packets out to the session's packet sinks
/// (a file muxer today; push/broadcast sinks share the same encode in Phase 3). Submit never blocks on
/// FFmpeg: video drops oldest on overflow (counted), audio drops oldest chunks (counted).
///
/// <para>Timestamps: the first video frame's presentation time is the session baseline; later frames
/// are relative to it with a monotonic clamp (a backwards seek during recording continues one frame
/// ahead instead of rewinding the file). Audio counts output samples from the first chunk. Both legs
/// therefore start at ≈0 when the session is armed mid-playback.</para>
///
/// <para>Lifecycle: attach the sinks (router/pump wiring), <see cref="FinishAsync"/> to flush encoders
/// and write trailers, then <see cref="Dispose"/>. A sink that throws is detached and reported via
/// <see cref="GetMetrics"/> - one dead destination never kills the others.</para>
/// </summary>
public sealed unsafe class FFmpegEncodeSession : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Encode.FFmpeg.Session");

    private readonly EncodeSessionOptions _options;
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _work = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;

    private sealed record SinkSlot(IEncodedPacketSink Sink)
    {
        public bool Faulted;
        public string? Error;
    }

    private readonly List<SinkSlot> _sinks = [];
    private readonly FfmpegAudioEncoderCore[] _audioCores;
    private FfmpegVideoEncoderCore? _videoCore;
    private volatile bool _videoConfigured;
    private bool _streamsAnnounced;
    private bool _finishRequested;
    private bool _disposed;

    // Video intake (submit thread → worker).
    private readonly Queue<VideoFrame> _videoQueue = new();
    private const int MaxQueuedVideoFrames = 8;

    // Audio intake per leg (router pump thread → worker). Chunks are copied on Submit.
    private readonly Queue<float[]>[] _audioQueues;
    private readonly long[] _audioQueuedFloats;
    private readonly long[] _audioFloatCap;

    // Video PTS policy (worker-thread state).
    private long _videoBaseTicks = long.MinValue;
    private long _lastVideoPts90k = -1;
    private long _videoFrameDuration90k;

    // Metrics.
    private long _videoSubmitted;
    private long _videoEncoded;
    private long _videoDropped;
    private long _audioChunksDropped;
    private readonly TimingAccumulator _encodeTiming = new();

    private FFmpegEncodeSession(EncodeSessionOptions options, IReadOnlyList<IEncodedPacketSink> sinks, int audioInputSampleRate)
    {
        _options = options;
        foreach (var sink in sinks)
            _sinks.Add(new SinkSlot(sink));

        IReadOnlyList<AudioLegOptions> legs = options.IncludesAudio ? options.AudioLegs : [];
        _audioCores = new FfmpegAudioEncoderCore[legs.Count];
        _audioQueues = new Queue<float[]>[legs.Count];
        _audioQueuedFloats = new long[legs.Count];
        _audioFloatCap = new long[legs.Count];
        var audioSinks = new FFmpegEncodeAudioSink[legs.Count];
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var inputChannels = leg.Channels > 0 ? leg.Channels : 2;
            var inputFormat = new AudioFormat(audioInputSampleRate, inputChannels);
            _audioCores[i] = new FfmpegAudioEncoderCore(leg, inputFormat);
            _audioQueues[i] = new Queue<float[]>();
            _audioFloatCap[i] = (long)audioInputSampleRate * inputChannels * 5; // ~5 s backlog per leg
            audioSinks[i] = new FFmpegEncodeAudioSink(this, i, inputFormat);
        }

        AudioSinks = audioSinks;
        CombinedAudioSink = audioSinks.Length > 0
            ? new FFmpegEncodeCombinedAudioSink(this, audioSinks.Select(s => s.Format).ToArray())
            : null;

        if (options.IncludesVideo)
            VideoSink = new FFmpegEncodeVideoSink(this);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "FFmpegEncodeSession",
        };
        _worker.Start();
    }

    /// <summary>Creates a session writing to one destination. Options are validated (throws on errors).
    /// <paramref name="audioInputSampleRate"/> is the rate the audio router submits at (the mix rate).</summary>
    public static FFmpegEncodeSession Create(
        EncodeSessionOptions options,
        EncodeIoTarget target,
        int audioInputSampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid encode options: {string.Join(" | ", errors)}", nameof(options));

        return new FFmpegEncodeSession(options, [new MuxPacketSink(target, options.Container)], audioInputSampleRate);
    }

    /// <summary>Multi-destination session (live streaming): the same encode feeds every supplied sink.
    /// Callers wrap slow/network destinations in <see cref="AsyncPacketSink"/> themselves.</summary>
    internal static FFmpegEncodeSession CreateWithSinks(
        EncodeSessionOptions options,
        IReadOnlyList<IEncodedPacketSink> sinks,
        int audioInputSampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Count == 0)
            throw new ArgumentException("at least one packet sink is required.", nameof(sinks));
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid encode options: {string.Join(" | ", errors)}", nameof(options));

        return new FFmpegEncodeSession(options, sinks, audioInputSampleRate);
    }

    /// <summary>The video leg's router-attachable output (null for audio-only sessions). Wrap in a
    /// <see cref="S.Media.Routing"/> pump via the router as usual; the sink's own queue is small
    /// because the session worker drains it continuously.</summary>
    public FFmpegEncodeVideoSink? VideoSink { get; }

    /// <summary>One audio sink per configured track, in options order. Attach each to the audio router
    /// with the channel map that produces that track's layout.</summary>
    public IReadOnlyList<FFmpegEncodeAudioSink> AudioSinks { get; }

    /// <summary>Every track as one sink with concatenated channels (tracks route via the standard N→M
    /// channel matrix: combined ch 0..k-1 = track 1, k.. = track 2, …). Null for video-only sessions.</summary>
    public FFmpegEncodeCombinedAudioSink? CombinedAudioSink { get; }

    /// <summary>Completes when the trailer of every healthy sink is written.</summary>
    public Task Completion => _finished.Task;

    /// <summary>Stops intake, drains the queues, flushes the encoders, finalizes every sink.</summary>
    public Task FinishAsync()
    {
        lock (_gate)
        {
            if (!_finishRequested)
            {
                _finishRequested = true;
                _work.Set();
            }
        }

        return _finished.Task;
    }

    public FFmpegEncodeSessionMetrics GetMetrics()
    {
        int videoQueueDepth;
        long audioQueuedSamples = 0;
        lock (_gate)
        {
            videoQueueDepth = _videoQueue.Count;
            foreach (var q in _audioQueuedFloats)
                audioQueuedSamples += q;
        }

        List<EncodeSinkState> sinkStates;
        lock (_gate)
        {
            sinkStates = _sinks
                .Select(s => new EncodeSinkState(s.Sink.Name, !s.Faulted, s.Error, s.Sink.BytesWritten))
                .ToList();
        }

        return new FFmpegEncodeSessionMetrics(
            Volatile.Read(ref _videoSubmitted),
            Volatile.Read(ref _videoEncoded),
            Volatile.Read(ref _videoDropped),
            videoQueueDepth,
            MaxQueuedVideoFrames,
            audioQueuedSamples,
            Volatile.Read(ref _audioChunksDropped),
            _encodeTiming.Snapshot(),
            sinkStates);
    }

    // --- intake (called by the sinks) ---------------------------------------

    internal void SubmitVideo(VideoFrame frame)
    {
        var dropped = false;
        lock (_gate)
        {
            if (_disposed || _finishRequested)
            {
                frame.Dispose();
                return;
            }

            while (_videoQueue.Count >= MaxQueuedVideoFrames)
            {
                _videoQueue.Dequeue().Dispose();
                Interlocked.Increment(ref _videoDropped);
                dropped = true;
            }

            _videoQueue.Enqueue(frame);
            Interlocked.Increment(ref _videoSubmitted);
            _work.Set();
        }

        if (dropped)
            Trace.LogWarning("encode session: video queue full - dropped oldest (encoder can't keep up)");
    }

    private VideoFormat _videoInputFormat;

    internal void ConfigureVideo(VideoFormat format)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_videoCore is not null)
            {
                // FORMAT LOCK: the encoder's OUTPUT format is fixed at first configure (explicit scale
                // options, else derived from the first-seen input); a mid-recording INPUT change (track
                // switch resizing the auto-sized canvas, live source renegotiation) only re-keys the
                // sws conversion - the core scales each frame from the frame's own geometry, so nothing
                // else has to change. The file/stream stays one continuous, single-format stream.
                if (format != _videoInputFormat)
                {
                    _videoInputFormat = format;
                    if (format.FrameRate.Numerator > 0)
                        _videoFrameDuration90k = 90_000L * format.FrameRate.Denominator / format.FrameRate.Numerator;
                    Trace.LogInformation(
                        "encode session: input format changed to {Format} - output stays locked at {Locked}",
                        format, _videoCore.EncodedFormat);
                }

                return;
            }

            _videoCore = new FfmpegVideoEncoderCore(_options.Video, format);
            _videoInputFormat = format;
            _videoFrameDuration90k = format.FrameRate.Numerator > 0
                ? 90_000L * format.FrameRate.Denominator / format.FrameRate.Numerator
                : 3_000; // 30 fps fallback
            _videoConfigured = true;
            _work.Set();
        }
    }

    internal void SubmitAudio(int legIndex, ReadOnlySpan<float> packedSamples)
    {
        if (packedSamples.IsEmpty)
            return;
        var chunk = packedSamples.ToArray();
        var dropped = false;
        lock (_gate)
        {
            if (_disposed || _finishRequested)
                return;

            var queue = _audioQueues[legIndex];
            queue.Enqueue(chunk);
            _audioQueuedFloats[legIndex] += chunk.Length;
            while (_audioQueuedFloats[legIndex] > _audioFloatCap[legIndex] && queue.Count > 1)
            {
                var victim = queue.Dequeue();
                _audioQueuedFloats[legIndex] -= victim.Length;
                Interlocked.Increment(ref _audioChunksDropped);
                dropped = true;
            }

            _work.Set();
        }

        if (dropped)
            Trace.LogWarning("encode session: audio leg {Leg} backlog over cap - dropped oldest chunk(s)", legIndex);
    }

    // --- worker --------------------------------------------------------------

    private bool AllLegsReady => !_options.IncludesVideo || _videoConfigured;

    private void WorkerLoop()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _work.Wait(250, token);
                _work.Reset();

                if (!AllLegsReady)
                {
                    if (FinishWasRequested())
                        break; // finished before video ever configured - nothing to write
                    continue;
                }

                AnnounceStreamsOnce();
                PumpOnce();

                if (FinishWasRequested())
                {
                    PumpOnce(); // drain what arrived between the check and now
                    FlushAndFinalize();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed without FinishAsync - best-effort trailer below in Dispose
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "encode session worker faulted");
            _finished.TrySetException(ex);
            return;
        }

        _finished.TrySetResult();
    }

    private bool FinishWasRequested()
    {
        lock (_gate)
            return _finishRequested;
    }

    private void AnnounceStreamsOnce()
    {
        if (_streamsAnnounced)
            return;
        _streamsAnnounced = true;

        var streams = new List<EncodedStreamInfo>();
        if (_videoCore is { } video)
            streams.Add(new EncodedStreamInfo(EncodedStreamKind.Video, video.CodecParameters, video.TimeBase, null, null));
        for (var i = 0; i < _audioCores.Length; i++)
        {
            var leg = _options.AudioLegs[i];
            streams.Add(new EncodedStreamInfo(
                EncodedStreamKind.Audio, _audioCores[i].CodecParameters, _audioCores[i].TimeBase, leg.Name, leg.Language));
        }

        ForEachLiveSink(slot => slot.Sink.OnStreamsReady(streams));
    }

    private void PumpOnce()
    {
        // Video first (keeps mux interleaving happy), then each audio leg.
        while (true)
        {
            VideoFrame? frame = null;
            lock (_gate)
            {
                if (_videoQueue.Count > 0)
                    frame = _videoQueue.Dequeue();
            }

            if (frame is null)
                break;

            try
            {
                var pts = NextVideoPts(frame.PresentationTime);
                var started = Stopwatch.GetTimestamp();
                _videoCore!.Encode(frame, pts, pkt => FanOut((AVPacket*)pkt, streamIndex: 0));
                _encodeTiming.RecordSince(started);
                Interlocked.Increment(ref _videoEncoded);
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "encode session: video encode failed - frame skipped");
                Interlocked.Increment(ref _videoDropped);
            }
            finally
            {
                frame.Dispose();
            }
        }

        var audioBase = _videoCore is null ? 0 : 1;
        for (var i = 0; i < _audioCores.Length; i++)
        {
            while (true)
            {
                float[]? chunk = null;
                lock (_gate)
                {
                    if (_audioQueues[i].Count > 0)
                    {
                        chunk = _audioQueues[i].Dequeue();
                        _audioQueuedFloats[i] -= chunk.Length;
                    }
                }

                if (chunk is null)
                    break;

                var legStream = audioBase + i;
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    _audioCores[i].Submit(chunk, pkt => FanOut((AVPacket*)pkt, legStream));
                    _encodeTiming.RecordSince(started);
                }
                catch (Exception ex)
                {
                    Trace.LogError(ex, "encode session: audio leg {Leg} encode failed - chunk skipped", i);
                }
            }
        }
    }

    /// <summary>First frame anchors the baseline; afterwards PTS is relative with a monotonic clamp so
    /// a backwards seek mid-recording continues one frame ahead instead of rewriting the timeline.</summary>
    private long NextVideoPts(TimeSpan presentationTime)
    {
        if (_videoBaseTicks == long.MinValue)
            _videoBaseTicks = presentationTime.Ticks;

        var relTicks = presentationTime.Ticks - _videoBaseTicks;
        var pts = relTicks * 9 / 1000; // 100 ns ticks → 90 kHz
        if (pts <= _lastVideoPts90k)
            pts = _lastVideoPts90k + Math.Max(1, _videoFrameDuration90k);
        _lastVideoPts90k = pts;
        return pts;
    }

    private void FlushAndFinalize()
    {
        if (_videoCore is { } video)
        {
            try { video.Flush(pkt => FanOut((AVPacket*)pkt, 0)); }
            catch (Exception ex) { Trace.LogError(ex, "encode session: video flush failed"); }
        }

        var audioBase = _videoCore is null ? 0 : 1;
        for (var i = 0; i < _audioCores.Length; i++)
        {
            var legStream = audioBase + i;
            try { _audioCores[i].Flush(pkt => FanOut((AVPacket*)pkt, legStream)); }
            catch (Exception ex) { Trace.LogError(ex, "encode session: audio leg {Leg} flush failed", i); }
        }

        ForEachLiveSink(slot => slot.Sink.Finish());
        Trace.LogInformation("encode session finished: video {Encoded}/{Submitted} frames ({Dropped} dropped)",
            Volatile.Read(ref _videoEncoded), Volatile.Read(ref _videoSubmitted), Volatile.Read(ref _videoDropped));
    }

    private void FanOut(AVPacket* packet, int streamIndex)
    {
        packet->stream_index = streamIndex;
        var keyframe = (packet->flags & AV_PKT_FLAG_KEY) != 0;
        ForEachLiveSink(slot => slot.Sink.OnPacket(packet, keyframe));
    }

    private void ForEachLiveSink(Action<SinkSlot> action)
    {
        foreach (var slot in _sinks)
        {
            if (slot.Faulted)
                continue;
            try
            {
                action(slot);
            }
            catch (Exception ex)
            {
                slot.Faulted = true;
                slot.Error = ex.Message;
                Trace.LogError(ex, "encode sink '{Sink}' faulted - detached; remaining sinks continue", slot.Sink.Name);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _finishRequested = true;
            _work.Set();
        }

        CooperativePlaybackJoin.JoinThread(_worker, TimeSpan.FromSeconds(5), CancellationToken.None);
        if (_worker.IsAlive)
        {
            _cts.Cancel();
            CooperativePlaybackJoin.JoinThread(_worker, TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        _finished.TrySetResult();
        MediaDiagnostics.SwallowDisposeErrors(() => _videoCore?.Dispose(), "FFmpegEncodeSession.Dispose: video core");
        foreach (var core in _audioCores)
            MediaDiagnostics.SwallowDisposeErrors(core.Dispose, "FFmpegEncodeSession.Dispose: audio core");
        foreach (var slot in _sinks)
            MediaDiagnostics.SwallowDisposeErrors(slot.Sink.Dispose, "FFmpegEncodeSession.Dispose: sink");
        lock (_gate)
        {
            while (_videoQueue.Count > 0)
                _videoQueue.Dequeue().Dispose();
            foreach (var q in _audioQueues)
                q.Clear();
        }

        _cts.Dispose();
        _work.Dispose();
    }
}

/// <summary>Health/state of one packet destination.</summary>
public sealed record EncodeSinkState(string Name, bool Healthy, string? Error, long BytesWritten);

/// <summary>One-call snapshot for HUDs and the debug-stats page.</summary>
public sealed record FFmpegEncodeSessionMetrics(
    long VideoFramesSubmitted,
    long VideoFramesEncoded,
    long VideoFramesDropped,
    int VideoQueueDepth,
    int VideoQueueCapacity,
    long AudioQueuedFloats,
    long AudioChunksDropped,
    S.Media.Core.Diagnostics.TimingSnapshot EncodeTiming,
    IReadOnlyList<EncodeSinkState> Sinks);
