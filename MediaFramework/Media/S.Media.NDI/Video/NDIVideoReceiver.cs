using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.NDI.Clock;

namespace S.Media.NDI.Video;

/// <summary>
/// <see cref="IVideoSource"/> backed by an NDI receiver. Captures video on a background thread,
/// copies frames into pool-backed <see cref="VideoFrame"/> instances, and queues them for
/// <see cref="TryReadNextFrame"/>.
/// </summary>
internal sealed unsafe class NDIVideoReceiver : IVideoSource, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.Video.NDIVideoReceiver");

    private const int DefaultQueueDepth = 4;

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<VideoFrame> _queue = new();
    private readonly object _waitGate = new();
    /// <summary>Guards <see cref="_nextPts"/> and <see cref="_queue"/> drainage against the
    /// <see cref="RebaseToLatest"/> path. Held briefly per captured frame (during unpack + enqueue +
    /// PTS increment) so a concurrent rebase can't observe an in-flight stale-PTS frame.</summary>
    private readonly object _ptsLock = new();
    private readonly int _maxQueued;

    private VideoFormat _format;
    private PixelFormat[] _native = [];
    private TimeSpan _ptsStep = TimeSpan.FromMilliseconds(33);
    private TimeSpan _nextPts;
    private TimeSpan _rebaseBasePts;
    private TimeSpan _lastResolvedPts;
    private long _ndiTimingOriginTicks;
    private bool _ndiTimingOriginSet;
    private bool _hasLastResolvedPts;
    private bool _hasFormat;
    private bool _disposed;
    private long _overflowFrames;
    private long _unpackDrops;
    private volatile Exception? _faultEx;

    public bool IsConnected => _hasFormat;

    public VideoFormat Format =>
        _hasFormat
            ? _format
            : throw new InvalidOperationException(
                "NDI source has not delivered a video frame yet — wait until IsConnected is true");

    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;

    public bool IsExhausted => _disposed || _faultEx is not null;

    public long OverflowFrames => Interlocked.Read(ref _overflowFrames);

    /// <summary>Non-null after the background capture thread faulted. The receiver is then terminal:
    /// <see cref="IsExhausted"/> becomes true and <see cref="TryReadNextFrame"/> stops blocking.</summary>
    public Exception? Fault => _faultEx;

    /// <summary>Raised once if the background capture thread faults (native/unpack error). The handler
    /// runs on the capture thread; keep it lightweight.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Discards any frames currently queued and resets the PTS counter so the next captured frame is
    /// presented at <paramref name="nextPresentationTime"/>. Intended for the play-start moment: the receiver runs
    /// continuously from connect, so by the time the operator hits Play the PTS counter has advanced
    /// to <c>Tconnect</c> seconds while the consumer's playback clock may be at a fresh playhead —
    /// <see cref="S.Media.Core.Video.VideoPlayer"/> would then sit waiting for the playhead to catch
    /// up, leaving the output black. Calling this immediately before Play rebases the source so video
    /// presents in sync with the playback clock.
    /// </summary>
    public void RebaseToLatest(TimeSpan nextPresentationTime = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nextPresentationTime < TimeSpan.Zero)
            nextPresentationTime = TimeSpan.Zero;
        lock (_ptsLock)
        {
            while (_queue.TryDequeue(out var frame))
                frame.Dispose();
            _nextPts = nextPresentationTime;
            _rebaseBasePts = nextPresentationTime;
            _lastResolvedPts = nextPresentationTime;
            _hasLastResolvedPts = false;
            _ndiTimingOriginTicks = 0;
            _ndiTimingOriginSet = false;
        }
    }

    public NDIVideoReceiver(
        NDIDiscoveredSource source,
        string? receiverName = null,
        int maxQueuedFrames = DefaultQueueDepth,
        NDIIngestPlaybackClock? ingestClock = null)
    {
        if (maxQueuedFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedFrames));

        _maxQueued = maxQueuedFrames;
        _ingestClock = ingestClock;
        _ingestClock?.AttachReceiver();

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            // Force receiver-side conversion to formats we currently ingest reliably (UYVY/BGRA).
            // `Fastest` may surface sender-native formats (for example P216/UYVA) we don't unpack yet.
            var settings = new NDIReceiverSettings
            {
                ReceiverName = receiverName,
                ColorFormat = NDIRecvColorFormat.UyvyBgra,
            };
            rc = NDIReceiver.Create(out var recv, settings);
            if (rc != 0 || recv is null)
                throw new NDIException(rc, "NDIReceiver.Create");
            _receiver = recv;
            _receiver.Connect(source);
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDIVideoReceiver: NDIReceiver.Create/Connect");
#else
            _ = ex;
#endif
            _runtime.Dispose();
            throw;
        }

        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "NDIVideoReceiver",
        };
        _captureThread.Start();
    }

    public void SelectOutputFormat(PixelFormat format)
    {
        if (!_hasFormat)
            throw new InvalidOperationException("Format is not known until the first frame arrives.");
        if (format != _format.PixelFormat)
            throw new InvalidOperationException(
                $"NDIVideoReceiver delivers {_format.PixelFormat} only; output requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? next;
        while (!_queue.TryDequeue(out next))
        {
            if (_disposed || _faultEx is not null)
            {
                frame = null!;
                return false;
            }

            lock (_waitGate)
            {
                if (_queue.IsEmpty && !_disposed && _faultEx is null)
                    Monitor.Wait(_waitGate, 100);
            }
        }

        frame = next;
        return true;
    }

    private void CaptureLoop(CancellationToken token)
    {
        // Background capture must never crash the host. A native/unpack error records a terminal fault,
        // wakes any blocked reader, and surfaces via Fault / Faulted / IsExhausted instead of escaping
        // the thread and terminating the process.
        try
        {
            CaptureLoopCore(token);
        }
        catch (Exception ex)
        {
            _faultEx = ex;
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDIVideoReceiver.CaptureLoop faulted");
#else
            _ = ex;
#endif
            lock (_waitGate)
                Monitor.PulseAll(_waitGate);
            try { Faulted?.Invoke(ex); } catch { /* subscriber best effort */ }
        }
    }

    private void CaptureLoopCore(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frameType = _receiver.Capture(out var video, out var audio, out var metadata, timeoutMs: 100);

            if (frameType == NDIFrameType.Video)
            {
                try
                {
                    // Hold _ptsLock across unpack+enqueue+increment so a concurrent RebaseToLatest
                    // cannot observe an in-flight stale-PTS frame: either it runs before us (we then
                    // read _nextPts = 0) or after us (it drains the just-enqueued frame).
                    lock (_ptsLock)
                    {
                        var pts = ResolvePresentationTime(in video);
                        if (NDIVideoFrameUnpack.TryUnpack(video, pts, out var vf) && vf is not null)
                        {
                            EnsureFormat(vf.Format);
                            EnqueueFrame(vf);
                        }
                        else
                        {
                            var drops = Interlocked.Increment(ref _unpackDrops);
                            if (drops <= 5)
                            {
                                MediaDiagnostics.LogWarning(
                                    "NDIVideoReceiver: dropped video frame (unpack failed) fourCC={FourCc} size={W}x{H} stride={Stride}",
                                    video.FourCC, video.Xres, video.Yres, video.LineStrideInBytes);
                            }
                        }
                    }
                }
                finally
                {
                    _receiver.FreeVideo(video);
                }
            }
            else if (frameType == NDIFrameType.Audio)
            {
                _receiver.FreeAudio(audio);
            }
            else if (frameType == NDIFrameType.Metadata)
            {
                _receiver.FreeMetadata(metadata);
            }
        }
    }

    private void EnsureFormat(VideoFormat format)
    {
        if (_hasFormat && _format.PixelFormat == format.PixelFormat
                       && _format.Width == format.Width && _format.Height == format.Height)
            return;

        _format = format;
        _native = [format.PixelFormat];
        _ptsStep = format.FrameRate.Denominator > 0 && format.FrameRate.ToDouble() > 0
            ? TimeSpan.FromSeconds(1.0 / format.FrameRate.ToDouble())
            : TimeSpan.FromMilliseconds(33);
        if (_hasLastResolvedPts)
            _nextPts = _lastResolvedPts + _ptsStep;
        _hasFormat = true;
    }

    private TimeSpan ResolvePresentationTime(in NDIVideoFrameV2 video)
    {
        if (NDIFrameTiming.TryMapPresentationTime(
                video.Timecode,
                video.Timestamp,
                ref _ndiTimingOriginTicks,
                ref _ndiTimingOriginSet,
                out var relative))
        {
            var pts = _rebaseBasePts + relative;
            _lastResolvedPts = pts;
            _hasLastResolvedPts = true;
            _nextPts = pts + _ptsStep;
            return pts;
        }

        var synthetic = _nextPts;
        _lastResolvedPts = synthetic;
        _hasLastResolvedPts = true;
        _nextPts += _ptsStep;
        return synthetic;
    }

    private void EnqueueFrame(VideoFrame frame)
    {
        while (_queue.Count >= _maxQueued && _queue.TryDequeue(out var old))
        {
            old.Dispose();
            Interlocked.Increment(ref _overflowFrames);
        }

        _queue.Enqueue(frame);
        lock (_waitGate)
            Monitor.PulseAll(_waitGate);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* best effort */ }
        try { CooperativePlaybackJoin.JoinThread(_captureThread, TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        var captureStopped = !_captureThread.IsAlive;
        if (captureStopped)
        {
            try { _ingestClock?.NotifyCaptureStopped(); } catch { /* best effort */ }
        }
        if (captureStopped)
        {
            try { _cts.Dispose(); } catch { /* best effort */ }
        }
        else
        {
            _faultEx ??= new TimeoutException("NDIVideoReceiver capture thread did not exit during Dispose; native receiver/runtime were intentionally leaked.");
            Trace.LogError(_faultEx, "NDIVideoReceiver.Dispose: capture thread still alive after join cap; leaking native receiver/runtime and CTS to avoid use-after-dispose.");
        }

        while (_queue.TryDequeue(out var f))
            f.Dispose();

        lock (_waitGate)
            Monitor.PulseAll(_waitGate);

        if (captureStopped)
        {
            try { _receiver.Dispose(); } catch { /* best effort */ }
            try { _runtime.Dispose(); } catch { /* best effort */ }
        }
    }
}
