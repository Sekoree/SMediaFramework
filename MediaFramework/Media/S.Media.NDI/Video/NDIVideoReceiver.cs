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
    private const int DefaultQueueDepth = 8;
    private const int CaptureTimeoutMs = 16;
    private const int ReadWaitMs = 16;

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly bool _wallClockPresentation;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<VideoFrame> _queue = new();
    private readonly object _waitGate = new();
    private readonly int _maxQueued;

    private VideoFormat _format;
    private PixelFormat[] _native = [];
    private long _ptsOriginTicks;
    private bool _ptsOriginSet;
    private long _syntheticPtsTicks;
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

    /// <summary>Shared ingest clock when this receiver is paired with <see cref="Audio.NDIAudioReceiver"/>.</summary>
    public NDIIngestPlaybackClock? IngestClock => _ingestClock;

    public NDIVideoReceiver(
        NDIDiscoveredSource source,
        string? receiverName = null,
        int maxQueuedFrames = DefaultQueueDepth,
        NDIIngestPlaybackClock? ingestClock = null,
        bool wallClockPresentation = false)
    {
        if (maxQueuedFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedFrames));

        _maxQueued = maxQueuedFrames;
        _ingestClock = ingestClock;
        _wallClockPresentation = wallClockPresentation && ingestClock is not null;
        _ingestClock?.AttachReceiver();

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            // Prefer BGRA from the SDK so live UI sinks use the same packed path as file/NDI output.
            // UYVY is still unpacked when a sender delivers it natively (see NDIVideoFrameUnpack).
            var settings = new NDIReceiverSettings
            {
                ReceiverName = receiverName,
                ColorFormat = NDIRecvColorFormat.BgrxBgra,
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

    /// <summary>
    /// Clears queued frames and re-anchors presentation timestamps at the next captured frame.
    /// Call when transport starts so pre-buffered frames (captured while waiting to play) do not
    /// present as permanently late relative to the playhead.
    /// </summary>
    public void ResetPlaybackTimeline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_queue.TryDequeue(out var old))
            old.Dispose();

        _ptsOriginSet = false;
        _syntheticPtsTicks = 0;
        _ingestClock?.AttachReceiver();

        lock (_waitGate)
            Monitor.PulseAll(_waitGate);
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
        while (true)
        {
            if (_queue.TryDequeue(out var dequeued))
            {
                frame = dequeued;
                return true;
            }

            if (_disposed)
            {
                frame = null!;
                return false;
            }

            lock (_waitGate)
            {
                if (_queue.IsEmpty && !_disposed)
                    Monitor.Wait(_waitGate, ReadWaitMs);
            }
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frameType = _receiver.Capture(out var video, out var audio, out var metadata, timeoutMs: CaptureTimeoutMs);

            if (frameType == NDIFrameType.Video)
            {
                try
                {
                    var pts = MapPresentationTime(in video);
                    _ingestClock?.NotifyVideoFrame(ref video);
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

    private TimeSpan MapPresentationTime(in NDIVideoFrameV2 video)
    {
        if (_wallClockPresentation)
            return _ingestClock!.SnapshotWallPresentationPosition();

        if (NDIFrameTiming.TryMapPresentationTime(
                video.Timecode,
                video.Timestamp,
                ref _ptsOriginTicks,
                ref _ptsOriginSet,
                out var pts))
            return pts;

        var step = NDIFrameTiming.FrameDurationTicks(video.FrameRateN, video.FrameRateD);
        var synthetic = TimeSpan.FromTicks(_syntheticPtsTicks);
        _syntheticPtsTicks += step;
        return synthetic;
    }

    private void EnsureFormat(VideoFormat format)
    {
        if (_hasFormat && _format.PixelFormat == format.PixelFormat
                       && _format.Width == format.Width && _format.Height == format.Height)
            return;

        _format = format;
        _native = [format.PixelFormat];
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
