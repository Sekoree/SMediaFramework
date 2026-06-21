using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.PortAudio;

/// <summary>PortAudio module hook for <see cref="MediaFrameworkRuntime"/>.</summary>
public static class MediaFrameworkRuntimePortAudioExtensions
{
    /// <summary>
    /// Acquires one PortAudio runtime reference (released on <see cref="MediaFrameworkRuntime.Shutdown"/>) and
    /// registers <see cref="PortAudioBackend"/> with <see cref="AudioBackends"/> so callers can reach PortAudio
    /// device discovery / output creation through the backend-agnostic interface.
    /// </summary>
    public static MediaFrameworkRuntimeBuilder UsePortAudio(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        PortAudioRuntime.Acquire();
        MediaFrameworkRuntime.RegisterShutdown(PortAudioRuntime.Release);
        AudioBackends.Register(new PortAudioBackend());
        return builder;
    }
}
