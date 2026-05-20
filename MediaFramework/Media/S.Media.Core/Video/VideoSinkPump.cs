using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Snapshot counters for a <see cref="VideoSinkPump"/> (video analogue of audio <c>SinkPumpStats</c>).
/// <see cref="MaxQueueDepth"/> is configured capacity (oldest dropped when enqueue would exceed it).
/// <see cref="CurrentQueuedDepth"/> is frames waiting on the pump thread (approximate under concurrency).
/// </summary>
public readonly record struct VideoSinkPumpMetrics(
    long DroppedFrames,
    long SubmittedFrames,
    int MaxQueueDepth,
    int CurrentQueuedDepth);

/// <summary>Optional <see cref="VideoSinkPump"/> wrapping when registering an output on <see cref="VideoRouter"/>.</summary>
/// <param name="MaxQueuedFrames">Queue depth before drop-oldest (must be ≥ 1).</param>
/// <param name="ThreadName">Background drainer thread name; defaults from the router output id when omitted.</param>
/// <param name="Logger">Optional logger for pump diagnostics.</param>
/// <param name="DisposeInnerSinkWhenPumpDisposes">
/// When false, the inner sink is not disposed with the pump (use when the inner is owned elsewhere, e.g. an NDI sender).
/// </param>
public readonly record struct VideoSinkPumpAttachOptions(
    int MaxQueuedFrames = 3,
    string? ThreadName = null,
    ILogger? Logger = null,
    bool DisposeInnerSinkWhenPumpDisposes = true);

/// <summary>
/// Bounded asynchronous delivery to an <see cref="IVideoSink"/>: <see cref="Submit"/> returns quickly
/// after enqueueing; a drainer thread calls <see cref="IVideoSink.Submit"/>. When the queue is full,
/// the <strong>oldest</strong> frame is disposed and replaced (drop-late policy).
/// <see cref="Dispose"/> performs best-effort cooperative shutdown; in <c>DEBUG</c> builds, cancel/join or inner-sink
/// disposal failures are logged via <see cref="MediaDiagnostics"/>.
/// </summary>
/// <remarks>
/// Intended for slow network encoders (for example NDI) so a blocking <see cref="IVideoSink.Submit"/>
/// cannot stall upstream decode. This is the video analogue of audio <c>SinkPump</c> back-pressure;
/// <see cref="VideoRouter.AddOutput"/> can attach a pump via <see cref="VideoSinkPumpAttachOptions"/>.
/// Subscribe to <see cref="VideoSinkPump.PumpPressure"/> (or <see cref="VideoRouter.PumpPressure"/> when registered via the router)
/// for per-drop callbacks on the <see cref="Submit"/> thread (same idea as <c>AudioRouter.PumpPressure</c>).
/// </remarks>
public sealed class VideoSinkPump : IVideoSink, IVideoSinkD3D11GlBorrowSetup, IDisposable
{
    private readonly IVideoSink _inner;
    private readonly bool _disposeInner;
    private readonly int _maxQueued;
    private readonly ILogger? _log;
    private readonly string _name;
    private readonly object _gate = new();
    private readonly Queue<VideoFrame> _queue = new();
    private readonly ManualResetEventSlim _pending = new(false);
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _configured;
    private bool _disposed;
    private long _dropped;
    private long _submitted;
    private long _lastDropLogTicks;
    private int _firstSubmitLogged;
    private EventHandler<VideoSinkPumpPressureEventArgs>? _pumpPressure;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoSinkPump");

    public VideoSinkPump(IVideoSink inner, int maxQueuedFrames = 3, string name = "VideoSinkPump", ILogger? log = null,
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
    public event EventHandler<VideoSinkPumpPressureEventArgs>? PumpPressure
    {
        add => _pumpPressure += value;
        remove => _pumpPressure -= value;
    }

    /// <summary>Configured queue depth before drop-oldest (same as <see cref="VideoSinkPumpAttachOptions.MaxQueuedFrames"/>).</summary>
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_configured)
            {
                // VideoRouter.ApplyConfigureLocked re-Configures the primary every time a branch route
                // is added (even though the negotiated format hasn't changed). Forward to the inner sink
                // without restarting the drainer thread — same semantics other IVideoSinks already have.
                // Frames already queued at the prior format would race a true format change, so callers
                // doing an actual resize/pixel-format swap should pause + drain first.
                Trace.LogTrace("Configure: name={Name} re-configure (already running) format={Format}", _name, format);
                _inner.Configure(format);
                return;
            }
            Trace.LogDebug("Configure: name={Name} format={Format} maxQueued={MaxQueued} innerType={InnerType}",
                _name, format, _maxQueued, _inner.GetType().Name);
            _inner.Configure(format);
            _configured = true;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = _name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource)
    {
        if (_inner is IVideoSinkD3D11GlBorrowSetup innerBorrow)
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("VideoSinkPump.Submit called before Configure");
        }

        lock (_gate)
        {
            while (_queue.Count >= _maxQueued)
            {
                var victim = _queue.Dequeue();
                victim.Dispose();
                var total = Interlocked.Increment(ref _dropped);
                RaisePumpPressure(total);
                ThrottledWarnQueueDrop();
            }

            _queue.Enqueue(frame);
        }

        _pending.Set();
    }

    private void RaisePumpPressure(long droppedFramesTotal) =>
        _pumpPressure?.Invoke(this, new VideoSinkPumpPressureEventArgs(_name, droppedFramesTotal));

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
                    $"VideoSinkPump '{_name}': queue full — dropped oldest frame(s); total DroppedFrames={total}. " +
                    "Increase MaxQueuedFrames on VideoSinkPumpAttachOptions or reduce sink Submit latency.");
                if (_log is { } l && l.IsEnabled(LogLevel.Warning))
                    l.LogWarning("VideoSinkPump {Name}: queue full drops (total {Total})", _name, total);
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
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _pending.Reset();
                        break;
                    }

                    next = _queue.Dequeue();
                }

                if (next is null) break;
                try
                {
                    _inner.Submit(next);
                    var n = Interlocked.Increment(ref _submitted);
                    if (n == 1)
                    {
                        Interlocked.Exchange(ref _firstSubmitLogged, 1);
                        Trace.LogDebug("{Name}: first frame submitted to inner sink", _name);
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogError(ex, "VideoSinkPump {Name}: inner Submit failed", _name);
                    Trace.LogError(ex, $"{_name}: inner Submit failed");
                    next.Dispose();
                }
            }
        }

        lock (_gate)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpPressure = null;
        try
        {
            _cts?.Cancel();
            _pending.Set();
            if (_thread is { } t)
                // Drainer is parked at most one SDK-paced Submit (typically ≤33 ms at the negotiated frame
                // rate). A 2 s cap covers slow sinks while keeping Pause / Stop / unload responsive — the
                // previous 30 s cap could stack into multi-second freezes when the drainer was held up
                // inside a paced send (most visible with attached_pic streams that declared 1 FPS).
                CooperativePlaybackJoin.JoinThread(t, TimeSpan.FromSeconds(2), CancellationToken.None);
        }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, $"VideoSinkPump.Dispose: cooperative shutdown ({_name})"); }
#else
        catch { /* best effort */ }
#endif
        finally
        {
            _cts?.Dispose();
            _pending.Dispose();
            lock (_gate)
            {
                while (_queue.Count > 0)
                    _queue.Dequeue().Dispose();
            }

            if (_disposeInner && _inner is IDisposable d)
            {
                try { d.Dispose(); }
#if DEBUG
                catch (Exception ex) { MediaDiagnostics.LogError(ex, $"VideoSinkPump.Dispose: inner sink ({_name})"); }
#else
                catch { /* best effort */ }
#endif
            }
        }
    }
}
