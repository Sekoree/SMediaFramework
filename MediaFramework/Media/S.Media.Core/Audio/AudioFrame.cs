namespace S.Media.Core.Audio;

/// <summary>
/// One block of decoded/received audio: packed float32 samples plus the
/// presentation time the producer attached to them.
/// </summary>
/// <remarks>
/// <see cref="Samples"/> is packed (interleaved) — length is
/// <see cref="SamplesPerChannel"/> × <see cref="AudioFormat.Channels"/>.
/// The backing buffer's lifetime is the producer's responsibility; the
/// default expectation is "owned by the frame" (safe to keep), but
/// zero-copy sources may hand out borrowed memory and document a shorter
/// validity window.
/// </remarks>
public readonly record struct AudioFrame(
    TimeSpan PresentationTime,
    AudioFormat Format,
    int SamplesPerChannel,
    ReadOnlyMemory<float> Samples);
