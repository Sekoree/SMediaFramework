namespace S.Media.Core.Audio;

/// <summary>
/// Pull-based audio producer. Sources include file decoders (FFmpeg), network
/// receivers (NDI), and capture devices (PortAudio input). The router pulls
/// at its mixer chunk cadence; sources that don't have a full chunk yet
/// return a partial fill.
/// </summary>
/// <remarks>
/// Implementations are not required to be thread-safe — the router serialises
/// reads from a single thread.
/// </remarks>
public interface IAudioSource
{
    AudioFormat Format { get; }

    /// <summary>True when the source has no more samples to emit (file EOF, network disconnect, …).</summary>
    bool IsExhausted { get; }

    /// <summary>
    /// Fill <paramref name="destination"/> with packed (interleaved) float
    /// samples — channel-count must match <see cref="Format"/>'s and
    /// <c>destination.Length</c> must be a multiple of it. Returns the number
    /// of floats actually written; a value less than <c>destination.Length</c>
    /// means the source couldn't supply a full chunk (live source not yet
    /// warmed up, or near EOF). The unfilled tail of <paramref name="destination"/>
    /// is left untouched — the caller decides whether to treat it as silence.
    /// </summary>
    int ReadInto(Span<float> destination);
}
