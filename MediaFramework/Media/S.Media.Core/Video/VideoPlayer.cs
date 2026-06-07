using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Glues an <see cref="IVideoSource"/>, an <see cref="IVideoOutput"/>, and an
/// <see cref="IMediaClock"/> together. A background decode thread keeps a
/// small presentation queue full; on each <see cref="IMediaClock.VideoTick"/>
/// the player picks the most recent frame whose PTS is at or before the
/// playhead, drops anything stale, and submits to the output.
/// </summary>
/// <remarks>
/// <para>
/// Pacing model: the master clock decides when frames display; the source
/// just provides them in PTS order. For audio-mastered playback this means
/// the audio output's <see cref="IPlaybackClock"/> drives the
/// <see cref="MediaClock"/>, and the video player follows along. With no
/// master attached (free-running stopwatch), wall time paces the playback.
/// </para>
/// <para>
/// Thread safety / latency: <see cref="IVideoOutput.Submit"/> runs on the
/// <see cref="IMediaClock.VideoTick"/> thread (the clock's driver thread),
/// so outputs must return promptly — typically by handing the frame to a
/// render thread of their own. A slow Submit delays subsequent ticks for
/// every other subscriber on the same clock.
/// </para>
/// <para>
/// Frame negotiation runs at construction via
/// <see cref="VideoFormatNegotiator.Connect"/> (optional
/// <c>negotiatePixelFormats</c> predicate excludes pixel formats Core would
/// otherwise pick). The source and output agree on a format before <see cref="Play"/>.
/// </para>
/// <para>
/// On <see cref="Pause"/> / <see cref="Dispose"/>, when the <see cref="IVideoSource"/> implements
/// <see cref="ICooperativeVideoReadInterrupt"/>, a yield is requested before cancelling the decode loop so
/// blocking reads (for example shared-demux FFmpeg) can return <c>false</c> promptly.
/// The decode thread may be waiting on the presentation-queue semaphore when ticks stop; <see cref="Pause"/>
/// drains the queue and recreates that semaphore before joining decode so shutdown cannot deadlock on a full queue.
/// </para>
/// </remarks>
public sealed class VideoPlayer : IDisposable
{
    private readonly IVideoSource _source;
    private readonly IVideoOutput _sink;
    private readonly IMediaClock _clock;
    private readonly ConcurrentQueue<VideoFrame> _queue = new();
    private SemaphoreSlim _slotsAvailable;
    private readonly int _queueCapacity;
    private readonly TimeSpan _decodePollInterval;
    private readonly VideoPresentationMode _presentationMode;
    private readonly Lock _gate = new();

    private Thread? _decodeThread;
    private CancellationTokenSource? _cts;
    private EventHandler? _tickHandler;
    private bool _isRunning;
    private bool _disposed;
    private volatile Exception? _fault;
    /// <summary>Set when a decode thread failed to exit within the join cap (blocked in a native read).
    /// A restart would create a second decode thread sharing this source/queue, so <see cref="Play"/>
    /// is refused once this is set — the player is terminally stuck and must be disposed.</summary>
    private volatile bool _decodeThreadStuck;

    private long _decoded;
    private long _latestDecodedPtsTicks;
    private long _displayed;
    private long _droppedLate;
    private long _droppedQueueDrain;
    private long _lastPresentedPtsTicks;
    private int _firstTickLogged;
    private int _firstSubmittedLogged;
    private int _firstDecodedLogged;
    private int _syncDebugTicksRemaining;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoPlayer");
    private static readonly TimeSpan SyncOutputPreDrainTimeout = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Frozen plane data captured from the final submitted frame when <see cref="HoldLastFrameAtEnd"/> is on.
    /// Read/written only on the clock-driver thread inside <see cref="OnVideoTick"/>.
    /// </summary>
    private ReadOnlyMemory<byte>[]? _heldPlanes;
    private int[]? _heldStrides;
    private VideoFormat _heldFormat;
    private VideoTransferHint _heldTransferHint;
    private TimeSpan _heldOriginalPts;
    private long _holdSubmitCount;

    /// <summary>
    /// Raised after a frame is successfully submitted to the output with its
    /// <see cref="VideoFrame.PresentationTime"/> (for example to feed a
    /// <see cref="VideoPtsClock"/>).
    /// </summary>
    /// <remarks>Runs on the <see cref="IMediaClock"/> driver thread (same thread as <see cref="IMediaClock.VideoTick"/>). Keep handlers lightweight or marshal work elsewhere.</remarks>
    public event Action<TimeSpan>? FramePresentationTimePresented;

    /// <summary>
    /// Raised when the decode loop hits an unhandled source/conversion error and stops.
    /// The player becomes terminal; dispose it and create a new instance to recover.
    /// </summary>
    public event EventHandler<VideoPlayerFaultedEventArgs>? Faulted;

    /// <summary>The clock that paces <see cref="IMediaClock.VideoTick"/> and <see cref="Play"/>.</summary>
    public IMediaClock Clock => _clock;

    public VideoFormat Format => _sink.Format;
    public bool IsRunning { get { lock (_gate) return _isRunning; } }
    /// <summary>Non-null after the decode loop faulted or shutdown left a live decode thread behind.</summary>
    public Exception? Fault => _fault;

    /// <summary>True after the source signalled <see cref="IVideoSource.IsExhausted"/> AND every queued frame has been displayed.</summary>
    public bool CompletedNaturally { get; private set; }

    /// <summary>Total frames pulled from the source.</summary>
    public long DecodedCount => Volatile.Read(ref _decoded);
    /// <summary>Total frames handed to the output.</summary>
    public long DisplayedCount => Volatile.Read(ref _displayed);
    /// <summary>
    /// Lifetime decoded minus displayed (includes frames dropped on pause/seek drain).
    /// Prefer <see cref="QueuedFrameCount"/> for jitter-buffer depth.
    /// </summary>
    public long PendingBufferedCount => DecodedCount - DisplayedCount;

    /// <summary>Frames currently waiting in the presentation queue.</summary>
    public int QueuedFrameCount => _queue.Count;
    /// <summary>PTS of the most recently decoded frame still in (or at the tail of) the jitter buffer.</summary>
    public TimeSpan LatestDecodedPresentationTime =>
        TimeSpan.FromTicks(Volatile.Read(ref _latestDecodedPtsTicks));
    /// <summary>PTS last handed to the output (after a successful <see cref="IVideoOutput.Submit"/>).</summary>
    public TimeSpan LastPresentedPresentationTime =>
        TimeSpan.FromTicks(Volatile.Read(ref _lastPresentedPtsTicks));
    /// <summary>Frames dropped because the playhead had advanced past them by more than <see cref="LateThreshold"/>.</summary>
    public long DroppedLate => Volatile.Read(ref _droppedLate);
    /// <summary>Frames dropped on Stop/Pause/Seek (queue drain) before they could be displayed.</summary>
    public long DroppedDrain => Volatile.Read(ref _droppedQueueDrain);

    /// <summary>
    /// A frame whose PTS is more than this far behind the playhead is
    /// dropped instead of submitted. Default 150 ms.
    /// </summary>
    public TimeSpan LateThreshold { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// A frame whose PTS is at most this far ahead of the playhead is still
    /// considered ready to display. Default 8 ms (~half a 60 Hz tick).
    /// </summary>
    public TimeSpan EarlyTolerance { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// A/V-sync compensation subtracted from the clock before scheduling video (Scheduled mode only).
    /// The audio output buffers some samples ahead of the speakers (e.g. a PortAudio ring), so audio is
    /// HEARD a fixed latency after the clock says "now". Video, presented immediately at clock time, would
    /// then lead the audio by that latency. Set this to the output's buffered latency to hold video back so
    /// it lines up with what's actually heard. Default zero (no compensation). Read on the clock's video
    /// tick thread; setting it is a cheap volatile-ish write — safe to update live.
    /// </summary>
    public TimeSpan PlayheadOffset { get; set; }

    /// <summary>
    /// When <c>true</c>, the player freezes the last successfully submitted frame's plane data and
    /// keeps re-submitting it on every <see cref="IMediaClock.VideoTick"/> after the source is
    /// exhausted, instead of going dark and signalling <see cref="CompletedNaturally"/>. Use for
    /// image cues, "hold the final frame" transitions, and NDI senders that need a frame stream to
    /// keep their session alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only CPU-backed frames are supported today — hardware backings (DMA-BUF NV12/P010/P016,
    /// Win32 D3D11 shared NV12) fall through to natural completion since their lifetime is bound
    /// to the source's release callback. Wrap the source in a CPU-readback path (or pre-decode
    /// images via SkiaSharp) if you need hold-last with HW frames.
    /// </para>
    /// <para>
    /// Setting this to <c>false</c> after a hold has started releases the captured planes on the
    /// next tick; calling <see cref="Play"/> or <see cref="Seek"/> also resets the held state.
    /// Held frames advance their PTS with the playhead so the output keeps receiving monotonically
    /// timestamped frames (matches the live-decode contract).
    /// </para>
    /// </remarks>
    public bool HoldLastFrameAtEnd { get; set; }

    /// <summary>Number of held frames re-submitted since the last hold transition. Diagnostic / test hook.</summary>
    public long HeldFrameSubmitCount => Volatile.Read(ref _holdSubmitCount);

    /// <summary>True when the player has frozen a final frame and is re-submitting it under <see cref="HoldLastFrameAtEnd"/>.</summary>
    public bool IsHoldingLastFrame => _heldPlanes is not null;

    public VideoPlayer(IVideoSource source, IVideoOutput output, IMediaClock clock,
                       int queueCapacity = 4, TimeSpan? decodePollInterval = null,
                       Func<PixelFormat, bool>? negotiatePixelFormats = null,
                       VideoPresentationMode presentationMode = VideoPresentationMode.Scheduled)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity), "must be >= 1");

        _source = source;
        _sink = output;
        _clock = clock;
        _presentationMode = presentationMode;
        _queueCapacity = queueCapacity;
        _slotsAvailable = new SemaphoreSlim(queueCapacity, queueCapacity);
        _decodePollInterval = decodePollInterval ?? TimeSpan.FromMilliseconds(5);

        // Negotiate format up front so the output is configured before any
        // frame arrives. Either side throws here if no compatible format.
        VideoFormatNegotiator.Connect(_source, _sink, negotiatePixelFormats);
        _sink.Format.Validate(nameof(output));
    }

    /// <summary>Start decoding and scheduling. Idempotent.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_decodeThreadStuck)
                throw new InvalidOperationException(
                    "VideoPlayer cannot restart: a previous decode thread is still blocked in a native read and never exited. " +
                    "Dispose this player and create a new one.");
            if (_fault is not null)
                throw new InvalidOperationException(
                    "VideoPlayer cannot restart after a decode fault. Dispose this player and create a new one.",
                    _fault);
            if (_isRunning)
            {
                Trace.LogTrace("Play: already running");
                return;
            }
            CompletedNaturally = false;
            ReleaseHeldFrame();
            Interlocked.Exchange(ref _firstTickLogged, 0);
            Interlocked.Exchange(ref _firstSubmittedLogged, 0);
            Interlocked.Exchange(ref _firstDecodedLogged, 0);
            _syncDebugTicksRemaining = Trace.IsEnabled(LogLevel.Debug) ? 90 : 0;
            if (_source is ICooperativeVideoReadInterrupt iv)
                iv.ClearYieldRequest();
            // After pause the shared demux can leave decode state out of step with the frozen clock
            // even when Position still reports emitted samples (≈ clock). Always re-seek on resume —
            // same policy as AvPlaybackCoordinator.RealignAudioSourceBeforeStart for audio.
            if (!_clock.IsRunning && _source is ISeekableSource seekable)
            {
                var pos = _clock.CurrentPosition;
                var src = seekable.Position;
                var drift = (src - pos).Duration();
                Trace.LogDebug(
                    "Play: realigning source from {Src} to clock {Clock} (driftMs={DriftMs})",
                    src, pos, drift.TotalMilliseconds);
                seekable.Seek(pos);
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _tickHandler = (_, _) => OnVideoTick();
            _clock.VideoTick += _tickHandler;

            _decodeThread = new Thread(() => DecodeLoop(token))
            {
                IsBackground = true,
                Name = "VideoPlayer.Decode",
            };
            _isRunning = true;
            _decodeThread.Start();
            Trace.LogDebug("Play: format={Format} queueCap={Cap} clockRunning={ClockRunning} clockType={ClockType}",
                _sink.Format, _queueCapacity, _clock.IsRunning, _clock.GetType().Name);
        }
    }

    /// <summary>
    /// Stop scheduling and decoding, drop any queued frames. Use this for
    /// both pause and stop semantics — when the master clock is also paused,
    /// the displayed frame just freezes anyway.
    /// </summary>
    /// <remarks>
    /// If <paramref name="cancellationToken"/> is cancelled while the decode thread is still alive,
    /// the player becomes terminal/non-restartable; dispose it and create a new player.
    /// </remarks>
    /// <param name="cancellationToken">Cooperative cancel while joining the decode thread.</param>
    public void Pause(CancellationToken cancellationToken = default) => StopInternal(cancellationToken);

    /// <summary>Alias for <see cref="Pause(CancellationToken)"/>.</summary>
    public void Stop(CancellationToken cancellationToken = default) => StopInternal(cancellationToken);

    /// <summary>
    /// Coordinated seek: pauses, calls <see cref="ISeekableSource.Seek"/> on
    /// the source (must implement it — throws otherwise), and resumes if
    /// the player was running.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not ISeekableSource seekable)
            throw new InvalidOperationException("source does not implement ISeekableSource");

        var wasRunning = IsRunning;
        if (wasRunning) StopInternal(CancellationToken.None);
        seekable.Seek(position);
        if (wasRunning) Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(() => StopInternal(CancellationToken.None), "VideoPlayer.Dispose: StopInternal");
        _slotsAvailable.Dispose();
    }

    // --- internals ---------------------------------------------------------

    private void StopInternal(CancellationToken cancellationToken = default)
    {
        var interrupt = _source as ICooperativeVideoReadInterrupt;
        interrupt?.RequestYieldBetweenReads();

        Thread? toJoin;
        CancellationTokenSource? toDispose;
        EventHandler? handler;
        lock (_gate)
        {
            if (!_isRunning)
            {
                interrupt?.ClearYieldRequest();
                return;
            }
            _isRunning = false;
            handler = _tickHandler;
            _tickHandler = null;
            toJoin = _decodeThread;
            toDispose = _cts;
            _decodeThread = null;
            _cts = null;
        }

        if (handler is not null)
            _clock.VideoTick -= handler;
        toDispose?.Cancel();

        // Once IsRunning is false, OnVideoTick returns without dequeuing — no more
        // _slotsAvailable.Release() from the clock. Decode may be blocked on Wait
        // for a slot while the queue still holds frames; dispose/recreate the
        // semaphore so Wait throws ObjectDisposedException and the thread can exit.
        DrainQueue();

        var joinCancelled = false;
        try
        {
            // Never join the decode thread without a wall-clock cap: with CancellationToken.None,
            // JoinThreadWhileCancelable would spin until the thread exits — if TryReadNextFrame blocks
            // in native code, Pause/Dispose can hang for minutes. Bounded join + cancelled CTS unblocks Wait.
            CooperativePlaybackJoin.JoinThread(toJoin, TimeSpan.FromSeconds(12), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            joinCancelled = true;
            if (toJoin is { IsAlive: true })
            {
                var ex = new OperationCanceledException(
                    "VideoPlayer stop was cancelled before the decode thread exited; the player is now non-restartable.",
                    cancellationToken);
                _fault = ex;
                _decodeThreadStuck = true;
                Trace.LogError(ex, "StopInternal: stop cancelled with decode thread still alive; player is now non-restartable (dispose it).");
                RaiseFaulted(ex);
            }
            throw;
        }
        finally
        {
            if (toJoin is not { IsAlive: true })
                toDispose?.Dispose();
            else
                MediaDiagnostics.LogWarning("VideoPlayer.StopInternal: decode thread still alive after join; leaking CancellationTokenSource to avoid use-after-dispose.");
            if (_source is ICooperativeVideoReadInterrupt clearYield)
                clearYield.ClearYieldRequest();

            DrainQueue();
            ReleaseHeldFrame();
        }

        if (toJoin is { IsAlive: true } && !joinCancelled)
        {
            // The decode thread is still blocked in native code after the full join cap (not an early
            // cooperative cancel). A subsequent Play() must NOT start a second decode thread sharing
            // this source/queue/semaphore — concurrent non-thread-safe decoder access. Mark terminal.
            _decodeThreadStuck = true;
            var ex = new TimeoutException("VideoPlayer decode thread did not exit within the join cap; the player is now non-restartable.");
            _fault = ex;
            Trace.LogError(ex, "StopInternal: decode thread did not exit within the join cap; player is now non-restartable (dispose it).");
            RaiseFaulted(ex);
        }
    }

    private void DrainQueue()
    {
        // Reset the slot semaphore wholesale: anything we drop here returns
        // a slot, and the next Play wants the full capacity available.
        var dropped = 0;
        while (_queue.TryDequeue(out var f))
        {
            f.Dispose();
            dropped++;
        }
        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedQueueDrain, dropped);
            Volatile.Write(ref _latestDecodedPtsTicks, Volatile.Read(ref _lastPresentedPtsTicks));
        }

        // Reissue the semaphore so the new run's decode loop has a clean
        // tally — simpler than counting precisely how many tokens to release.
        _slotsAvailable.Dispose();
        _slotsAvailable = new SemaphoreSlim(_queueCapacity, _queueCapacity);
    }

    private void DecodeLoop(CancellationToken token)
    {
        Trace.LogDebug("DecodeLoop: entered");
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_source.IsExhausted)
                {
                    Trace.LogDebug("DecodeLoop: source exhausted (decoded={Decoded})", Volatile.Read(ref _decoded));
                    return;
                }

                if (!_source.TryReadNextFrame(out var frame))
                {
                    // Live source not ready (or end-of-stream lag). Brief sleep
                    // so we don't spin; cancellation is honoured promptly.
                    if (token.WaitHandle.WaitOne(_decodePollInterval)) return;
                    continue;
                }

                Interlocked.Increment(ref _decoded);
                Volatile.Write(ref _latestDecodedPtsTicks, frame.PresentationTime.Ticks);
                if (Interlocked.Exchange(ref _firstDecodedLogged, 1) == 0)
                    Trace.LogDebug("DecodeLoop: first frame decoded (pts={Pts})", frame.PresentationTime);

                try
                {
                    _slotsAvailable.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    frame.Dispose();
                    return;
                }
                _queue.Enqueue(frame);
            }
        }
        catch (ObjectDisposedException) { /* StopInternal disposed semaphore mid-wait */ }
        catch (Exception ex)
        {
            EventHandler? handler = null;
            CancellationTokenSource? toDispose = null;
            _fault = ex;
            lock (_gate)
            {
                _isRunning = false;
                handler = _tickHandler;
                _tickHandler = null;
                if (ReferenceEquals(_decodeThread, Thread.CurrentThread))
                    _decodeThread = null;
                toDispose = _cts;
                _cts = null;
            }
            if (handler is not null)
                _clock.VideoTick -= handler;
            try { toDispose?.Cancel(); } catch { /* best effort */ }
            try { toDispose?.Dispose(); } catch { /* best effort */ }
            DrainQueue();
            ReleaseHeldFrame();
            Trace.LogError(ex, "VideoPlayer.DecodeLoop: unhandled exception; player faulted and stopped");
            RaiseFaulted(ex);
        }
        finally
        {
            Trace.LogDebug("DecodeLoop: exiting (decoded={Decoded} displayed={Displayed} droppedLate={DropLate} droppedDrain={DropDrain})",
                Volatile.Read(ref _decoded), Volatile.Read(ref _displayed),
                Volatile.Read(ref _droppedLate), Volatile.Read(ref _droppedQueueDrain));
        }
    }

    private void OnVideoTick()
    {
        if (!IsRunning) return;

        if (Interlocked.Exchange(ref _firstTickLogged, 1) == 0)
            Trace.LogDebug("OnVideoTick: first tick (playhead={Playhead} queued={Queued} mode={Mode})",
                _clock.CurrentPosition, _queue.Count, _presentationMode);

        if (_presentationMode == VideoPresentationMode.LatestOnTick)
        {
            PresentLatestQueuedFrame();
            return;
        }

        // Hold video back by the audio output latency so it lines up with what's actually heard
        // (audio is buffered ahead in the output ring; presenting video at raw clock time leads it).
        var playhead = _clock.CurrentPosition - PlayheadOffset;
        if (_syncDebugTicksRemaining > 0)
        {
            Trace.LogDebug(
                "OnVideoTick: sync clock={Clock} playhead={Playhead} offset={Offset} queued={Queued} latestDecoded={Latest}",
                _clock.CurrentPosition, playhead, PlayheadOffset, _queue.Count, LatestDecodedPresentationTime);
            _syncDebugTicksRemaining--;
        }

        var early = playhead + EarlyTolerance;
        var lateCutoff = playhead - LateThreshold;

        VideoFrame? toShow = null;
        // Anti-freeze / catch-up fallback. When the playhead has run past EVERY queued frame — decode
        // fell behind after a seek or a high-variance scene and the shallow queue drained — we used to
        // drop them all and present nothing, which froze the picture permanently (the freerun clock never
        // waits for video, so it never recovered). Instead keep the NEWEST late frame and show it when no
        // on-time frame exists: a brief A/V lag beats a freeze, and presenting the latest decoded frame
        // each tick lets video visually catch up as the decode bursts land.
        VideoFrame? newestLate = null;

        // Walk the queue forward as long as the next frame's PTS is in the
        // past (or within early tolerance). We always prefer the latest such
        // frame — older candidates are dropped as "skipped" when we find a
        // newer one. Frames more than LateThreshold behind are held as the
        // newest-late fallback (newest wins) rather than discarded outright.
        while (_queue.TryPeek(out var head))
        {
            if (head.PresentationTime > early) break;

            if (!_queue.TryDequeue(out var frame))
                break;
            _slotsAvailable.Release();

            if (frame.PresentationTime < lateCutoff)
            {
                if (newestLate is not null)
                {
                    newestLate.Dispose();
                    Interlocked.Increment(ref _droppedLate);
                }
                newestLate = frame;
                continue;
            }

            if (toShow is not null)
            {
                toShow.Dispose();
                Interlocked.Increment(ref _droppedLate);
            }
            toShow = frame;
        }

        if (toShow is not null)
        {
            // An on-time frame won this tick; the older late fallback is superseded.
            if (newestLate is not null)
            {
                newestLate.Dispose();
                Interlocked.Increment(ref _droppedLate);
            }
        }
        else
        {
            // Nothing on time — present the newest late frame (if any) so the picture keeps moving.
            toShow = newestLate;
        }

        if (toShow is not null)
        {
            // Capture a managed snapshot of the last-frame plane data the moment we know this
            // is the final frame the source will produce — must happen before _sink.Submit takes
            // ownership (the output may free the underlying buffers via the frame's release).
            // Skipped for hardware-backed frames: their lifetime is tied to the source's release
            // and there's no portable way to fork an additional refcounted view here.
            if (HoldLastFrameAtEnd && _heldPlanes is null
                && _source.IsExhausted && _queue.IsEmpty
                && toShow.DmabufNv12 is null && toShow.DmabufP010 is null
                && toShow.DmabufP016 is null && toShow.Win32Nv12 is null)
            {
                CaptureHeldFrame(toShow);
            }

            TrySubmitFrameToSink(toShow, "OnVideoTick", logFirstSubmission: true);
        }
        else if (_source.IsExhausted && _queue.IsEmpty)
        {
            if (HoldLastFrameAtEnd && _heldPlanes is not null)
            {
                TrySubmitHeldFrame(playhead);
            }
            else
            {
                CompletedNaturally = true;
                ReleaseHeldFrame();
            }
        }
        else if (!HoldLastFrameAtEnd && _heldPlanes is not null)
        {
            // Property toggled off mid-hold — drop the snapshot.
            ReleaseHeldFrame();
        }
    }

    internal bool TryPresentBufferedFrameForSync(
        TimeSpan target,
        TimeSpan outputIdleTimeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning)
            return false;

        if (!TryTakeFrameForSyncPresentation(target, out var frame) || frame is null)
            return false;

        var outputQueue = _sink as IVideoOutputQueueControl;
        if (outputQueue is not null)
        {
            try
            {
                outputQueue.AbandonQueuedFrames();
                if (!WaitForOutputIdle(outputQueue, SyncOutputPreDrainTimeout, cancellationToken, "PresentBufferedFrameForSync.preDrain"))
                {
                    Trace.LogDebug("PresentBufferedFrameForSync: output did not become idle before sync submit (timeout={Timeout})",
                        SyncOutputPreDrainTimeout);
                }
            }
            catch (OperationCanceledException)
            {
                frame.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                Trace.LogDebug(ex, "PresentBufferedFrameForSync: video output pre-drain failed");
            }
        }

        var pts = frame.PresentationTime;
        if (!TrySubmitFrameToSink(frame, "PresentBufferedFrameForSync", logFirstSubmission: true))
            return false;

        var outputReady = true;
        if (outputQueue is not null && outputIdleTimeout > TimeSpan.Zero)
            outputReady = WaitForOutputIdle(outputQueue, outputIdleTimeout, cancellationToken, "PresentBufferedFrameForSync.postSubmit");

        Trace.LogDebug(
            "PresentBufferedFrameForSync: target={Target} pts={Pts} outputReady={OutputReady} queued={Queued} latestDecoded={Latest}",
            target, pts, outputReady, QueuedFrameCount, LatestDecodedPresentationTime);
        return outputReady;
    }

    private bool TryTakeFrameForSyncPresentation(TimeSpan target, out VideoFrame? toShow)
    {
        toShow = null;
        var playhead = target - PlayheadOffset;
        var early = playhead + EarlyTolerance;
        var lateCutoff = playhead - LateThreshold;

        while (_queue.TryPeek(out var head))
        {
            if (head.PresentationTime > early)
                break;

            if (!_queue.TryDequeue(out var frame))
                break;
            _slotsAvailable.Release();

            if (frame.PresentationTime < lateCutoff)
            {
                frame.Dispose();
                Interlocked.Increment(ref _droppedLate);
                continue;
            }

            if (toShow is not null)
            {
                toShow.Dispose();
                Interlocked.Increment(ref _droppedLate);
            }

            toShow = frame;
        }

        if (toShow is not null)
            return true;

        var futureCutoff = playhead + SyncStartupLead;
        if (!_queue.TryPeek(out var future) || future.PresentationTime > futureCutoff)
            return false;

        if (!_queue.TryDequeue(out toShow))
            return false;
        _slotsAvailable.Release();
        return true;
    }

    private bool WaitForOutputIdle(
        IVideoOutputQueueControl output,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operation)
    {
        try
        {
            return output.WaitForIdle(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.LogDebug(ex, "{Operation}: video output idle wait failed", operation);
            return false;
        }
    }

    private void PresentLatestQueuedFrame()
    {
        VideoFrame? latest = null;
        while (_queue.TryDequeue(out var frame))
        {
            _slotsAvailable.Release();
            latest?.Dispose();
            latest = frame;
        }

        if (latest is null)
            return;

        TrySubmitFrameToSink(latest, "PresentLatestQueuedFrame", logFirstSubmission: false);
    }

    private bool TrySubmitFrameToSink(VideoFrame frame, string operation, bool logFirstSubmission)
    {
        // Capture PTS before Submit: after a successful Submit the output owns
        // the frame and we must not touch or dispose it again. Counters and the
        // presentation event run outside the Submit try so a throwing subscriber
        // can never trigger a double-release of a frame the output already owns.
        var presentedPts = frame.PresentationTime;
        var submitted = false;
        try
        {
            _sink.Submit(frame);
            submitted = true;
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, $"VideoPlayer.{operation} output Submit");
#endif
            // Output threw — ownership did not move; release the frame to avoid a
            // native buffer leak. Rethrow would kill MediaClock's driver.
            try { frame.Dispose(); }
#if DEBUG
            catch (Exception dex) { MediaDiagnostics.LogError(dex, $"VideoPlayer.{operation} frame Dispose after Submit failure"); }
#else
            catch { /* best effort */ }
#endif
            _ = ex;
        }

        if (!submitted)
            return false;

        Interlocked.Increment(ref _displayed);
        if (logFirstSubmission && Interlocked.Exchange(ref _firstSubmittedLogged, 1) == 0)
            Trace.LogDebug("{Operation}: first frame submitted (pts={Pts})", operation, presentedPts);
        RaisePresented(presentedPts);
        return true;
    }

    private void CaptureHeldFrame(VideoFrame source)
    {
        var planeCount = source.PlaneCount;
        var planes = new ReadOnlyMemory<byte>[planeCount];
        for (var i = 0; i < planeCount; i++)
        {
            var managed = source.Planes[i].ToArray();
            planes[i] = managed;
        }
        _heldPlanes = planes;
        _heldStrides = (int[])source.Strides.Clone();
        _heldFormat = source.Format;
        _heldTransferHint = source.ColorTransferHint;
        _heldOriginalPts = source.PresentationTime;
        Volatile.Write(ref _holdSubmitCount, 0);
    }

    private void TrySubmitHeldFrame(TimeSpan playhead)
    {
        if (_heldPlanes is null || _heldStrides is null) return;
        var resubmitPts = playhead > _heldOriginalPts ? playhead : _heldOriginalPts;
        VideoFrame frame;
        try
        {
            frame = new VideoFrame(
                resubmitPts,
                _heldFormat,
                _heldPlanes,
                _heldStrides,
                release: null,
                metadata: new VideoFrameMetadata(ColorTransferHint: _heldTransferHint));
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "VideoPlayer.TrySubmitHeldFrame: VideoFrame ctor");
#else
            _ = ex;
#endif
            ReleaseHeldFrame();
            CompletedNaturally = true;
            return;
        }

        var submitted = false;
        try
        {
            _sink.Submit(frame);
            submitted = true;
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "VideoPlayer.TrySubmitHeldFrame: output Submit");
#else
            _ = ex;
#endif
            try { frame.Dispose(); }
            catch { /* best effort */ }
        }

        if (submitted)
        {
            Interlocked.Increment(ref _displayed);
            Interlocked.Increment(ref _holdSubmitCount);
            RaisePresented(resubmitPts);
        }
    }

    /// <summary>
    /// Notify <see cref="FramePresentationTimePresented"/> subscribers after a frame was
    /// successfully submitted. The output owns the frame by now, so this must never touch or
    /// dispose it, and a throwing subscriber must not reach the clock-driver thread.
    /// </summary>
    private void RaisePresented(TimeSpan pts)
    {
        Volatile.Write(ref _lastPresentedPtsTicks, pts.Ticks);
        try { FramePresentationTimePresented?.Invoke(pts); }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "VideoPlayer.FramePresentationTimePresented subscriber threw");
#else
            _ = ex;
#endif
        }
    }

    private void RaiseFaulted(Exception ex)
    {
        try { Faulted?.Invoke(this, new VideoPlayerFaultedEventArgs(ex)); }
        catch (Exception hex) { MediaDiagnostics.LogError(hex, "VideoPlayer.Faulted handler threw"); }
    }

    private void ReleaseHeldFrame()
    {
        _heldPlanes = null;
        _heldStrides = null;
        _heldFormat = default;
        _heldTransferHint = default;
        _heldOriginalPts = TimeSpan.Zero;
        Volatile.Write(ref _holdSubmitCount, 0);
    }

    /// <summary>
    /// True when the queue holds at least one frame whose PTS is at or before
    /// <paramref name="playhead"/> plus <see cref="EarlyTolerance"/>.
    /// </summary>
    internal bool HasPresentableFrameAt(TimeSpan playhead) =>
        HasFrameWithinLeadOf(playhead, EarlyTolerance);

    /// <summary>
    /// Max lead (ahead of playhead) for the earliest queued frame to count as sync-ready
    /// before audio starts. ~1.5 frame periods, clamped for pathological rates.
    /// </summary>
    internal TimeSpan SyncStartupLead
    {
        get
        {
            var fps = _sink.Format.FrameRate.ToDouble();
            var lead = fps > 0
                ? TimeSpan.FromSeconds(1.5 / fps)
                : TimeSpan.FromMilliseconds(62.5);
            if (lead < TimeSpan.FromMilliseconds(50))
                lead = TimeSpan.FromMilliseconds(50);
            if (lead > TimeSpan.FromMilliseconds(250))
                lead = TimeSpan.FromMilliseconds(250);
            return lead;
        }
    }

    /// <summary>
    /// True when some queued frame has PTS at or before <paramref name="playhead"/> + <paramref name="lead"/>.
    /// Used for pre-audio buffer checks where the clock is frozen and the first decode frame may
    /// legitimately start one GOP/frame period after the sync target.
    /// </summary>
    internal bool HasFrameWithinLeadOf(TimeSpan playhead, TimeSpan lead)
    {
        var cutoff = playhead + lead;
        foreach (var frame in _queue)
        {
            if (frame.PresentationTime <= cutoff)
                return true;
        }
        return false;
    }
}

/// <summary>Argument for <see cref="VideoPlayer.Faulted"/>.</summary>
public sealed class VideoPlayerFaultedEventArgs : EventArgs
{
    public Exception Exception { get; }

    public VideoPlayerFaultedEventArgs(Exception exception) => Exception = exception;
}
