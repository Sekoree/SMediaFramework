namespace S.Media.Audio.PortAudio;

/// <summary>
/// Registers the PortAudio host backend into the media registry — the AOT-pure replacement for the old
/// <c>.UsePortAudio()</c> hook + static <c>AudioBackends</c> registration (P2). Once registered,
/// <c>IMediaRegistry.AudioBackends</c> exposes PortAudio device discovery and output/input creation.
/// </summary>
public sealed class PortAudioModule : IMediaModule
{
    public string Name => "PortAudio";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Acquire one PortAudio runtime reference. Release is ref-counted; host/session disposal wiring
        // (the equivalent of the old MediaFrameworkRuntime.Shutdown) lands with the session in Phase 4.
        PortAudioRuntime.Acquire();
        builder.AddAudioBackend(new PortAudioBackend());
    }
}
