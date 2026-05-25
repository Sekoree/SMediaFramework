using S.Media.Core.Diagnostics;

namespace S.Media.PortAudio;

/// <summary>PortAudio module hook for <see cref="MediaFrameworkRuntime"/>.</summary>
public static class MediaFrameworkRuntimePortAudioExtensions
{
    /// <summary>Acquires one PortAudio runtime reference (released on <see cref="MediaFrameworkRuntime.Shutdown"/>).</summary>
    public static MediaFrameworkRuntimeBuilder UsePortAudio(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        PortAudioRuntime.Acquire();
        MediaFrameworkRuntime.RegisterShutdown(PortAudioRuntime.Release);
        return builder;
    }
}
