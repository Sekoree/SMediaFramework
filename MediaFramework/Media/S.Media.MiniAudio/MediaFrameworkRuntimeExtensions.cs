using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.MiniAudio.Runtime;

namespace S.Media.MiniAudio;

public static class MediaFrameworkRuntimeMiniAudioExtensions
{
    /// <summary>
    /// Registers the miniaudio backend. This does not open or initialize a native device; the shim loads
    /// lazily when device enumeration or CreateOutput/CreateInput is called.
    /// </summary>
    public static MediaFrameworkRuntimeBuilder UseMiniAudio(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        MiniAudioLibraryResolver.Install();
        AudioBackends.Register(new MiniAudioBackend());
        return builder;
    }
}
