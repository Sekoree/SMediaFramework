using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Snapshot counters for a <see cref="VideoOutputPump"/> (video analogue of audio <c>OutputPumpStats</c>).
/// <see cref="MaxQueueDepth"/> is configured capacity (oldest dropped when enqueue would exceed it).
/// <see cref="CurrentQueuedDepth"/> is frames waiting on the pump thread (approximate under concurrency).
/// </summary>
public readonly record struct VideoOutputPumpMetrics(
    long DroppedFrames,
    long SubmittedFrames,
    int MaxQueueDepth,
    int CurrentQueuedDepth);

/// <summary>Optional <see cref="VideoOutputPump"/> wrapping when registering an output on <see cref="VideoRouter"/>.</summary>
/// <param name="MaxQueuedFrames">Queue depth before drop-oldest (must be ≥ 1).</param>
/// <param name="ThreadName">Background drainer thread name; defaults from the router output id when omitted.</param>
/// <param name="Logger">Optional logger for pump diagnostics.</param>
/// <param name="DisposeInnerOutputWhenPumpDisposes">
/// When false, the inner output is not disposed with the pump (use when the inner is owned elsewhere, e.g. an NDI sender).
/// </param>
public readonly record struct VideoOutputPumpAttachOptions(
    int MaxQueuedFrames = 3,
    string? ThreadName = null,
    ILogger? Logger = null,
    bool DisposeInnerOutputWhenPumpDisposes = true);

/// <summary>
/// Bounded asynchronous delivery to an <see cref="IVideoOutput"/>: <see cref="Submit"/> returns quickly
/// after enqueueing; a drainer thread calls <see cref="IVideoOutput.Submit"/>. When the queue is full,
/// the <strong>oldest</strong> frame is disposed and replaced (drop-late policy).
/// <see cref="Dispose"/> performs best-effort cooperative shutdown; in <c>DEBUG</c> builds, cancel/join or inner-output
/// disposal failures are logged via <see cref="MediaDiagnostics"/>.
/// </summary>
/// <remarks>
/// Intended for slow network encoders (for example NDI) so a blocking <see cref="IVideoOutput.Submit"/>
/// cannot stall upstream decode. This is the video analogue of audio <c>OutputPump</c> back-pressure;
/// <see cref="VideoRouter.AddOutput"/> can attach a pump via <see cref="VideoOutputPumpAttachOptions"/>.
/// Subscribe to <see cref="VideoOutputPump.PumpPressure"/> (or <see cref="VideoRouter.PumpPressure"/> when registered via the router)
/// for per-drop callbacks on the <see cref="Submit"/> thread (same idea as <c>AudioRouter.PumpPressure</c>).
/// </remarks>
public sealed class VideoOutputPump : IVideoOutput, IVideoOutputD3D11GlBorrowSetup, IVideoOutputQueueControl, IVideoOutputCooperativeAbort, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly bool _disposeInner;
    private readonly int _maxQueued;
    private readonly ILogger? _log;
    private readonly string _name;
    private readonly object _gate = new();
    private readonly Queue<(VideoFrame Frame, int Version)> _queue = new();
    private readonly ManualResetEventSlim _pending = new(false);
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _configured;
    // Bumped under _gate on a real format change. Each queued frame carries the version it was enqueued at,
    // so the drain thread can drop a frame that was already dequeued before a reconfigure (the in-flight
    // stale-format frame the queue-drop in Configure cannot reach).
    private int _formatVersion;
    private bool _disposed;
    private long _dropped;
    private long _submitted;
    private long _lastDropLogTicks;
    private long _lastSlowSubmitLogTicks;
    private long _lastSlowConvertLogTicks;
    private int _activeSubmits;
    private int _firstSubmitLogged;
    private EventHandler<VideoOutputPumpPressureEventArgs>? _pumpPressure;

    // Optional branch conversion performed on THIS pump's drain thread instead of the router submit thread
    // (P2-7): the router hands a slow converting branch — e.g. NDI's yuv422p10le→UYVY at 4K60 — a zero-copy
    // raw view via VideoFrame.TryCreateCpuFanOutViews and lets the heavy swscale run here, off the player's
    // per-frame budget. _branchConverter is touched only by the drain thread; SetBranchConverter stages a
    // swap under _gate that the drain thread adopts BETWEEN frames, so a reconfigure can never dispose a
    // converter while Convert is running on it.
    private IVideoCpuFrameConverter? _branchConverter;
    private IVideoCpuFrameConverter? _pendingBranchConverter;
    private bool _branchConverterPending;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoOutputPump");
    private const double SlowInnerSubmitWarningMs = 50;
    private const double SlowBranchConvertWarningMs = 100;

    public VideoOutputPump(IVideoOutput inner, int maxQueuedFrames = 3, string name = "VideoOutputPump", ILogger? log = null,
        bool disposeInnerOnDispose = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxQueuedFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxQueuedFrames));
        _inner = inner;
        _disposeInner = disposeInnerOnDispose;
        _maxQueued = maxQueuedFrames;
        _name = name;
        _log = log;
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long SubmittedFrames => Interlocked.Read(ref _submitted);

    /// <summary>Optional: raised on the <see cref="Submit"/> thread each time an oldest frame is dropped because the queue is full.</summary>
    public event EventHandler<VideoOutputPumpPressureEventArgs>? PumpPressure
    {
        add => _pumpPressure += value;
        remove => _pumpPressure -= value;
    }

    /// <summary>Configured queue depth before drop-oldest (same as <see cref="VideoOutputPumpAttachOptions.MaxQueuedFrames"/>).</summary>
    public int MaxQueueDepth => _maxQueued;

    /// <summary>Frames currently waiting for the drainer thread (snapshot under <see cref="Submit"/> / <c>Run</c> concurrency).</summary>
    public int CurrentQueuedDepth
    {
        get
        {
            lock (_gate) return _queue.Count;
        }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public VideoFormat Format => _inner.Format;

    public void Configure(VideoFormat format)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "VideoOutputPump.Configure", slowWarningMs: 250);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_configured)
            {
                // VideoRouter.ApplyConfigureLocked re-Configures the primary every time a branch route is
                // added even though the format is unchanged — a cheap pass-through. But a REAL format
                // change must not leave old-format frames queued for the reconfigured inner output, so
                // drop them first. (A frame already in-flight on the drainer thread can still race; full
                // format-versioned queues would close that residual window.)
                if (format != _inner.Format)
                {
                    _formatVersion++;
                    while (_queue.Count > 0)
                    {
                        _queue.Dequeue().Frame.Dispose();
                        Interlocked.Increment(ref _dropped);
                    }
                }
                Trace.LogTrace("Configure: name={Name} re-configure (already running) format={Format}", _name, format);
                _inner.Configure(format);
                timing?.SetOutcome($"name={_name} reconfigure format={format}");
                return;
            }
            Trace.LogDebug("Configure: name={Name} format={Format} maxQueued={MaxQueued} innerType={InnerType}",
                _name, format, _maxQueued, _inner.GetType().Name);
            _inner.Configure(format);
            _configured = true;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = _name,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            timing?.SetOutcome($"name={_name} format={format} maxQueued={_maxQueued}");
        }
    }

    /// <summary>
    /// Sets (or clears) the per-frame converter run on the drain thread before delivery to the inner output.
    /// Used by <see cref="VideoRouter"/> for a converting branch so the swscale repack happens here rather than
    /// on the player submit thread. The swap is staged under the gate and adopted by the drain thread between
    /// frames; the previously-set converter is disposed by the drain thread (or by <see cref="Dispose"/>), never
    /// while a <c>Convert</c> is in flight. The pump owns the converter and disposes it.
    /// </summary>
    public void SetBranchConverter(IVideoCpuFrameConverter? converter)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A prior staged converter the drain thread never got to adopt would otherwise leak.
            if (_branchConverterPending && !ReferenceEquals(_pendingBranchConverter, converter))
                _pendingBranchConverter?.Dispose();
            _pendingBranchConverter = converter;
            _branchConverterPending = true;
        }
    }

    /// <summary>Drain-thread-only: adopt a staged converter swap between frames and dispose the one it replaces.</summary>
    private void AdoptPendingBranchConverterIfAny()
    {
        IVideoCpuFrameConverter? toDispose = null;
        lock (_gate)
        {
            if (_branchConverterPending)
            {
                toDispose = _branchConverter;
                _branchConverter = _pendingBranchConverter;
                _pendingBranchConverter = null;
                _branchConverterPending = false;
            }
        }

        if (toDispose is not null)
            MediaDiagnostics.SwallowDisposeErrors(toDispose.Dispose, $"VideoOutputPump.SetBranchConverter: previous converter ({_name})");
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource)
    {
        if (_inner is IVideoOutputD3D11GlBorrowSetup innerBorrow)
        {
            if (videoSource is IHardwareD3D11GlInteropSource)
                innerBorrow.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);
            else
                innerBorrow.SetBorrowVideoSourceForWin32Nv12Gl(null);
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_configured)
            {
                frame.Dispose();
                throw new InvalidOperationException("VideoOutputPump.Submit called before Configure");
            }

            while (_queue.Count >= _maxQueued)
            {
                var victim = _queue.Dequeue().Frame;
                victim.Dispose();
                var total = Interlocked.Increment(ref _dropped);
                RaisePumpPressure(total);
                ThrottledWarnQueueDrop();
            }

            _queue.Enqueue((frame, _formatVersion));
            _pending.Set();
        }
    }

    public void AbandonQueuedFrames()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_queue.Count > 0)
            {
                _queue.Dequeue().Frame.Dispose();
                Interlocked.Increment(ref _dropped);
            }

            _pending.Reset();
        }

        if (_inner is IVideoOutputQueueControl innerControl)
            innerControl.AbandonQueuedFrames();
    }

    /// <summary>
    /// Forwards a cooperative-abort request to the inner output (for when this pump is itself the inner of
    /// an outer wrapper). The pump's own <see cref="Dispose"/> also signals the inner directly.
    /// </summary>
    public void RequestSubmitAbort() =>
        (_inner as IVideoOutputCooperativeAbort)?.RequestSubmitAbort();

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queueEmpty = false;
            lock (_gate)
                queueEmpty = _queue.Count == 0;

            if (queueEmpty && Volatile.Read(ref _activeSubmits) == 0)
                break;

            if (Environment.TickCount64 >= deadline)
            {
                Trace.LogWarning(
                    "VideoOutputPump {Name}: WaitForIdle timed out after {ElapsedMs:0.00}ms (timeout={TimeoutMs:0.00}ms, queued={Queued}, activeSubmits={ActiveSubmits}, submitted={Submitted}, dropped={Dropped})",
                    _name,
                    MediaDiagnostics.ElapsedMillisecondsSince(started),
                    timeout.TotalMilliseconds,
                    CurrentQueuedDepth,
                    Volatile.Read(ref _activeSubmits),
                    Interlocked.Read(ref _submitted),
                    Interlocked.Read(ref _dropped));
                return false;
            }

            Thread.Sleep(1);
        }

        if (_inner is not IVideoOutputQueueControl innerControl)
            return true;

        var remainingMs = Math.Max(0, deadline - Environment.TickCount64);
        return innerControl.WaitForIdle(TimeSpan.FromMilliseconds(remainingMs), cancellationToken);
    }

    private void RaisePumpPressure(long droppedFramesTotal) =>
        _pumpPressure?.Invoke(this, new VideoOutputPumpPressureEventArgs(_name, droppedFramesTotal));

    private void ThrottledWarnQueueDrop()
    {
        var now = Environment.TickCount64;
        var prev = Volatile.Read(ref _lastDropLogTicks);
        if (now - prev >= 2000 || prev == 0)
        {
            if (Interlocked.CompareExchange(ref _lastDropLogTicks, now, prev) == prev)
            {
                var total = Interlocked.Read(ref _dropped);
                MediaDiagnostics.LogWarning(
                    $"VideoOutputPump '{_name}': queue full — dropped oldest frame(s); total DroppedFrames={total}. " +
                    "Increase MaxQueuedFrames on VideoOutputPumpAttachOptions or reduce output Submit latency.");
                if (_log is { } l && l.IsEnabled(LogLevel.Warning))
                    l.LogWarning("VideoOutputPump {Name}: queue full drops (total {Total})", _name, total);
            }
        }
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _pending.Wait(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested) break;
            while (true)
            {
                VideoFrame? next = null;
                var enqueuedVersion = 0;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _pending.Reset();
                        break;
                    }

                    var item = _queue.Dequeue();
                    next = item.Frame;
                    enqueuedVersion = item.Version;
                    Interlocked.Increment(ref _activeSubmits);
                }

                if (next is null) break;

                try
                {
                    // Branch conversion (if configured) runs HERE, on the drain thread, so the player submit
                    // thread is never charged for it. Adopt any staged converter swap first (between frames).
                    AdoptPendingBranchConverterIfAny();
                    var toSubmit = next;
                    if (_branchConverter is { } conv)
                    {
                        try
                        {
                            var convertStarted = Trace.IsEnabled(LogLevel.Warning) ? Stopwatch.GetTimestamp() : 0;
                            var converted = conv.Convert(next, next.ColorTransferHint);
                            if (convertStarted != 0)
                                MaybeLogSlowBranchConvert(convertStarted);
                            next.Dispose();
                            toSubmit = converted;
                        }
                        catch (Exception cex)
                        {
                            _log?.LogError(cex, "VideoOutputPump {Name}: branch convert failed — frame dropped", _name);
                            Trace.LogError(cex, $"{_name}: branch convert failed — frame dropped");
                            next.Dispose();
                            Interlocked.Increment(ref _dropped);
                            continue;
                        }
                    }

                    // Format-versioned queue: a real reconfigure since this frame was enqueued makes it
                    // stale for the now-reconfigured inner output — drop it rather than submit a
                    // wrong-format frame. Closes the "frame already dequeued before a format change"
                    // window the queue-drop in Configure cannot reach.
                    bool staleFormat;
                    lock (_gate) staleFormat = enqueuedVersion != _formatVersion;
                    if (staleFormat)
                    {
                        toSubmit.Dispose();
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }

                    try
                    {
                        var submitStarted = Trace.IsEnabled(LogLevel.Warning) ? Stopwatch.GetTimestamp() : 0;
                        _inner.Submit(toSubmit);
                        if (submitStarted != 0)
                            MaybeLogSlowInnerSubmit(submitStarted);
                        var n = Interlocked.Increment(ref _submitted);
                        if (n == 1)
                        {
                            Interlocked.Exchange(ref _firstSubmitLogged, 1);
                            Trace.LogDebug("{Name}: first frame submitted to inner output", _name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.LogError(ex, "VideoOutputPump {Name}: inner Submit failed", _name);
                        Trace.LogError(ex, $"{_name}: inner Submit failed");
                        toSubmit.Dispose();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeSubmits);
                }
            }
        }

        lock (_gate)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Frame.Dispose();
        }
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "VideoOutputPump.Dispose", slowWarningMs: 1000);
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                timing?.SetOutcome($"name={_name} already-disposed");
                return;
            }
            _disposed = true;
            _pumpPressure = null;
            thread = _thread;
            cts = _cts;
        }

        var threadExited = true;
        MediaDiagnostics.SwallowDisposeErrors(() =>
        {
            cts?.Cancel();
            _pending.Set();
            // Ask a cooperative inner output to abandon any in-flight Submit so the drainer returns
            // promptly — otherwise a slow blocking Submit forces the join cap to expire and we leak pump
            // state below. Outputs that don't implement this keep the prior leak-avoidance fallback.
            //
            // Only signal this when THIS pump owns the inner (disposeInner): the abort is terminal on the
            // inner, and a borrowed inner outlives the pump. A shared, long-lived NDIVideoSender (owned by
            // the NDI carrier and reused across every cue/deck acquisition) must NOT be permanently disabled
            // by a transient pump's teardown — doing so turned the whole NDI output black until app restart.
            if (_disposeInner)
                (_inner as IVideoOutputCooperativeAbort)?.RequestSubmitAbort();
            if (thread is { } t)
            {
                // Drainer is parked at most one SDK-paced Submit (typically ≤33 ms at the negotiated frame
                // rate). A 2 s cap covers slow outputs while keeping Pause / Stop / unload responsive — the
                // previous 30 s cap could stack into multi-second freezes when the drainer was held up
                // inside a paced send (most visible with attached_pic streams that declared 1 FPS).
                CooperativePlaybackJoin.JoinThread(t, TimeSpan.FromSeconds(2), CancellationToken.None);
                threadExited = !t.IsAlive;
            }
        }, $"VideoOutputPump.Dispose: cooperative shutdown ({_name})");

        if (!threadExited)
        {
            // The drainer is still blocked inside a slow inner Submit after the join cap. Disposing
            // _pending / _cts / the inner output now would pull state out from under it — an
            // ObjectDisposedException on the drainer's _pending.Wait, or use-after-dispose inside the
            // inner output. Leak those deliberately rather than crash: it's a background thread that
            // will exit on its own once the inner Submit finally returns (it re-checks cancellation).
            var ex = new TimeoutException($"VideoOutputPump '{_name}' drainer did not exit within the join cap.");
            NativeResourceHealth.ReportStuck(
                nameof(VideoOutputPump),
                "video output pump drainer",
                _name,
                TimeSpan.FromSeconds(2),
                ex);
            Trace.LogError(ex, "VideoOutputPump '{Name}': drainer did not exit within the join cap; leaking pump state to avoid use-after-dispose.", _name);
            timing?.SetOutcome($"name={_name} drainer-stuck");
            return;
        }

        cts?.Dispose();
        _pending.Dispose();
        lock (_gate)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Frame.Dispose();
        }

        // Drain thread has exited (threadExited above), so the branch converter is no longer in use.
        MediaDiagnostics.SwallowDisposeErrors(() =>
        {
            _branchConverter?.Dispose();
            _pendingBranchConverter?.Dispose();
        }, $"VideoOutputPump.Dispose: branch converter ({_name})");

        if (_disposeInner && _inner is IDisposable d)
        {
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, $"VideoOutputPump.Dispose: inner output ({_name})");
        }
        timing?.SetOutcome($"name={_name} submitted={Interlocked.Read(ref _submitted)} dropped={Interlocked.Read(ref _dropped)}");
    }

    private void MaybeLogSlowInnerSubmit(long started)
    {
        var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
        if (elapsedMs < SlowInnerSubmitWarningMs ||
            !TryUpdateThrottle(ref _lastSlowSubmitLogTicks, TimeSpan.FromSeconds(2)))
            return;

        Trace.LogWarning(
            "VideoOutputPump {Name}: inner Submit took {ElapsedMs:0.00}ms (threshold={ThresholdMs:0.00}ms, queued={Queued}, activeSubmits={ActiveSubmits}, submitted={Submitted}, dropped={Dropped})",
            _name,
            elapsedMs,
            SlowInnerSubmitWarningMs,
            CurrentQueuedDepth,
            Volatile.Read(ref _activeSubmits),
            Interlocked.Read(ref _submitted),
            Interlocked.Read(ref _dropped));
    }

    private void MaybeLogSlowBranchConvert(long started)
    {
        var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
        if (elapsedMs < SlowBranchConvertWarningMs ||
            !TryUpdateThrottle(ref _lastSlowConvertLogTicks, TimeSpan.FromSeconds(2)))
            return;

        Trace.LogWarning(
            "VideoOutputPump {Name}: branch conversion took {ElapsedMs:0.00}ms (threshold={ThresholdMs:0.00}ms, queued={Queued}, dropped={Dropped})",
            _name,
            elapsedMs,
            SlowBranchConvertWarningMs,
            CurrentQueuedDepth,
            Interlocked.Read(ref _dropped));
    }

    private static bool TryUpdateThrottle(ref long ticksSlot, TimeSpan interval)
    {
        var now = Stopwatch.GetTimestamp();
        var prev = Volatile.Read(ref ticksSlot);
        if (prev != 0 && Stopwatch.GetElapsedTime(prev, now) < interval)
            return false;
        return Interlocked.CompareExchange(ref ticksSlot, now, prev) == prev;
    }
}
