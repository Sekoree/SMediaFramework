namespace S.Media.PortAudio.Internal;

/// <summary>
/// Pulls PCM from an <see cref="S.Media.Core.Audio.IAudioSource"/> into a delivery delegate until a
/// predicate stops or the source runs dry — used for hardware ring prebuffer before starting an
/// <see cref="S.Media.Core.Audio.AudioRouter"/>.
/// </summary>
internal static class AudioPrefill
{
    public static void PumpWhile(
        S.Media.Core.Audio.IAudioSource source,
        Action<ReadOnlySpan<float>> deliver,
        Func<bool> shouldContinue,
        TimeSpan timeout,
        int? maxScratchFloats = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(shouldContinue);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var ch = source.Format.Channels;
        if (ch <= 0)
            throw new ArgumentException("source channel count must be positive", nameof(source));

        var scratchCap = maxScratchFloats ?? Math.Min(65536, Math.Max(8192 * ch, 4096));
        var buf = new float[scratchCap];
        var deadline = DateTime.UtcNow + timeout;
        while (shouldContinue() && DateTime.UtcNow < deadline)
        {
            var read = source.ReadInto(buf);
            if (read == 0) break;
            deliver(buf.AsSpan(0, read));
        }
    }
}
