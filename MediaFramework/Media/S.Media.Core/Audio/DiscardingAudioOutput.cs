namespace S.Media.Core.Audio;

/// <summary>
/// <see cref="IAudioOutput"/> that accepts any format and drops every sample submitted to it.
/// Useful as a placeholder while a real output is being attached, in headless smoke tests, and
/// in the quickstart sample.
/// </summary>
public sealed class DiscardingAudioOutput(AudioFormat format) : IAudioOutput
{
    public AudioFormat Format { get; } = format;

    public void Submit(ReadOnlySpan<float> packedSamples) { }
}

// Phase 1 salvage note: the old `ForRouter(AudioRouter, channels)` convenience lived here when Core and
// the router shared one assembly. It coupled Core → Routing (it read router.SampleRate), so it was
// dropped to keep Core router-free; re-add it as an AudioRouter extension in S.Media.Routing if wanted.
