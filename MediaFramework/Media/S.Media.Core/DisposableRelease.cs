using System.Buffers;
using System.Collections.Concurrent;

namespace S.Media.Core;

/// <summary>Helpers for unified <see cref="IDisposable"/> release callbacks on frames.</summary>
public static class DisposableRelease
{
    /// <summary>Wraps an <see cref="Action"/> as an <see cref="IDisposable"/> (idempotent dispose).</summary>
    public static IDisposable Wrap(Action? release) =>
        release is null ? NoopDisposable.Instance : new ActionDisposable(release);

    /// <summary>Invokes <paramref name="primary"/> then <paramref name="secondary"/> on dispose.</summary>
    public static IDisposable Chain(IDisposable? primary, IDisposable? secondary)
    {
        if (primary is null) return secondary ?? NoopDisposable.Instance;
        if (secondary is null) return primary;
        return new ChainedDisposable(primary, secondary);
    }

    /// <summary>Wraps an action and an <see cref="IDisposable"/> - both run on dispose.</summary>
    public static IDisposable Combine(Action? action, IDisposable? disposable) =>
        Chain(disposable, Wrap(action));

    /// <summary>Disposes <paramref name="inner"/> after <paramref name="count"/> dispose calls.</summary>
    public static IDisposable SharedCountdown(IDisposable inner, int count) =>
        new SharedCountdownDisposable(inner, count);

    /// <summary>Adjusts a <see cref="SharedCountdown"/> when fan-out construction fails partway.</summary>
    internal static void AdjustSharedCountdown(IDisposable countdown, int delta)
    {
        if (countdown is SharedCountdownDisposable shared)
            shared.AddRemaining(delta);
    }

    private sealed class ActionDisposable(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            release();
        }
    }

    private sealed class ChainedDisposable(IDisposable first, IDisposable second) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { first.Dispose(); }
            finally { second.Dispose(); }
        }
    }

    private sealed class SharedCountdownDisposable(IDisposable inner, int count) : IDisposable
    {
        private int _remaining = count;

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                inner.Dispose();
        }

        /// <summary>Adjusts the countdown when fan-out construction fails partway (see <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/>).</summary>
        internal void AddRemaining(int delta) => Interlocked.Add(ref _remaining, delta);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Pooled release state for CPU frames emitted on hot per-frame paths (sws emit, NDI unpack, CPU
/// convert). Owns up to <see cref="MaxPlaneCount"/> <see cref="ArrayPool{T}"/>-rented plane buffers
/// plus the exact-length plane/stride arrays handed to the emitted <see cref="Video.VideoFrame"/>.
/// An emitted frame outlives the emit call (it sits in downstream queues, several in flight), so
/// this state cannot live on the emitter - <see cref="Dispose"/>, wired as the frame's release,
/// returns the plane buffers to the pool and recycles the lease itself onto a bounded per-plane-count
/// free list, making a steady-state emit allocation-free (versus closure + ActionDisposable + three
/// small arrays per frame).
/// </summary>
/// <remarks>
/// The do-not-mutate contract on <see cref="Video.VideoFrame.Planes"/> / <c>Strides</c> holds for the
/// frame's lifetime only: once the release fires the arrays are recycled and a later frame overwrites
/// them, so a disposed frame's plane/stride views are undefined (same class of hazard as its pooled
/// bytes). <see cref="Dispose"/> is idempotent (Interlocked-guarded) and callable from any thread.
/// </remarks>
public sealed class PooledFrameRelease : IDisposable
{
    /// <summary>Largest plane count any CPU pixel format carries (4-plane YUVA).</summary>
    public const int MaxPlaneCount = 4;

    // Bounded free lists keyed by plane count; overflow leases are dropped to the GC instead of
    // growing the cache. FreeCounts approximates each queue's length without O(n) Count calls.
    private const int MaxFreePerPlaneCount = 32;
    private static readonly ConcurrentQueue<PooledFrameRelease>[] FreeLists = [new(), new(), new(), new()];
    private static readonly int[] FreeCounts = new int[MaxPlaneCount];

    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly byte[]?[] _rented;
    private int _disposed;

    private PooledFrameRelease(int planeCount)
    {
        _planes = new ReadOnlyMemory<byte>[planeCount];
        _strides = new int[planeCount];
        _rented = new byte[planeCount][];
    }

    /// <summary>Exact-length plane array to hand to the emitted frame (populated by <see cref="RentPlane"/>).</summary>
    public ReadOnlyMemory<byte>[] Planes => _planes;

    /// <summary>Exact-length stride array to hand to the emitted frame (populated by <see cref="RentPlane"/>).</summary>
    public int[] Strides => _strides;

    public int PlaneCount => _planes.Length;

    /// <summary>Takes a lease for <paramref name="planeCount"/> planes (1..4) off the free list, or allocates one.</summary>
    public static PooledFrameRelease Rent(int planeCount)
    {
        if (planeCount is < 1 or > MaxPlaneCount)
            throw new ArgumentOutOfRangeException(nameof(planeCount), planeCount, "plane count must be 1..4");

        if (FreeLists[planeCount - 1].TryDequeue(out var lease))
        {
            Interlocked.Decrement(ref FreeCounts[planeCount - 1]);
            lease._disposed = 0;
            return lease;
        }

        return new PooledFrameRelease(planeCount);
    }

    /// <summary>
    /// Rents plane <paramref name="index"/> from <see cref="ArrayPool{T}.Shared"/>: exposes its first
    /// <paramref name="length"/> bytes as <see cref="Planes"/>[index], records <paramref name="stride"/>,
    /// and returns the (possibly longer) backing array for pinning/filling. A partially-populated lease
    /// disposes cleanly, so emit sites can roll back with a single <see cref="Dispose"/>.
    /// </summary>
    public byte[] RentPlane(int index, int length, int stride)
    {
        // Zero-length planes (degenerate ABI frames) keep their stride but rent nothing, so
        // Dispose has nothing to return for the slot.
        if (length <= 0)
        {
            _rented[index] = null;
            _planes[index] = default;
            _strides[index] = stride;
            return [];
        }

        var buf = ArrayPool<byte>.Shared.Rent(length);
        _rented[index] = buf;
        _planes[index] = buf.AsMemory(0, length);
        _strides[index] = stride;
        return buf;
    }

    /// <summary>Returns every rented plane to the pool and recycles the lease itself. Idempotent; any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        for (var i = 0; i < _rented.Length; i++)
        {
            var buf = _rented[i];
            _rented[i] = null;
            _planes[i] = default;
            _strides[i] = 0;
            if (buf is not null)
                ArrayPool<byte>.Shared.Return(buf);
        }

        var slot = _planes.Length - 1;
        if (Interlocked.Increment(ref FreeCounts[slot]) <= MaxFreePerPlaneCount)
            FreeLists[slot].Enqueue(this);
        else
            Interlocked.Decrement(ref FreeCounts[slot]);
    }
}
