using S.Media.Core.Audio;

namespace S.Media.NDI.Clock;

/// <summary>
/// Paces <see cref="AudioRouter"/> production from <see cref="NDIIngestPlaybackClock"/> media time
/// instead of wall clock or PortAudio — keeps decoded audio pulls aligned with NDI-timestamped video.
/// </summary>
public sealed class IngestSlavedRouterClock : IRouterClock
{
    private readonly NDIIngestPlaybackClock _ingest;
    private readonly TimeSpan _chunkDuration;
    private TimeSpan _nextChunkDeadline;

    public TimeSpan ChunkDuration => _chunkDuration;

    public IngestSlavedRouterClock(NDIIngestPlaybackClock ingest, int sampleRate, int chunkSamples)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        _ingest = ingest;
        _chunkDuration = TimeSpan.FromSeconds((double)chunkSamples / sampleRate);
        _nextChunkDeadline = _chunkDuration;
    }

    public void Reset() => _nextChunkDeadline = _chunkDuration;

    public bool WaitForNextChunk(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var elapsed = _ingest.ElapsedSinceStart;
            if (elapsed >= _nextChunkDeadline)
            {
                _nextChunkDeadline += _chunkDuration;
                return true;
            }

            if (!token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(2)))
                return false;
        }

        return false;
    }
}
