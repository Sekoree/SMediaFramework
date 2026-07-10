using System.Diagnostics;
using System.Threading;
using S.Media.Decode.FFmpeg.Diagnostics;

namespace S.Media.Decode.FFmpeg.Video;

/// <summary>
/// Handle from <see cref="PassThroughDescriptorArena.Rent"/>; pass to <see cref="PassThroughDescriptorArena.Return"/> when the
/// <see cref="VideoFrame"/> release callback runs (may be off-thread vs decode).
/// </summary>
internal readonly struct PassThroughRentHandle
{
    public readonly int PlaneCount;
    public readonly ReadOnlyMemory<byte>[] Planes;
    public readonly int[] Strides;
    /// <summary>True when <see cref="Planes"/> / <see cref="Strides"/> belong to the arena's fixed pool for this plane count.</summary>
    public readonly bool FromFixedPool;
    /// <summary>Index into the fixed pool when <see cref="FromFixedPool"/> is true; otherwise undefined.</summary>
    public readonly byte SlotIndex;

    public PassThroughRentHandle(int planeCount, ReadOnlyMemory<byte>[] planes, int[] strides, bool fromFixedPool, byte slotIndex)
    {
        PlaneCount = planeCount;
        Planes = planes;
        Strides = strides;
        FromFixedPool = fromFixedPool;
        SlotIndex = slotIndex;
    }
}

/// <summary>
/// Pooled paired <see cref="ReadOnlyMemory{T}"/> / stride arrays for libav pass-through <see cref="VideoFrame"/> metadata.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Rent"/> runs on the decode thread; <see cref="Return"/> may run from any thread when <see cref="VideoFrame.Dispose"/> fires.
/// Each plane-count bucket keeps up to <see cref="PoolCap"/> slots in a fixed array; a <strong>Treiber stack</strong> of free slot indices
/// (<c>Interlocked.CompareExchange</c> on the head) hands out pooled pairs without a <see cref="Lock"/>. On a miss, <see cref="Rent"/>
/// allocates fresh arrays (same as an exhausted pool); those are never pushed back (no promotion), so the pool stays bounded.
/// <c>Array.Clear</c> on the return path runs before any pool interaction so pooled arrays never retain stale plane views.
/// </para>
/// <para>
/// <see cref="Dispose"/> sets a flag so <see cref="Return"/> becomes a no-op after clearing (no push); <see cref="Rent"/> throws
/// <see cref="ObjectDisposedException"/>. On the rare path where the dispose transition or profiling bookkeeping throws,
/// <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/>; <strong>Release</strong> builds continue best-effort
/// (same policy as <see cref="VideoRouter.Dispose"/> teardown).
/// </para>
/// <para>
/// The fixed-pool free lists are Treiber stacks (no mutex on pop/push). Optional profiling via <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c>
/// (<see cref="PassThroughArenaProfiling.TreiberCasRetries"/> / wall timers). If contention persists, set
/// <c>MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE=1</c> to take a per-arena mutex around rent/return/dispose ordering
/// (<see cref="PassThroughArenaSerialization"/>) - trades throughput for determinism.
/// </para>
/// </remarks>
internal sealed class PassThroughDescriptorArena : IDisposable
{
    public const int PoolCap = 32;

    /// <summary>Max <see cref="PixelFormatInfo.PlaneCount"/> today (e.g. YUVA420p = 4).</summary>
    public const int MaxPlaneCount = 4;

    private readonly PlaneCountPool?[] _pools = new PlaneCountPool?[MaxPlaneCount + 1];
    private int _disposed;
    private readonly object _serializationGate = new();

    public PassThroughDescriptorArena()
    {
        for (var i = 1; i <= MaxPlaneCount; i++)
            _pools[i] = new PlaneCountPool(i);
    }

    public PassThroughRentHandle Rent(int planeCount)
    {
        ValidatePlaneCount(planeCount);

        var profile = PassThroughArenaProfiling.IsEnabled;
        long t0 = 0;
        if (profile)
            t0 = Stopwatch.GetTimestamp();
        try
        {
            if (PassThroughArenaSerialization.IsEnabled)
            {
                lock (_serializationGate)
                    return RentUnlocked(planeCount, profile);
            }

            return RentUnlocked(planeCount, profile);
        }
        finally
        {
            if (profile)
                PassThroughArenaProfiling.RecordRent(Stopwatch.GetTimestamp() - t0);
        }
    }

    public void Return(in PassThroughRentHandle handle)
    {
        Array.Clear(handle.Planes);
        Array.Clear(handle.Strides);

        var profile = PassThroughArenaProfiling.IsEnabled;
        long t0 = 0;
        if (profile)
            t0 = Stopwatch.GetTimestamp();
        try
        {
            if (PassThroughArenaSerialization.IsEnabled)
            {
                lock (_serializationGate)
                    ReturnUnlocked(in handle, profile);
            }
            else
                ReturnUnlocked(in handle, profile);
        }
        finally
        {
            if (profile)
                PassThroughArenaProfiling.RecordReturn(Stopwatch.GetTimestamp() - t0);
        }
    }

    public void Dispose()
    {
        var profile = PassThroughArenaProfiling.IsEnabled;
        long t0 = 0;
        if (profile)
            t0 = Stopwatch.GetTimestamp();
        try
        {
            if (PassThroughArenaSerialization.IsEnabled)
            {
                lock (_serializationGate)
                {
                    if (!TryAcquireDisposeTransition())
                        return;
                }
            }
            else
            {
                if (!TryAcquireDisposeTransition())
                    return;
            }
        }
        finally
        {
            if (profile)
            {
                MediaDiagnostics.SwallowDisposeErrors(() => PassThroughArenaProfiling.RecordClear(Stopwatch.GetTimestamp() - t0), "PassThroughDescriptorArena.Dispose: RecordClear");
            }
        }
    }

    private bool TryAcquireDisposeTransition()
    {
        try
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0;
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "PassThroughDescriptorArena.Dispose: dispose transition");
            return false;
        }
#else
        catch
        {
            return false;
        }
#endif
    }

    private PassThroughRentHandle RentUnlocked(int planeCount, bool profile)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PassThroughDescriptorArena));

        var pool = _pools[planeCount]!;
        if (pool.TryPop(out var slotIndex, profile))
        {
            return new PassThroughRentHandle(
                planeCount,
                pool.GetPlanes(slotIndex),
                pool.GetStrides(slotIndex),
                fromFixedPool: true,
                (byte)slotIndex);
        }

        return new PassThroughRentHandle(
            planeCount,
            new ReadOnlyMemory<byte>[planeCount],
            new int[planeCount],
            fromFixedPool: false,
            slotIndex: 0);
    }

    private void ReturnUnlocked(in PassThroughRentHandle handle, bool profile)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!handle.FromFixedPool)
            return;

        ValidatePlaneCount(handle.PlaneCount);
        _pools[handle.PlaneCount]!.Push(handle.SlotIndex, profile);
    }

    private static void ValidatePlaneCount(int planeCount)
    {
        if (planeCount is < 1 or > MaxPlaneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(planeCount),
                $"planeCount must be between 1 and {MaxPlaneCount} for the pass-through descriptor arena.");
        }
    }

    /// <summary>Fixed <see cref="PoolCap"/> slots + Treiber stack of free indices (head = first free index, <c>-1</c> = empty).</summary>
    private sealed class PlaneCountPool
    {
        private readonly PooledSlot[] _slots;
        private int _freeHead;

        public PlaneCountPool(int planeCount)
        {
            _slots = new PooledSlot[PoolCap];
            for (var i = 0; i < PoolCap; i++)
            {
                _slots[i].Planes = new ReadOnlyMemory<byte>[planeCount];
                _slots[i].Strides = new int[planeCount];
                _slots[i].NextFree = i + 1 < PoolCap ? i + 1 : -1;
            }

            _freeHead = 0;
        }

        public ReadOnlyMemory<byte>[] GetPlanes(int index) => _slots[index].Planes;

        public int[] GetStrides(int index) => _slots[index].Strides;

        public bool TryPop(out int slotIndex, bool recordCasRetries)
        {
            while (true)
            {
                var head = Volatile.Read(ref _freeHead);
                if (head < 0)
                {
                    slotIndex = 0;
                    return false;
                }

                var next = Volatile.Read(ref _slots[head].NextFree);
                if (Interlocked.CompareExchange(ref _freeHead, next, head) == head)
                {
                    slotIndex = head;
                    return true;
                }

                if (recordCasRetries)
                    PassThroughArenaProfiling.RecordTreiberCasRetry();
            }
        }

        public void Push(int slotIndex, bool recordCasRetries)
        {
            ref var slot = ref _slots[slotIndex];
            while (true)
            {
                var head = Volatile.Read(ref _freeHead);
                slot.NextFree = head;
                if (Interlocked.CompareExchange(ref _freeHead, slotIndex, head) == head)
                    return;

                if (recordCasRetries)
                    PassThroughArenaProfiling.RecordTreiberCasRetry();
            }
        }
    }

    private struct PooledSlot
    {
        public ReadOnlyMemory<byte>[] Planes;
        public int[] Strides;
        public int NextFree;
    }
}
