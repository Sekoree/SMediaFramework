namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioSink"/> capability describing channel-width limits and whether
/// the sink can renegotiate channel count without replacing the sink instance.
/// </summary>
public interface IAudioSinkChannelCapabilities
{
    AudioSinkChannelCapabilities ChannelCapabilities { get; }
}

/// <summary>
/// Channel-width capability contract for an <see cref="IAudioSink"/>.
/// </summary>
public readonly record struct AudioSinkChannelCapabilities(
    int CurrentChannels,
    int MinChannels,
    int MaxChannels,
    bool SupportsRuntimeChannelReconfigure)
{
    public static AudioSinkChannelCapabilities Fixed(int channels) =>
        new(channels, channels, channels, SupportsRuntimeChannelReconfigure: false);
}
