using System.Diagnostics;

namespace S.Media.Time;

/// <summary>
/// A monotonic, pausable wall clock exposed as an <see cref="IPlaybackClock"/> — the time reference for a
/// <em>live-led</em> <see cref="SessionClock"/> (no file output to slave to). Built on
/// <see cref="Stopwatch"/> so it never goes backwards. In Phase 5 the live ingest disciplines its long-term
/// rate (a PI controller on arrival cadence); for now it free-runs at real time.
/// </summary>
public sealed class MonotonicWallClock : IPlaybackClock
{
    private readonly Stopwatch _sw = new();
    private TimeSpan _accumulated;

    /// <summary>Creates the clock already running (the common live case). Use <see cref="Pause"/> to freeze.</summary>
    public MonotonicWallClock(bool start = true)
    {
        if (start)
            _sw.Start();
    }

    public TimeSpan ElapsedSinceStart => _accumulated + _sw.Elapsed;

    public bool IsAdvancing => _sw.IsRunning;

    /// <summary>Resume advancing. No-op if already running.</summary>
    public void Resume() => _sw.Start();

    /// <summary>Freeze the clock. <see cref="ElapsedSinceStart"/> holds steady until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        _accumulated += _sw.Elapsed;
        _sw.Reset();
    }
}
