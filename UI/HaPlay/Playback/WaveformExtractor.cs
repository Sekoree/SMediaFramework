using S.Media.Decode.FFmpeg.Audio;

namespace HaPlay.Playback;

/// <summary>
/// Extracts a low-resolution peak waveform from an audio file on a background thread.
/// Returns an array of normalized peak values (0..1) for rendering on the scrubber.
/// <paramref name="onPartial"/> (optional) receives throttled snapshots of the buckets analysed so far -
/// normalized against the running peak, zeros beyond the analysed prefix - so the host can render the
/// waveform filling in left-to-right instead of popping in when the whole file is done.
/// </summary>
internal static class WaveformExtractor
{
    private const int TargetBuckets = 512;
    private const int ReadChunkSamples = 4096;
    private static readonly TimeSpan PartialInterval = TimeSpan.FromMilliseconds(150);

    public static async Task<float[]?> ExtractAsync(string path, CancellationToken ct, Action<float[]>? onPartial = null)
    {
        return await Task.Run(() => Extract(path, ct, onPartial), ct).ConfigureAwait(false);
    }

    private static float[]? Extract(string path, CancellationToken ct, Action<float[]>? onPartial)
    {
        AudioFileDecoder? decoder = null;
        try
        {
            decoder = AudioFileDecoder.Open(path);
        }
        catch
        {
            return null;
        }

        try
        {
            if (decoder.Duration <= TimeSpan.Zero)
                return null;

            var channels = decoder.Format.Channels;
            if (channels <= 0) channels = 1;
            var sampleRate = decoder.Format.SampleRate;
            var totalSamples = (long)(decoder.Duration.TotalSeconds * sampleRate);
            if (totalSamples <= 0) return null;

            var bucketCount = Math.Min(TargetBuckets, (int)Math.Max(1, totalSamples));
            var samplesPerBucket = (double)totalSamples / bucketCount;

            var peaks = new float[bucketCount];
            var buffer = new float[ReadChunkSamples * channels];
            long sampleIndex = 0;
            var lastPartial = Environment.TickCount64;

            while (!decoder.IsExhausted)
            {
                ct.ThrowIfCancellationRequested();

                var read = decoder.ReadInto(buffer);
                if (read <= 0) break;

                var frames = read / channels;
                for (var f = 0; f < frames; f++)
                {
                    var bucket = (int)(sampleIndex / samplesPerBucket);
                    if (bucket >= bucketCount) bucket = bucketCount - 1;

                    for (var c = 0; c < channels; c++)
                    {
                        var abs = Math.Abs(buffer[f * channels + c]);
                        if (abs > peaks[bucket]) peaks[bucket] = abs;
                    }
                    sampleIndex++;
                }

                if (onPartial is not null && Environment.TickCount64 - lastPartial >= PartialInterval.TotalMilliseconds)
                {
                    lastPartial = Environment.TickCount64;
                    onPartial(NormalizedCopy(peaks));
                }
            }

            var globalPeak = 0f;
            for (var i = 0; i < peaks.Length; i++)
                if (peaks[i] > globalPeak) globalPeak = peaks[i];

            if (globalPeak > 0)
            {
                for (var i = 0; i < peaks.Length; i++)
                    peaks[i] /= globalPeak;
            }

            return peaks;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>Snapshot for progressive display: the buckets filled so far, normalized against the
    /// RUNNING peak (so the shape reads correctly mid-analysis; the final pass re-normalizes globally).</summary>
    private static float[] NormalizedCopy(float[] peaks)
    {
        var copy = new float[peaks.Length];
        var runningPeak = 0f;
        for (var i = 0; i < peaks.Length; i++)
            if (peaks[i] > runningPeak) runningPeak = peaks[i];
        if (runningPeak <= 0)
            return copy;
        for (var i = 0; i < peaks.Length; i++)
            copy[i] = peaks[i] / runningPeak;
        return copy;
    }
}
