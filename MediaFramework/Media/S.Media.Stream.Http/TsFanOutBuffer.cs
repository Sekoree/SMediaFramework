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
    private readonly long _rollingCapacityBytes;
    private readonly Action? _historySnapshotCaptured;
    private readonly Queue<(long Offset, byte[] Bytes)> _chunks = new();
    private readonly List<Client> _clients = [];
    private long _totalBytes;      // absolute offset of the stream end
    private long _bufferedBytes;   // bytes currently held in _chunks
    private long _joinOffset;      // absolute offset a new client starts from (last keyframe boundary)
    private long _lastBoundaryOffset; // absolute offset after the previously written packet
    private long _evictedClients;
    private bool _joinOffsetValid = true; // stream offset zero is a valid start until its bytes are evicted

    internal TsFanOutBuffer(
        long rollingCapacityBytes = RollingCapacityBytes,
        Action? historySnapshotCaptured = null)
    {
        if (rollingCapacityBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(rollingCapacityBytes), "must be >= 1");
        _rollingCapacityBytes = rollingCapacityBytes;
        _historySnapshotCaptured = historySnapshotCaptured;
    }

    private sealed class Client(Channel<byte[]> channel)
    {
        public readonly Channel<byte[]> Channel = channel;
        public readonly List<byte[]> PendingLive = [];
        public int ReservedHistorySlots;
        public bool Priming = true;
        public bool Dead;
    }

    public long TotalBytes { get { lock (_gate) return _totalBytes; } }

    internal long BufferedBytes { get { lock (_gate) return _bufferedBytes; } }

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
            while (_bufferedBytes > _rollingCapacityBytes && _chunks.Count > 1)
            {
                var victim = _chunks.Dequeue();
                _bufferedBytes -= victim.Bytes.Length;
                if (_joinOffsetValid && victim.Offset + victim.Bytes.Length > _joinOffset)
                {
                    // A malformed/very-long GOP must not turn the rolling window into an unbounded
                    // allocation. Once the newest keyframe itself has aged out, new clients receive no
                    // history until another keyframe establishes a decodable join point.
                    _joinOffsetValid = false;
                }
            }

            foreach (var client in _clients)
            {
                if (client.Dead)
                    continue;
                if (client.Priming)
                {
                    // Registration materializes its immutable history snapshot outside _gate. Retain the live
                    // tail separately until history has been inserted first; the combined items must still fit
                    // the same bounded client channel or the client is evicted.
                    if (client.PendingLive.Count >= ClientQueueChunks - client.ReservedHistorySlots)
                    {
                        EvictClient(client);
                    }
                    else
                    {
                        client.PendingLive.Add(copy);
                    }
                    continue;
                }
                if (!client.Channel.Writer.TryWrite(copy))
                {
                    EvictClient(client);
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
            {
                _joinOffset = _lastBoundaryOffset;
                _joinOffsetValid = true;
            }
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
        List<(byte[] Bytes, int Skip)> historySnapshot;
        lock (_gate)
        {
            // Capture immutable byte-array references and register the client immediately. While the snapshot
            // is materialized outside this lock, OnBytes stages the live tail in PendingLive.
            historySnapshot = [];
            var joinOffset = _joinOffsetValid ? _joinOffset : _totalBytes;
            foreach (var (offset, bytes) in _chunks)
            {
                if (offset + bytes.Length <= joinOffset)
                    continue;
                var skip = Math.Max(0, joinOffset - offset);
                historySnapshot.Add((bytes, (int)skip));
            }

            client.ReservedHistorySlots = historySnapshot.Count <= ClientQueueChunks
                ? historySnapshot.Count
                : 1;
            _clients.Add(client);
        }

        registration = client;
        IReadOnlyList<byte[]> historyItems;
        try
        {
            _historySnapshotCaptured?.Invoke(); // test seam; deliberately outside _gate
            historyItems = MaterializeHistory(historySnapshot);
        }
        catch
        {
            Unregister(client);
            throw;
        }

        lock (_gate)
        {
            if (!client.Dead)
            {
                foreach (var item in historyItems)
                {
                    if (!channel.Writer.TryWrite(item))
                    {
                        EvictClient(client);
                        break;
                    }
                }

                if (!client.Dead)
                {
                    foreach (var item in client.PendingLive)
                    {
                        if (!channel.Writer.TryWrite(item))
                        {
                            EvictClient(client);
                            break;
                        }
                    }
                }

                client.PendingLive.Clear();
                client.Priming = false;
            }

            _clients.RemoveAll(c => c.Dead);
        }

        return channel.Reader;
    }

    private static IReadOnlyList<byte[]> MaterializeHistory(List<(byte[] Bytes, int Skip)> snapshot)
    {
        if (snapshot.Count == 0)
            return [];
        if (snapshot.Count <= ClientQueueChunks)
        {
            var items = new byte[snapshot.Count][];
            for (var i = 0; i < snapshot.Count; i++)
            {
                var (bytes, skip) = snapshot[i];
                items[i] = skip == 0 ? bytes : bytes.AsSpan(skip).ToArray();
            }
            return items;
        }

        // A long history must be one channel item so the bounded queue never contains only its prefix.
        // The exact-size allocation/copy can be several MiB, hence it intentionally runs outside _gate.
        var total = 0;
        foreach (var (bytes, skip) in snapshot)
            total = checked(total + bytes.Length - skip);
        var coalesced = new byte[total];
        var destinationOffset = 0;
        foreach (var (bytes, skip) in snapshot)
        {
            var length = bytes.Length - skip;
            Buffer.BlockCopy(bytes, skip, coalesced, destinationOffset, length);
            destinationOffset += length;
        }
        return [coalesced];
    }

    private void EvictClient(Client client)
    {
        if (client.Dead)
            return;
        client.Dead = true;
        client.Channel.Writer.TryComplete();
        Interlocked.Increment(ref _evictedClients);
        Trace.LogWarning("TS client evicted: cannot keep up ({Queued} chunks behind)", ClientQueueChunks);
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
            {
                client.Dead = true;
                client.Channel.Writer.TryComplete();
            }
            _clients.Clear();
        }
    }
}
