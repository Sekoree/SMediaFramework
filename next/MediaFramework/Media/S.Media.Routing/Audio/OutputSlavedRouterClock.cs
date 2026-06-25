using Microsoft.Extensions.Logging;

namespace S.Media.Routing;

/// <summary>
/// Pacing source that defers to a specific output's <see cref="IClockedOutput.WaitForCapacity"/>.
/// Falls back to a wall-clock impl when the slaved output is missing or hasn't
/// implemented <see cref="IClockedOutput"/> — so dynamically removing the
/// slaved output doesn't stall the router.
/// </summary>
/// <remarks>
/// The output lookup is a callback so the router can resolve from its current
/// (immutable) state on each tick — additions and removals take effect on the
/// next chunk without re-binding the clock.
/// <para>
/// The wall-clock fallback is created <strong>lazily</strong> the first time
/// <see cref="Reset"/> runs or the first time a tick needs it, using the
/// <paramref name="sampleRate"/> and <paramref name="chunkSamples"/> captured
/// here (so it always matches the owning router's current rate).
/// </para>
/// <para>
/// <paramref name="resolveOutput"/> runs on the router producer thread once per
/// chunk. Avoid blocking on the same lock the router holds while mutating the
/// graph; read a lock-free snapshot (as <see cref="AudioRouter"/> does via
/// <c>Volatile.Read</c> of its immutable state) so <see cref="SlaveTo"/> /
/// <see cref="RetargetSlaveClock"/> cannot deadlock with the run loop.
/// </para>
/// </remarks>
internal sealed class OutputSlavedRouterClock : IRouterClock
{
    private readonly object _fallbackGate = new();
    private readonly Func<IClockedOutput?> _resolveOutput;
    private readonly int _sampleRate;
    private readonly int _chunkSamples;
    private WallClockRouterClock? _lazyFallback;
    private int _consecutiveFallbacks;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Audio.OutputSlavedRouterClock");

    /// <remarks>
    /// <strong>Invariant — ctor must not invoke any <see cref="AudioRouter"/> API.</strong>
    /// <see cref="AudioRouter.SlaveTo"/>, <see cref="AudioRouter.RetargetSlaveClock"/>, and
    /// <see cref="AudioRouter.ReconfigureSampleRateWhileRunning"/> construct the clock while
    /// holding the router's internal <c>_gate</c> lock; calling back into the router from
    /// here would deadlock the run loop. Keep this ctor field-store-only — defer all router
    /// interaction to <paramref name="resolveOutput"/>, which runs lock-free on the producer
    /// thread.
    /// </remarks>
    public OutputSlavedRouterClock(int sampleRate, int chunkSamples, Func<IClockedOutput?> resolveOutput)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        ArgumentNullException.ThrowIfNull(resolveOutput);
        _sampleRate = sampleRate;
        _chunkSamples = chunkSamples;
        _resolveOutput = resolveOutput;
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
        var output = _resolveOutput();
        if (output is not null)
        {
            if (Interlocked.Exchange(ref _consecutiveFallbacks, 0) > 0)
                Trace.LogDebug("WaitForNextChunk: slaved output resolved again (recovered from wall-clock fallback)");
            return output.WaitForCapacity(_chunkSamples, token);
        }

        var fallbacks = Interlocked.Increment(ref _consecutiveFallbacks);
        // Log on first fallback and every ~5 seconds afterwards (~500 chunks at 480/48k).
        if (fallbacks == 1 || fallbacks % 500 == 0)
            Trace.LogWarning("WaitForNextChunk: slaved output unresolvable — falling back to wall clock (consecutiveFallbacks={Count})", fallbacks);

        WallClockRouterClock fb;
        lock (_fallbackGate)
            fb = _lazyFallback ??= new WallClockRouterClock(_sampleRate, _chunkSamples);
        return fb.WaitForNextChunk(token);
    }
}
