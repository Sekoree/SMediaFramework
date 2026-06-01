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

    private long _decoded;
    private long _displayed;
    private long _droppedLate;
    private long _droppedQueueDrain;
    private int _firstTickLogged;
    private int _firstSubmittedLogged;
    private int _firstDecodedLogged;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoPlayer");

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

    /// <summary>The clock that paces <see cref="IMediaClock.VideoTick"/> and <see cref="Play"/>.</summary>
    public IMediaClock Clock => _clock;

    public VideoFormat Format => _sink.Format;
    public bool IsRunning { get { lock (_gate) return _isRunning; } }

    /// <summary>True after the source signalled <see cref="IVideoSource.IsExhausted"/> AND every queued frame has been displayed.</summary>
    public bool CompletedNaturally { get; private set; }

    /// <summary>Total frames pulled from the source.</summary>
    public long DecodedCount => Volatile.Read(ref _decoded);
    /// <summary>Total frames handed to the output.</summary>
    public long DisplayedCount => Volatile.Read(ref _displayed);
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
    }

    /// <summary>Start decoding and scheduling. Idempotent.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
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
            if (_source is ICooperativeVideoReadInterrupt iv)
                iv.ClearYieldRequest();
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
        if (_source is ICooperativeVideoReadInterrupt iv)
            iv.RequestYieldBetweenReads();

        Thread? toJoin;
        CancellationTokenSource? toDispose;
        EventHandler? handler;
        lock (_gate)
        {
            if (!_isRunning) return;
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

        try
        {
            // Never join the decode thread without a wall-clock cap: with CancellationToken.None,
            // JoinThreadWhileCancelable would spin until the thread exits — if TryReadNextFrame blocks
            // in native code, Pause/Dispose can hang for minutes. Bounded join + cancelled CTS unblocks Wait.
            CooperativePlaybackJoin.JoinThread(toJoin, TimeSpan.FromSeconds(12), cancellationToken);
        }
        finally
        {
            toDispose?.Dispose();
        }

        if (_source is ICooperativeVideoReadInterrupt clearYield)
            clearYield.ClearYieldRequest();

        DrainQueue();
        ReleaseHeldFrame();
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
        if (dropped > 0) Interlocked.Add(ref _droppedQueueDrain, dropped);

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
            Trace.LogError(ex, "VideoPlayer.DecodeLoop: unhandled exception");
            throw;
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

        var playhead = _clock.CurrentPosition;
        var early = playhead + EarlyTolerance;
        var lateCutoff = playhead - LateThreshold;

        VideoFrame? toShow = null;

        // Walk the queue forward as long as the next frame's PTS is in the
        // past (or within early tolerance). We always prefer the latest such
        // frame — older candidates are dropped as "skipped" when we find a
        // newer one. Frames more than LateThreshold behind are discarded
        // outright.
        while (_queue.TryPeek(out var head))
        {
            if (head.PresentationTime > early) break;

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

            // Capture PTS before Submit: after a successful Submit the output owns
            // the frame and we must not touch or dispose it again. Counters and the
            // presentation event run outside the Submit try so a throwing subscriber
            // can never trigger a double-release of a frame the output already owns.
            var presentedPts = toShow.PresentationTime;
            var submitted = false;
            try
            {
                _sink.Submit(toShow);
                submitted = true;
            }
            catch (Exception ex)
            {
#if DEBUG
                MediaDiagnostics.LogError(ex, "VideoPlayer.OnVideoTick output Submit");
#endif
                // Output threw — ownership did not move; release the frame to avoid a
                // native buffer leak. Rethrow would kill MediaClock's driver.
                try { toShow.Dispose(); }
#if DEBUG
                catch (Exception dex) { MediaDiagnostics.LogError(dex, "VideoPlayer.OnVideoTick frame Dispose after Submit failure"); }
#else
                catch { /* best effort */ }
#endif
                _ = ex;
            }

            if (submitted)
            {
                Interlocked.Increment(ref _displayed);
                if (Interlocked.Exchange(ref _firstSubmittedLogged, 1) == 0)
                    Trace.LogDebug("OnVideoTick: first frame submitted (pts={Pts})", presentedPts);
                RaisePresented(presentedPts);
            }
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

        var presentedPts = latest.PresentationTime;
        var submitted = false;
        try
        {
            _sink.Submit(latest);
            submitted = true;
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "VideoPlayer.PresentLatestQueuedFrame output Submit");
#endif
            try { latest.Dispose(); } catch { /* best effort */ }
            _ = ex;
        }

        if (submitted)
        {
            Interlocked.Increment(ref _displayed);
            FramePresentationTimePresented?.Invoke(presentedPts);
        }
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

    private void ReleaseHeldFrame()
    {
        _heldPlanes = null;
        _heldStrides = null;
        _heldFormat = default;
        _heldTransferHint = default;
        _heldOriginalPts = TimeSpan.Zero;
        Volatile.Write(ref _holdSubmitCount, 0);
    }
}
