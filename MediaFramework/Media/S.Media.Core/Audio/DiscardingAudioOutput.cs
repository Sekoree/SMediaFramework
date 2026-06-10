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

    /// <summary>
    /// Discard output pre-matched to <paramref name="router"/>'s sample rate, so
    /// <see cref="AudioRouter.AddOutput"/> can't fail on a rate mismatch (the common
    /// quickstart/test foot-gun of restating the rate by hand).
    /// </summary>
    public static DiscardingAudioOutput ForRouter(AudioRouter router, int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        return new DiscardingAudioOutput(new AudioFormat(router.SampleRate, channels));
    }
}
