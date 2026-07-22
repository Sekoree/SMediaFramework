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
/// ahead instead of rewinding the file). Audio tracks absolute submitted input-frame positions, so a
/// bounded-queue drop creates a timestamp gap rather than silently compressing the audio timeline.
/// Both legs start at ≈0 when the session is armed mid-playback.</para>
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

    // Video intake (submit thread → worker). Source frames use their media PTS, continuation frames
    // are placed directly after the encode cursor, and live-wall-clock frames use their intake instant.
    // The last mode is important for a continuously running carrier: a paused compositor may keep
    // producing fresh black/held frames with one frozen media PTS, but those are still distinct live
    // output frames and must not collapse to a single encoded frame.
    private enum VideoTimelineMode
    {
        Source,
        Continuation,
        LiveWallClock,
    }

    private sealed record QueuedVideoFrame(
        VideoFrame Frame,
        VideoTimelineMode TimelineMode,
        long LiveCaptureTimestamp = 0);
    private readonly Queue<QueuedVideoFrame> _videoQueue = new();
    private const int MaxQueuedVideoFrames = 8;

    // Audio intake per leg (router pump thread → worker). The absolute input-frame position survives
    // queue overflow, so the encoder can represent dropped time instead of compressing the timeline.
    // Samples is RENTED from ArrayPool (review P2-3: 100 exact-size arrays/second/leg was constant
    // Gen0 churn on long recordings) - Length is the valid float count; return via ReturnChunk on
    // every exit path (encoded, dropped, or disposed).
    private sealed record QueuedAudioChunk(float[] Samples, int Length, long StartFrame);

    private static void ReturnChunk(QueuedAudioChunk chunk) =>
        System.Buffers.ArrayPool<float>.Shared.Return(chunk.Samples);
    private readonly Queue<QueuedAudioChunk>[] _audioQueues;
    private readonly long[] _audioQueuedFloats;
    private readonly long[] _audioFloatCap;
    private readonly long[] _audioSubmittedFrames;
    private readonly int[] _audioInputChannels;

    // Video PTS policy (worker-thread state).
    private long _videoBaseTicks = long.MinValue;
    private long _videoBasePts90k;
    private long _lastVideoSourceTicks = long.MinValue;
    private bool _sourceTimelineNeedsReanchor;
    private long _lastVideoPts90k = -1;
    private long _videoFrameDuration90k;
    // Exact rational stepping for generated/re-anchored frames (review P3-2): 60000/1001 needs
    // 1501.5 ticks per frame, so repeatedly adding the truncated integer duration loses half a tick
    // per generated frame. The remainder accumulator carries the fraction across steps.
    private int _videoFrameRateNum;
    private int _videoFrameRateDen;
    private long _videoStepRemainder;
    // Review H7 - target-rate scheduler: when options declare a fixed FPS, frames are selected/dropped
    // per target tick (fast input) and the last frame is re-encoded into gaps (slow input), so the
    // output really carries the configured cadence instead of just advertising it in metadata.
    private readonly long _targetVideoFrameDuration90k;
    private long _videoBaseTick;
    private long _lastVideoTick = -1;
    private VideoFrame? _heldVideoFrame;
    private long _liveVideoBaseTimestamp = long.MinValue;
    private long _liveVideoBaseTick;

    // Metrics.
    private long _videoSubmitted;
    private long _videoEncoded;
    private long _videoDropped;
    private long _audioChunksDropped;
    private readonly TimingAccumulator _encodeTiming = new();

    private FFmpegEncodeSession(EncodeSessionOptions options, IReadOnlyList<IEncodedPacketSink> sinks, int audioInputSampleRate)
    {
        _options = options;
        _targetVideoFrameDuration90k = options.IncludesVideo && options.Video.Fps > 0
            ? 90_000L / Math.Max(1, options.Video.Fps)
            : 0;
        foreach (var sink in sinks)
            _sinks.Add(new SinkSlot(sink));

        IReadOnlyList<AudioLegOptions> legs = options.IncludesAudio ? options.AudioLegs : [];
        _audioCores = new FfmpegAudioEncoderCore[legs.Count];
        _audioQueues = new Queue<QueuedAudioChunk>[legs.Count];
        _audioQueuedFloats = new long[legs.Count];
        _audioFloatCap = new long[legs.Count];
        _audioSubmittedFrames = new long[legs.Count];
        _audioInputChannels = new int[legs.Count];
        var audioSinks = new FFmpegEncodeAudioSink[legs.Count];
        try
        {
            for (var i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                var inputChannels = leg.Channels > 0 ? leg.Channels : 2;
                var inputFormat = new AudioFormat(audioInputSampleRate, inputChannels);
                _audioCores[i] = new FfmpegAudioEncoderCore(leg, inputFormat);
                _audioQueues[i] = new Queue<QueuedAudioChunk>();
                _audioInputChannels[i] = inputChannels;
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
        catch
        {
            foreach (var core in _audioCores)
                core?.Dispose();
            foreach (var slot in _sinks)
                slot.Sink.Dispose();
            _cts.Dispose();
            _work.Dispose();
            throw;
        }
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
        var errors = options.Validate(audioInputSampleRate: audioInputSampleRate);
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
        var errors = options.Validate(audioInputSampleRate: audioInputSampleRate);
        if (errors.Count > 0)
        {
            // This overload is the ownership-transfer seam used by live fan-out. Once called, the
            // caller never has to guess whether validation or native construction failed first.
            foreach (var sink in sinks)
                sink.Dispose();
            throw new ArgumentException($"Invalid encode options: {string.Join(" | ", errors)}", nameof(options));
        }

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
        List<EncodeSinkState> sinkStates;
        lock (_gate)
        {
            videoQueueDepth = _videoQueue.Count;
            foreach (var q in _audioQueuedFloats)
                audioQueuedSamples += q;
            sinkStates = _sinks.Select(s =>
            {
                var healthy = !s.Faulted;
                var error = s.Error;
                if (healthy && s.Sink is IEncodedPacketSinkHealth liveHealth)
                {
                    healthy = liveHealth.Healthy;
                    error = liveHealth.Error;
                }

                return new EncodeSinkState(s.Sink.Name, healthy, error, s.Sink.BytesWritten);
            }).ToList();
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

    internal void SubmitVideo(VideoFrame frame, bool continueTimeline = false) =>
        SubmitVideoCore(
            frame,
            continueTimeline ? VideoTimelineMode.Continuation : VideoTimelineMode.Source,
            liveCaptureTimestamp: 0);

    /// <summary>
    /// Enqueues one frame on the live carrier timeline. Its capture instant, rather than its media PTS,
    /// selects the fixed-rate output tick. This keeps live video continuous across pause/stop and source
    /// timeline resets while retaining normal source-time scheduling for recordings.
    /// </summary>
    internal void SubmitLiveVideo(VideoFrame frame) =>
        SubmitVideoCore(frame, VideoTimelineMode.LiveWallClock, Stopwatch.GetTimestamp());

    private void SubmitVideoCore(
        VideoFrame frame,
        VideoTimelineMode timelineMode,
        long liveCaptureTimestamp)
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
                _videoQueue.Dequeue().Frame.Dispose();
                Interlocked.Increment(ref _videoDropped);
                dropped = true;
            }

            _videoQueue.Enqueue(new QueuedVideoFrame(frame, timelineMode, liveCaptureTimestamp));
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
                        SetVideoFrameDuration(format.FrameRate.Numerator, format.FrameRate.Denominator);
                    Trace.LogInformation(
                        "encode session: input format changed to {Format} - output stays locked at {Locked}",
                        format, _videoCore.EncodedFormat);
                }

                return;
            }

            _videoCore = new FfmpegVideoEncoderCore(
                _options.Video,
                format,
                enableConstantBitrateFiller: _options.Container is EncodeContainer.MpegTs or EncodeContainer.Flv);
            _videoInputFormat = format;
            if (format.FrameRate.Numerator > 0)
                SetVideoFrameDuration(format.FrameRate.Numerator, format.FrameRate.Denominator);
            else
                SetVideoFrameDuration(30, 1); // 30 fps fallback
            _videoConfigured = true;
            _work.Set();
        }
    }

    internal void SubmitAudio(int legIndex, ReadOnlySpan<float> packedSamples)
    {
        if (packedSamples.IsEmpty)
            return;
        var channels = _audioInputChannels[legIndex];
        if (packedSamples.Length % channels != 0)
            throw new ArgumentException(
                $"packedSamples length {packedSamples.Length} is not a multiple of channel count {channels}.",
                nameof(packedSamples));
        var rented = System.Buffers.ArrayPool<float>.Shared.Rent(packedSamples.Length);
        packedSamples.CopyTo(rented);
        var chunk = new QueuedAudioChunk(rented, packedSamples.Length, StartFrame: 0);
        var dropped = false;
        lock (_gate)
        {
            if (_disposed || _finishRequested)
            {
                ReturnChunk(chunk);
                return;
            }

            var queue = _audioQueues[legIndex];
            var startFrame = _audioSubmittedFrames[legIndex];
            _audioSubmittedFrames[legIndex] += chunk.Length / channels;
            queue.Enqueue(chunk with { StartFrame = startFrame });
            _audioQueuedFloats[legIndex] += chunk.Length;
            while (_audioQueuedFloats[legIndex] > _audioFloatCap[legIndex] && queue.Count > 1)
            {
                var victim = queue.Dequeue();
                _audioQueuedFloats[legIndex] -= victim.Length;
                ReturnChunk(victim);
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
            // Dispose without FinishAsync is an abort: queued frames are released and sinks are
            // disposed by Dispose after this worker exits. Graceful flush/trailer belongs to FinishAsync.
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
            streams.Add(new EncodedStreamInfo(
                EncodedStreamKind.Video, video.CodecParameters, video.TimeBase, video.FrameRate, null, null));
        for (var i = 0; i < _audioCores.Length; i++)
        {
            var leg = _options.AudioLegs[i];
            streams.Add(new EncodedStreamInfo(
                EncodedStreamKind.Audio, _audioCores[i].CodecParameters, _audioCores[i].TimeBase,
                default, leg.Name, leg.Language));
        }

        ForEachLiveSink(slot => slot.Sink.OnStreamsReady(streams));
    }

    private void PumpOnce()
    {
        // Weighted round-robin: one video frame, then a bounded catch-up batch from EVERY audio leg.
        // The old "drain all video, then audio" order could starve AAC forever when a 60 fps producer
        // kept the video queue non-empty and the encoder ran below real time. Audio then hit its 5 s cap,
        // acquired timestamp gaps and sounded sped-up/discontinuous at the receiver. Audio encoding is
        // cheap, so 32 chunks per leg lets it catch up without withholding video indefinitely.
        while (true)
        {
            var didWork = TryPumpOneVideo();
            for (var i = 0; i < _audioCores.Length; i++)
                didWork |= PumpAudioBatch(i, maxChunks: 32) > 0;
            if (!didWork)
                break;
        }
    }

    private bool TryPumpOneVideo()
    {
        QueuedVideoFrame? queued = null;
        lock (_gate)
        {
            if (_videoQueue.Count > 0)
                queued = _videoQueue.Dequeue();
        }

        if (queued is null)
            return false;

        if (_targetVideoFrameDuration90k > 0)
        {
            EncodeAtTargetRate(
                queued.Frame,
                queued.TimelineMode,
                queued.LiveCaptureTimestamp); // takes ownership
            return true;
        }

        try
        {
            var pts = NextVideoPts(
                queued.Frame.PresentationTime,
                queued.TimelineMode != VideoTimelineMode.Source);
            var started = Stopwatch.GetTimestamp();
            _videoCore!.Encode(queued.Frame, pts, pkt => FanOut((AVPacket*)pkt, streamIndex: 0));
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
            queued.Frame.Dispose();
        }

        return true;
    }

    private int PumpAudioBatch(int legIndex, int maxChunks)
    {
        var processed = 0;
        var audioBase = _videoCore is null ? 0 : 1;
        while (processed < maxChunks)
        {
            QueuedAudioChunk? chunk = null;
            lock (_gate)
            {
                if (_audioQueues[legIndex].Count > 0)
                {
                    chunk = _audioQueues[legIndex].Dequeue();
                    _audioQueuedFloats[legIndex] -= chunk.Length;
                }
            }

            if (chunk is null)
                break;

            var legStream = audioBase + legIndex;
            try
            {
                var started = Stopwatch.GetTimestamp();
                _audioCores[legIndex].Submit(
                    chunk.Samples.AsSpan(0, chunk.Length), chunk.StartFrame,
                    pkt => FanOut((AVPacket*)pkt, legStream));
                _encodeTiming.RecordSince(started);
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "encode session: audio leg {Leg} encode failed - chunk skipped", legIndex);
            }
            finally
            {
                ReturnChunk(chunk);
            }

            processed++;
        }

        return processed;
    }

    private void SetVideoFrameDuration(int rateNum, int rateDen)
    {
        _videoFrameRateNum = rateNum;
        _videoFrameRateDen = rateDen;
        _videoFrameDuration90k = 90_000L * rateDen / rateNum;
        _videoStepRemainder = 0;
    }

    /// <summary>One exact frame step in 90 kHz ticks: the integer part now, the fractional part
    /// accumulated so it lands as a whole tick every few frames (no drift at 60000/1001-style rates).</summary>
    private long NextVideoStep90k()
    {
        if (_videoFrameRateNum <= 0)
            return Math.Max(1, _videoFrameDuration90k);
        return AccumulateStep90k(_videoFrameRateNum, _videoFrameRateDen, ref _videoStepRemainder);
    }

    /// <summary>Pure step accumulator (unit-tested): emits integer 90 kHz steps whose SUM over N calls
    /// is exactly floor(N × 90000 × den / num) - zero long-run drift for any rational rate.</summary>
    internal static long AccumulateStep90k(int rateNum, int rateDen, ref long remainder)
    {
        var totalNumerator = 90_000L * rateDen + remainder;
        var step = totalNumerator / rateNum;
        remainder = totalNumerator % rateNum;
        return Math.Max(1, step);
    }

    /// <summary>First frame anchors the baseline; afterwards PTS is relative with a monotonic clamp so
    /// a backwards seek mid-recording continues one frame ahead instead of rewriting the timeline.</summary>
    private long NextVideoPts(TimeSpan presentationTime, bool continueTimeline)
    {
        if (continueTimeline)
        {
            _sourceTimelineNeedsReanchor = true;
            _lastVideoPts90k = _lastVideoPts90k < 0 ? 0 : _lastVideoPts90k + NextVideoStep90k();
            return _lastVideoPts90k;
        }

        var sourceTicks = presentationTime.Ticks;
        if (ShouldReanchorSource(sourceTicks, Math.Max(1, _videoFrameDuration90k)))
        {
            _videoBaseTicks = sourceTicks;
            _videoBasePts90k = _lastVideoPts90k < 0 ? 0 : _lastVideoPts90k + NextVideoStep90k();
            _sourceTimelineNeedsReanchor = false;
        }

        _lastVideoSourceTicks = sourceTicks;
        var relTicks = sourceTicks - _videoBaseTicks;
        var pts = _videoBasePts90k + relTicks * 9 / 1000; // 100 ns ticks → 90 kHz
        if (pts <= _lastVideoPts90k)
            pts = _lastVideoPts90k + NextVideoStep90k();
        _lastVideoPts90k = pts;
        return pts;
    }

    /// <summary>Review H7: encodes one submitted frame onto the FIXED target timebase. Each target tick
    /// (1/fps) carries exactly one frame: faster input drops frames whose tick is already covered,
    /// slower input re-encodes the last frame into the gap (capped at one second per gap - a seek/stall
    /// jumps rather than emitting thousands of duplicates). Owns <paramref name="frame"/>: it is HELD
    /// until the next frame replaces it (gap filling needs it), then disposed.</summary>
    private void EncodeAtTargetRate(
        VideoFrame frame,
        VideoTimelineMode timelineMode,
        long liveCaptureTimestamp)
    {
        var dur = _targetVideoFrameDuration90k;
        long tick;
        if (timelineMode == VideoTimelineMode.LiveWallClock)
        {
            if (_liveVideoBaseTimestamp == long.MinValue)
            {
                _liveVideoBaseTimestamp = liveCaptureTimestamp;
                _liveVideoBaseTick = _lastVideoTick + 1;
            }

            // Round to the nearest configured output tick. The submitter is paced to that same
            // cadence, so small wake-up jitter neither invents nor loses a frame; a real stall forms
            // a gap which the held-frame logic below fills just like a slow source.
            var elapsedTimestamp = Math.Max(0, liveCaptureTimestamp - _liveVideoBaseTimestamp);
            var elapsed90k = elapsedTimestamp * 90_000d / Stopwatch.Frequency;
            tick = _liveVideoBaseTick + (long)Math.Round(elapsed90k / dur);
        }
        else if (timelineMode == VideoTimelineMode.Continuation)
        {
            _sourceTimelineNeedsReanchor = true;
            tick = _lastVideoTick + 1;
        }
        else
        {
            var sourceTicks = frame.PresentationTime.Ticks;
            if (ShouldReanchorSource(sourceTicks, dur))
            {
                _videoBaseTicks = sourceTicks;
                _videoBaseTick = _lastVideoTick + 1;
                _sourceTimelineNeedsReanchor = false;
            }

            _lastVideoSourceTicks = sourceTicks;
            var srcPts90k = (sourceTicks - _videoBaseTicks) * 9 / 1000;
            tick = _videoBaseTick + (long)Math.Round(srcPts90k / (double)dur);
        }

        if (tick <= _lastVideoTick)
        {
            // Input runs faster than the target (60 in, 30 configured): this tick already has a frame.
            frame.Dispose();
            Interlocked.Increment(ref _videoDropped);
            return;
        }

        // Input runs slower (15 in, 30 configured): hold-duplicate the previous frame into the gap.
        var gapStart = _lastVideoTick + 1;
        if (_heldVideoFrame is { } held && tick > gapStart)
        {
            var maxDup = Math.Max(1, 90_000 / dur); // ≤ 1 s of duplicates per gap
            var dupEnd = Math.Min(tick - 1, gapStart + maxDup - 1);
            for (var t = gapStart; t <= dupEnd; t++)
                EncodeAtTick(held, t, dur);
        }

        EncodeAtTick(frame, tick, dur);
        _lastVideoTick = tick;
        _heldVideoFrame?.Dispose();
        _heldVideoFrame = frame; // retained for the next gap; swapped out above
    }

    private static long VideoDurationTicks(long duration90k) =>
        Math.Max(1, duration90k * TimeSpan.TicksPerSecond / 90_000L);

    /// <summary>Whether the source PTS baseline must be re-anchored for the incoming frame: the first
    /// frame, a pending continuation reset, or a backward seek beyond half a frame. Shared by the
    /// fixed-rate and source-following schedulers so their seek-detection policy can never drift apart.</summary>
    private bool ShouldReanchorSource(long sourceTicks, long duration90k) =>
        _videoBaseTicks == long.MinValue
        || _sourceTimelineNeedsReanchor
        || (_lastVideoSourceTicks != long.MinValue
            && sourceTicks < _lastVideoSourceTicks - VideoDurationTicks(duration90k) / 2);

    private void EncodeAtTick(VideoFrame frame, long tick, long dur)
    {
        try
        {
            var pts = tick * dur;
            var started = Stopwatch.GetTimestamp();
            _videoCore!.Encode(frame, pts, pkt => FanOut((AVPacket*)pkt, streamIndex: 0));
            _encodeTiming.RecordSince(started);
            _lastVideoPts90k = pts; // keeps the monotonic clamp coherent if paths ever mix
            Interlocked.Increment(ref _videoEncoded);
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "encode session: video encode failed - frame skipped");
            Interlocked.Increment(ref _videoDropped);
        }
    }

    private void FlushAndFinalize()
    {
        _heldVideoFrame?.Dispose();
        _heldVideoFrame = null;
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

        if (_worker.IsAlive)
        {
            // Review H6: the encode worker is wedged inside FFmpeg (stalled encoder/destination). Freeing
            // codec contexts, sinks, queued frames, or the CTS/event under it is a native use-after-free.
            // Mark terminally stuck and RETAIN the native state (deliberate leak) - the same policy the
            // router/player pumps use for a stuck terminal.
            MediaDiagnostics.LogWarning(
                "FFmpegEncodeSession.Dispose: encode worker still alive after join - retaining native state (terminally stuck, leaked).");
            _finished.TrySetException(new TimeoutException(
                "The FFmpeg encode worker did not stop; native state was retained to avoid use-after-free."));
            return;
        }

        _finished.TrySetResult();
        MediaDiagnostics.SwallowDisposeErrors(() => _heldVideoFrame?.Dispose(), "FFmpegEncodeSession.Dispose: held frame");
        _heldVideoFrame = null;
        MediaDiagnostics.SwallowDisposeErrors(() => _videoCore?.Dispose(), "FFmpegEncodeSession.Dispose: video core");
        foreach (var core in _audioCores)
            MediaDiagnostics.SwallowDisposeErrors(core.Dispose, "FFmpegEncodeSession.Dispose: audio core");
        foreach (var slot in _sinks)
            MediaDiagnostics.SwallowDisposeErrors(slot.Sink.Dispose, "FFmpegEncodeSession.Dispose: sink");
        lock (_gate)
        {
            while (_videoQueue.Count > 0)
                _videoQueue.Dequeue().Frame.Dispose();
            foreach (var q in _audioQueues)
            {
                while (q.Count > 0)
                    ReturnChunk(q.Dequeue());
            }
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
