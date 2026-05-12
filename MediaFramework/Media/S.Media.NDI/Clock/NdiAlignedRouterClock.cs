using S.Media.Core.Audio;

namespace S.Media.NDI.Clock;

/// <summary>
/// <see cref="IRouterClock"/> façade over <see cref="WallClockRouterClock"/> for NDI output scenarios.
/// </summary>
/// <remarks>
/// Today this is a straight delegate to wall pacing. If the NDI SDK exposes a stricter send cadence
/// API suitable for driving <see cref="AudioRouter"/>, replace <see cref="WaitForNextChunk"/> with
/// SDK-aware pacing while keeping the same surface for hosts.
/// </remarks>
public sealed class NdiAlignedRouterClock : IRouterClock
{
    private readonly WallClockRouterClock _inner;

    public NdiAlignedRouterClock(int sampleRate, int chunkSamples) =>
        _inner = new WallClockRouterClock(sampleRate, chunkSamples);

    public void Reset() => _inner.Reset();

    public bool WaitForNextChunk(CancellationToken token) => _inner.WaitForNextChunk(token);
}
