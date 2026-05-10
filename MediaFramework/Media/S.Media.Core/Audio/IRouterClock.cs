namespace S.Media.Core.Audio;

/// <summary>
/// Pacing source for <see cref="AudioRouter"/>'s chunk loop. The router calls
/// <see cref="WaitForNextChunk"/> once per chunk and produces samples whenever
/// it returns true.
/// </summary>
/// <remarks>
/// Two implementations ship with the framework:
/// <list type="bullet">
///   <item><see cref="WallClockRouterClock"/> — free-running stopwatch deadline,
///   appropriate when no sink can authoritatively pace the producer.</item>
///   <item><see cref="SinkSlavedRouterClock"/> — defers to an
///   <see cref="IClockedSink"/> looked up by ID, with a wall-clock fallback so
///   removing the slaved sink mid-run doesn't stall the router.</item>
/// </list>
/// Implementations are called from a single thread and don't need to be
/// thread-safe themselves.
/// </remarks>
public interface IRouterClock
{
    /// <summary>Called once when the router starts. Reset any internal pacing state.</summary>
    void Reset();

    /// <summary>
    /// Block until the router should produce its next chunk. Returns true to
    /// proceed, false when <paramref name="token"/> is cancelled.
    /// </summary>
    bool WaitForNextChunk(CancellationToken token);
}
