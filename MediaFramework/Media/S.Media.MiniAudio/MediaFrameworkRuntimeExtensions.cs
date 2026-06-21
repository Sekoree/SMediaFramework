using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.MiniAudio;

public static class MediaFrameworkRuntimeMiniAudioExtensions
{
    /// <summary>
    /// Registers the miniaudio backend. This does not open or initialize a native device; the native
    /// libminiaudio loads lazily when device enumeration or CreateOutput/CreateInput is first called.
    /// </summary>
    public static MediaFrameworkRuntimeBuilder UseMiniAudio(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AudioBackends.Register(new MiniAudioBackend());
        return builder;
    }
}
