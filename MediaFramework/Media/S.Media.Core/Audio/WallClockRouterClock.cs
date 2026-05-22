using System.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// Free-running deadline-based pacing: produce a chunk every
/// <c>chunkSamples / sampleRate</c> of wall time. Default <see cref="AudioRouter"/>
/// clock — appropriate when no output owns an authoritative consumption clock.
/// </summary>
/// <remarks>
/// Drift vs. real consumers (PortAudio audio thread, NDI sender's clocking) is
/// the cost — over long runs ring buffers fill or empty depending on whose
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

    public bool WaitForNextChunk(CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        var sleep = _nextDeadline - _stopwatch.Elapsed;
        _nextDeadline += _chunkDuration;
        if (sleep <= TimeSpan.Zero) return true; // behind schedule — produce immediately
        return !token.WaitHandle.WaitOne(sleep);
    }
}
