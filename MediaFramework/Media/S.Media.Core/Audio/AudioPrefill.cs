namespace S.Media.Core.Audio;

/// <summary>
/// Pulls PCM from an <see cref="IAudioSource"/> into a delivery delegate until a
/// predicate stops or the source runs dry — used for hardware ring prebuffer
/// before starting an <see cref="AudioRouter"/>.
/// </summary>
/// <remarks>
/// <paramref name="shouldContinue"/> must become false once the destination is
/// full (or no more progress is possible); otherwise a bounded output can spin
/// forever while the source still returns data. Prefer the PortAudio-specific
/// <c>PrefillFrom</c> helper on <c>S.Media.PortAudio.PortAudioOutput</c> for hardware rings,
/// which guards against a full ring.
/// </remarks>
public static class AudioPrefill
{
    /// <summary>
    /// Repeatedly <see cref="IAudioSource.ReadInto"/> while <paramref name="shouldContinue"/> is true
    /// and <paramref name="timeout"/> has not elapsed.
    /// </summary>
    public static void PumpWhile(
        IAudioSource source,
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
