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
        // Acquire one PortAudio runtime reference for the registry's lifetime and register its release, so the
        // ref is dropped when the registry is disposed (NXT-05) instead of leaking. Without this a C-ABI host
        // that creates/destroys sessions in a loop ratchets the PortAudio refcount up forever (Pa_Terminate
        // never runs). Release is ref-counted, so device-driven Acquire/Release still balance independently.
        PortAudioRuntime.Acquire();
        builder.AddLifetime(new PortAudioRuntimeLease());
        builder.AddAudioBackend(new PortAudioBackend());
    }

    /// <summary>Drops the registry's PortAudio runtime hold exactly once on dispose (NXT-05).</summary>
    private sealed class PortAudioRuntimeLease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                PortAudioRuntime.Release();
        }
    }
}
