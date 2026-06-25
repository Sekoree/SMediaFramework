using S.Media.Core;

namespace S.Media.Core.Audio;

/// <summary>
/// One block of decoded/received audio: packed float32 samples plus the
/// presentation time the producer attached to them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Samples"/> is packed (interleaved) — length is
/// <see cref="SamplesPerChannel"/> × <see cref="AudioFormat.Channels"/>.
/// The backing buffer's lifetime is the producer's responsibility; the
/// default expectation is "owned by the frame" (safe to keep), but
/// zero-copy sources may hand out borrowed memory and document a shorter
/// validity window.
/// </para>
/// <para>
/// Producers that lease their buffer from a pool (e.g.
/// <c>S.Media.FFmpeg.Audio.AudioFileDecoder.TryReadNextFrame</c> which now uses
/// <see cref="System.Buffers.ArrayPool{T}"/>) pass a non-null <see cref="Release"/> callback that returns the buffer
/// to its pool. Consumers must call <see cref="Dispose"/> when done; calling it multiple times is safe — only
/// the producer's release runs once.
/// </para>
/// </remarks>
public readonly record struct AudioFrame(
    TimeSpan PresentationTime,
    AudioFormat Format,
    int SamplesPerChannel,
    ReadOnlyMemory<float> Samples,
    IDisposable? Release = null)
{
    /// <summary>Builds an audio frame with an <see cref="Action"/> release (wrapped as <see cref="IDisposable"/>).</summary>
    public static AudioFrame WithActionRelease(
        TimeSpan presentationTime,
        AudioFormat format,
        int samplesPerChannel,
        ReadOnlyMemory<float> samples,
        Action? release) =>
        new(presentationTime, format, samplesPerChannel, samples, DisposableRelease.Wrap(release));

    /// <summary>Invokes the producer's release if set; otherwise a no-op.</summary>
    public void Dispose() => Release?.Dispose();
}
