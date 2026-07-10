using System.Diagnostics;

namespace S.Media.Routing;

/// <summary>
/// Free-running deadline-based pacing: produce a chunk every
/// <c>chunkSamples / sampleRate</c> of wall time. Default <see cref="AudioRouter"/>
/// clock - appropriate when no output owns an authoritative consumption clock.
/// </summary>
/// <remarks>
/// Drift vs. real consumers (PortAudio audio thread, NDI sender's clocking) is
/// the cost - over long runs ring buffers fill or empty depending on whose
/// quartz wins. For sample-accurate pacing, slave the router to a
/// <see cref="IClockedOutput"/> via <see cref="OutputSlavedRouterClock"/>.
/// </remarks>
internal sealed class WallClockRouterClock : IRouterClock
{
    private readonly TimeSpan _chunkDuration;
    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _nextDeadline;

    public TimeSpan ChunkDuration => _chunkDuration;

    public WallClockRouterClock(int sampleRate, int chunkSamples)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        _chunkDuration = TimeSpan.FromSeconds((double)chunkSamples / sampleRate);
    }

    public void Reset()
    {
        _stopwatch.Restart();
        _nextDeadline = _chunkDuration;
    }

    /// <summary>
    /// After falling more than this many chunks behind (GC pause, scheduling, slow source), re-anchor
    /// the deadline to "now" instead of bursting the whole backlog as fast as the loop runs - matches
    /// <see cref="S.Media.Core.Clock.MediaClock"/>'s bounded-burst behaviour.
    /// </summary>
    private const int MaxCatchupChunks = 64;

    public bool WaitForNextChunk(CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        var elapsed = _stopwatch.Elapsed;
        // Cap catch-up: a long stall must not make the router produce chunks back-to-back at full
        // speed to "catch up" - that just floods output pumps and CPU. Drop the excess backlog.
        if (elapsed - _nextDeadline > _chunkDuration * MaxCatchupChunks)
            _nextDeadline = elapsed;
        var sleep = _nextDeadline - elapsed;
        _nextDeadline += _chunkDuration;
        if (sleep <= TimeSpan.Zero) return true; // behind schedule - produce immediately
        return !token.WaitHandle.WaitOne(sleep);
    }
}
