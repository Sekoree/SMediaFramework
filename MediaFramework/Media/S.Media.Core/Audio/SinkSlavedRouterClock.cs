namespace S.Media.Core.Audio;

/// <summary>
/// Pacing source that defers to a specific sink's <see cref="IClockedSink.WaitForCapacity"/>.
/// Falls back to a wall-clock impl when the slaved sink is missing or hasn't
/// implemented <see cref="IClockedSink"/> — so dynamically removing the
/// slaved sink doesn't stall the router.
/// </summary>
/// <remarks>
/// The sink lookup is a callback so the router can resolve from its current
/// (immutable) state on each tick — additions and removals take effect on the
/// next chunk without re-binding the clock.
/// <para>
/// The wall-clock fallback is created <strong>lazily</strong> the first time
/// <see cref="Reset"/> runs or the first time a tick needs it, using the
/// <paramref name="sampleRate"/> and <paramref name="chunkSamples"/> captured
/// here (so it always matches the owning router's current rate).
/// </para>
/// <para>
/// <paramref name="resolveSink"/> runs on the router producer thread once per
/// chunk. Avoid blocking on the same lock the router holds while mutating the
/// graph; read a lock-free snapshot (as <see cref="AudioRouter"/> does via
/// <c>Volatile.Read</c> of its immutable state) so <see cref="SlaveTo"/> /
/// <see cref="RetargetSlaveClock"/> cannot deadlock with the run loop.
/// </para>
/// </remarks>
public sealed class SinkSlavedRouterClock : IRouterClock
{
    private readonly object _fallbackGate = new();
    private readonly Func<IClockedSink?> _resolveSink;
    private readonly int _sampleRate;
    private readonly int _chunkSamples;
    private WallClockRouterClock? _lazyFallback;

    /// <remarks>
    /// <strong>Invariant — ctor must not invoke any <see cref="AudioRouter"/> API.</strong>
    /// <see cref="AudioRouter.SlaveTo"/>, <see cref="AudioRouter.RetargetSlaveClock"/>, and
    /// <see cref="AudioRouter.ReconfigureSampleRateWhileRunning"/> construct the clock while
    /// holding the router's internal <c>_gate</c> lock; calling back into the router from
    /// here would deadlock the run loop. Keep this ctor field-store-only — defer all router
    /// interaction to <paramref name="resolveSink"/>, which runs lock-free on the producer
    /// thread.
    /// </remarks>
    public SinkSlavedRouterClock(int sampleRate, int chunkSamples, Func<IClockedSink?> resolveSink)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        ArgumentNullException.ThrowIfNull(resolveSink);
        _sampleRate = sampleRate;
        _chunkSamples = chunkSamples;
        _resolveSink = resolveSink;
    }

    public void Reset()
    {
        lock (_fallbackGate)
        {
            _lazyFallback ??= new WallClockRouterClock(_sampleRate, _chunkSamples);
            _lazyFallback.Reset();
        }
    }

    public bool WaitForNextChunk(CancellationToken token)
    {
        var sink = _resolveSink();
        if (sink is not null)
            return sink.WaitForCapacity(_chunkSamples, token);

        WallClockRouterClock fb;
        lock (_fallbackGate)
            fb = _lazyFallback ??= new WallClockRouterClock(_sampleRate, _chunkSamples);
        return fb.WaitForNextChunk(token);
    }
}
