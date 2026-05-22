namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioOutput"/> capability describing channel-width limits and whether
/// the output can renegotiate channel count without replacing the output instance.
/// </summary>
public interface IAudioOutputChannelCapabilities
{
    AudioOutputChannelCapabilities ChannelCapabilities { get; }
}

/// <summary>
/// Channel-width capability contract for an <see cref="IAudioOutput"/>.
/// </summary>
public readonly record struct AudioOutputChannelCapabilities(
    int CurrentChannels,
    int MinChannels,
    int MaxChannels,
    bool SupportsRuntimeChannelReconfigure)
{
    public static AudioOutputChannelCapabilities Fixed(int channels) =>
        new(channels, channels, channels, SupportsRuntimeChannelReconfigure: false);
}
