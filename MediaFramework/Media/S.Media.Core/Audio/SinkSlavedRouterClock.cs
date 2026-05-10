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
/// </remarks>
public sealed class SinkSlavedRouterClock : IRouterClock
{
    private readonly Func<IClockedSink?> _resolveSink;
    private readonly int _chunkSamples;
    private readonly IRouterClock _fallback;

    public SinkSlavedRouterClock(int chunkSamples, Func<IClockedSink?> resolveSink, IRouterClock fallback)
    {
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        ArgumentNullException.ThrowIfNull(resolveSink);
        ArgumentNullException.ThrowIfNull(fallback);
        _chunkSamples = chunkSamples;
        _resolveSink = resolveSink;
        _fallback = fallback;
    }

    public void Reset() => _fallback.Reset();

    public bool WaitForNextChunk(CancellationToken token)
    {
        var sink = _resolveSink();
        return sink is null
            ? _fallback.WaitForNextChunk(token)
            : sink.WaitForCapacity(_chunkSamples, token);
    }
}
