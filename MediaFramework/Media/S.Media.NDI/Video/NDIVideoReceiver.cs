using System.Collections.Concurrent;
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
public sealed unsafe class NDIVideoReceiver : IVideoSource, IDisposable
{
    private const int DefaultQueueDepth = 4;

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<VideoFrame> _queue = new();
    private readonly object _waitGate = new();
    private readonly int _maxQueued;

    private VideoFormat _format;
    private PixelFormat[] _native = [];
    private TimeSpan _ptsStep = TimeSpan.FromMilliseconds(33);
    private TimeSpan _nextPts;
    private bool _hasFormat;
    private bool _disposed;
    private long _overflowFrames;
    private long _unpackDrops;

    public bool IsConnected => _hasFormat;

    public VideoFormat Format =>
        _hasFormat
            ? _format
            : throw new InvalidOperationException(
                "NDI source has not delivered a video frame yet — wait until IsConnected is true");

    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;

    public bool IsExhausted => _disposed;

    public long OverflowFrames => Interlocked.Read(ref _overflowFrames);

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
                $"NDIVideoReceiver delivers {_format.PixelFormat} only; sink requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_queue.TryDequeue(out frame))
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            lock (_waitGate)
            {
                if (_queue.IsEmpty && !_disposed)
                    Monitor.Wait(_waitGate, 100);
            }
        }

        return true;
    }

    private void CaptureLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frameType = _receiver.Capture(out var video, out var audio, out var metadata, timeoutMs: 100);

            if (frameType == NDIFrameType.Video)
            {
                try
                {
                    if (NDIVideoFrameUnpack.TryUnpack(video, _nextPts, out var vf) && vf is not null)
                    {
                        EnsureFormat(vf.Format);
                        EnqueueFrame(vf);
                        _nextPts += _ptsStep;
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
        _hasFormat = true;
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
        try { _ingestClock?.NotifyCaptureStopped(); } catch { /* best effort */ }
        try { _cts.Dispose(); } catch { /* best effort */ }

        while (_queue.TryDequeue(out var f))
            f.Dispose();

        lock (_waitGate)
            Monitor.PulseAll(_waitGate);

        try { _receiver.Dispose(); } catch { /* best effort */ }
        try { _runtime.Dispose(); } catch { /* best effort */ }
    }
}
