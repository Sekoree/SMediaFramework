using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace S.Media.Stream.Http;

/// <summary>
/// Broadcasts the live MPEG-TS byte stream to N HTTP clients. The mux sink feeds
/// <see cref="OnBytes"/>/<see cref="OnPacketBoundary"/> (single writer - its drain thread); clients
/// join at the most recent VIDEO KEYFRAME boundary (recorded from the mux's per-packet flush
/// notifications - no TS parsing) so decoders sync fast, then live-follow through a bounded per-client
/// channel. A client that can't keep up is evicted (its channel closes), never buffered unboundedly.
/// </summary>
internal sealed class TsFanOutBuffer
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Stream.Http.TsFanOutBuffer");

    private const long RollingCapacityBytes = 8 * 1024 * 1024;
    private const int ClientQueueChunks = 256;

    private readonly Lock _gate = new();
    private readonly Queue<(long Offset, byte[] Bytes)> _chunks = new();
    private readonly List<Client> _clients = [];
    private long _totalBytes;      // absolute offset of the stream end
    private long _bufferedBytes;   // bytes currently held in _chunks
    private long _joinOffset;      // absolute offset a new client starts from (last keyframe boundary)
    private long _lastBoundaryOffset; // absolute offset after the previously written packet
    private long _evictedClients;

    private sealed class Client(Channel<byte[]> channel)
    {
        public readonly Channel<byte[]> Channel = channel;
        public bool Dead;
    }

    public long TotalBytes { get { lock (_gate) return _totalBytes; } }

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    public long EvictedClients => Volatile.Read(ref _evictedClients);

    /// <summary>Mux byte feed (sink drain thread).</summary>
    public void OnBytes(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        var copy = bytes.ToArray();
        lock (_gate)
        {
            _chunks.Enqueue((_totalBytes, copy));
            _totalBytes += copy.Length;
            _bufferedBytes += copy.Length;
            while (_bufferedBytes > RollingCapacityBytes && _chunks.Count > 1)
            {
                // Never discard the newest video-keyframe join point. A temporarily oversized window
                // is preferable to handing a new decoder a non-decodable mid-GOP prefix.
                var oldest = _chunks.Peek();
                if (_joinOffset > 0 && oldest.Offset + oldest.Bytes.Length > _joinOffset)
                    break;
                var victim = _chunks.Dequeue();
                _bufferedBytes -= victim.Bytes.Length;
            }

            foreach (var client in _clients)
            {
                if (client.Dead)
                    continue;
                if (!client.Channel.Writer.TryWrite(copy))
                {
                    client.Dead = true;
                    client.Channel.Writer.TryComplete();
                    Interlocked.Increment(ref _evictedClients);
                    Trace.LogWarning("TS client evicted: cannot keep up ({Queued} chunks behind)", ClientQueueChunks);
                }
            }

            _clients.RemoveAll(c => c.Dead);
        }
    }

    /// <summary>Mux packet-boundary feed (sink drain thread; after the packet's bytes were flushed).
    /// A key video packet occupied [previous boundary, stream end) - that start is the next join point.</summary>
    public void OnPacketBoundary(bool videoKeyframe)
    {
        lock (_gate)
        {
            if (videoKeyframe)
                _joinOffset = _lastBoundaryOffset;
            _lastBoundaryOffset = _totalBytes;
        }
    }

    /// <summary>Registers a client: returns a reader primed with history from the last keyframe join
    /// point, then live-following. Call <see cref="Unregister"/> when the connection ends.</summary>
    public ChannelReader<byte[]> Register(out object registration)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueueChunks)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite=false on full → eviction above
        });
        var client = new Client(channel);
        lock (_gate)
        {
            // Coalesce the complete history into one channel item. Previously, a history longer than
            // ClientQueueChunks wrote only its oldest prefix, then switched straight to the live tail,
            // creating a decoder-corrupting byte discontinuity.
            var historyChunks = new List<byte[]>();
            foreach (var (offset, bytes) in _chunks)
            {
                if (offset + bytes.Length <= _joinOffset)
                    continue;
                var skip = Math.Max(0, _joinOffset - offset);
                historyChunks.Add(skip > 0 ? bytes.AsSpan((int)skip).ToArray() : bytes);
            }
            if (historyChunks.Count <= ClientQueueChunks)
            {
                foreach (var chunk in historyChunks)
                    channel.Writer.TryWrite(chunk);
            }
            else
            {
                // Coalesce into one exact-size array filled once. The old MemoryStream + ToArray
                // allocated the whole history twice (internal buffer plus the copy); the join copy
                // still runs under _gate to stay ordered ahead of the live tail, but the mux drain
                // thread's OnBytes is blocked for half as much allocation work.
                var total = 0;
                foreach (var chunk in historyChunks)
                    total += chunk.Length;
                var coalesced = new byte[total];
                var offset = 0;
                foreach (var chunk in historyChunks)
                {
                    Buffer.BlockCopy(chunk, 0, coalesced, offset, chunk.Length);
                    offset += chunk.Length;
                }
                channel.Writer.TryWrite(coalesced);
            }

            _clients.Add(client);
        }

        registration = client;
        return channel.Reader;
    }

    public void Unregister(object registration)
    {
        if (registration is not Client client)
            return;
        lock (_gate)
        {
            client.Dead = true;
            client.Channel.Writer.TryComplete();
            _clients.Remove(client);
        }
    }

    /// <summary>End of stream: completes every client channel so readers drain and disconnect.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            foreach (var client in _clients)
                client.Channel.Writer.TryComplete();
            _clients.Clear();
        }
    }
}
