using S.Media.Core.Clock;

namespace S.Media.Core.Audio;

/// <summary>
/// Paces <see cref="AudioRouter"/> production from an <see cref="IPlaybackClock"/> media timeline
/// (for example <c>NDIIngestPlaybackClock</c>) instead of wall clock or PortAudio.
/// </summary>
internal sealed class PlaybackSlavedRouterClock : IRouterClock
{
    private readonly IPlaybackClock _master;
    private readonly TimeSpan _chunkDuration;
    private TimeSpan _nextChunkDeadline;

    public PlaybackSlavedRouterClock(IPlaybackClock master, int sampleRate, int chunkSamples)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        _master = master;
        _chunkDuration = TimeSpan.FromSeconds((double)chunkSamples / sampleRate);
        _nextChunkDeadline = _chunkDuration;
    }

    public void Reset() => _nextChunkDeadline = _chunkDuration;

    public bool WaitForNextChunk(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var elapsed = _master.ElapsedSinceStart;
            if (elapsed >= _nextChunkDeadline)
            {
                _nextChunkDeadline += _chunkDuration;
                return true;
            }

            token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(2));
        }

        return false;
    }
}
