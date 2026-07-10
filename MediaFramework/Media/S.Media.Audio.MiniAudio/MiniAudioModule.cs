namespace S.Media.Audio.MiniAudio;

/// <summary>
/// Registers the miniaudio host backend into the media registry - the AOT-pure replacement for the old
/// <c>.UseMiniAudio()</c> hook + static <c>AudioBackends</c> registration (P2). Once registered,
/// <c>IMediaRegistry.AudioBackends</c> exposes miniaudio device discovery and output/input creation.
/// Registration does not open a native device: the host-supplied <c>libminiaudio</c> loads lazily when
/// device enumeration or <c>CreateOutput</c>/<c>CreateInput</c> is first called.
/// </summary>
public sealed class MiniAudioModule : IMediaModule
{
    public string Name => "miniaudio";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddAudioBackend(new MiniAudioBackend());
    }
}
