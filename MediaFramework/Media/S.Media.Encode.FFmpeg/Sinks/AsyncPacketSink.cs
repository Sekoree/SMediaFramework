using Microsoft.Extensions.Logging;
using S.Media.Core.Threading;

namespace S.Media.Encode.FFmpeg.Sinks;

/// <summary>
/// Decouples a packet sink from the encode worker with its own bounded queue + drain thread. FFmpeg's
/// network protocols block inside connect/write, so a stalled RTMP/SRT/RTSP push behind this wrapper
/// costs the session nothing - packets are <c>av_packet_ref</c>'d (cheap, refcounted) on the encode
/// worker and the wrapped sink runs entirely on the drain thread. Overflow drops the OLDEST packet
/// runs (counted) - a live push wants fresh data, not a growing backlog. An inner-sink throw faults
/// the wrapper (the session detaches it); the drain thread keeps draining to dispose queued refs.
/// </summary>
internal sealed unsafe class AsyncPacketSink : IEncodedPacketSink
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Encode.FFmpeg.AsyncPacketSink");

    private readonly IEncodedPacketSink _inner;
    private readonly int _maxQueued;
    private readonly Lock _gate = new();
    private readonly Queue<(IntPtr Packet, bool Keyframe)> _queue = new();
    private readonly ManualResetEventSlim _pending = new(false);
    private Thread? _thread;
    private IReadOnlyList<EncodedStreamInfo>? _streams;
    private volatile bool _finishRequested;
    private volatile bool _innerFaulted;
    private bool _disposed;
    private long _dropped;
    private Exception? _innerError;

    public AsyncPacketSink(IEncodedPacketSink inner, int maxQueuedPackets = 256)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxQueued = Math.Max(16, maxQueuedPackets);
    }

    public string Name => _inner.Name;

    public long BytesWritten => _inner.BytesWritten;

    public long DroppedPackets => Interlocked.Read(ref _dropped);

    public void OnStreamsReady(IReadOnlyList<EncodedStreamInfo> streams)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
                throw new InvalidOperationException("OnStreamsReady called twice.");
            _streams = streams;
            _thread = new Thread(DrainLoop)
            {
                IsBackground = true,
                Name = $"EncodeSink:{_inner.Name}",
            };
            _thread.Start();
        }
    }

    public void OnPacket(AVPacket* packet, bool keyframe)
    {
        if (_innerFaulted)
            throw new InvalidOperationException($"sink '{Name}' faulted: {_innerError?.Message}", _innerError);

        var clone = av_packet_alloc();
        if (clone is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");
        var ret = av_packet_ref(clone, packet);
        if (ret < 0)
        {
            av_packet_free(&clone);
            FFmpegException.ThrowIfError(ret, nameof(av_packet_ref));
        }

        var droppedNow = 0;
        lock (_gate)
        {
            if (_disposed || _finishRequested)
            {
                FreePacket((IntPtr)clone);
                return;
            }

            while (_queue.Count >= _maxQueued)
            {
                var victim = _queue.Dequeue();
                FreePacket(victim.Packet);
                Interlocked.Increment(ref _dropped);
                droppedNow++;
            }

            _queue.Enqueue(((IntPtr)clone, keyframe));
            _pending.Set();
        }

        if (droppedNow > 0)
            Trace.LogWarning("push sink '{Name}': backlog full - dropped {Count} oldest packet(s) (slow destination)", Name, droppedNow);
    }

    public void Finish()
    {
        Thread? thread;
        lock (_gate)
        {
            _finishRequested = true;
            _pending.Set();
            thread = _thread;
        }

        if (thread is not null)
            CooperativePlaybackJoin.JoinThread(thread, TimeSpan.FromSeconds(10), CancellationToken.None);

        if (_innerFaulted && _innerError is not null)
            throw new InvalidOperationException($"sink '{Name}' faulted: {_innerError.Message}", _innerError);
    }

    private void DrainLoop()
    {
        try
        {
            if (_streams is { } streams && !TryInner(() => _inner.OnStreamsReady(streams)))
            {
                DrainAndFreeRemaining();
                return;
            }

            while (true)
            {
                _pending.Wait(250);
                while (true)
                {
                    (IntPtr Packet, bool Keyframe) item;
                    lock (_gate)
                    {
                        if (_queue.Count == 0)
                        {
                            _pending.Reset();
                            break;
                        }

                        item = _queue.Dequeue();
                    }

                    var deliver = !_innerFaulted;
                    try
                    {
                        if (deliver)
                            TryInner(() => _inner.OnPacket((AVPacket*)item.Packet, item.Keyframe));
                    }
                    finally
                    {
                        FreePacket(item.Packet);
                    }
                }

                bool finish;
                lock (_gate)
                    finish = (_finishRequested || _disposed) && _queue.Count == 0;
                if (finish)
                    break;
            }

            if (!_innerFaulted)
                TryInner(_inner.Finish);
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "push sink '{Name}': drain loop faulted", Name);
        }
        finally
        {
            DrainAndFreeRemaining();
        }
    }

    private bool TryInner(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            _innerError = ex;
            _innerFaulted = true;
            Trace.LogError(ex, "push sink '{Name}' faulted - packets will be discarded", Name);
            return false;
        }
    }

    private void DrainAndFreeRemaining()
    {
        lock (_gate)
        {
            while (_queue.Count > 0)
                FreePacket(_queue.Dequeue().Packet);
        }
    }

    private static void FreePacket(IntPtr packet)
    {
        var p = (AVPacket*)packet;
        av_packet_free(&p);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _finishRequested = true;
            _pending.Set();
            thread = _thread;
        }

        if (thread is not null)
            CooperativePlaybackJoin.JoinThread(thread, TimeSpan.FromSeconds(5), CancellationToken.None);

        DrainAndFreeRemaining();
        MediaDiagnostics.SwallowDisposeErrors(_inner.Dispose, $"AsyncPacketSink.Dispose: inner sink ({Name})");
        _pending.Dispose();
    }
}
