using S.Media.Core.Diagnostics;

namespace S.Media.FFmpeg;

/// <summary>FFmpeg module hook for <see cref="MediaFrameworkRuntime"/>.</summary>
public static class MediaFrameworkRuntimeFfmpegExtensions
{
    private static int _initialized;

    /// <summary>
    /// Initializes FFmpeg bindings and registers file/stream source factories on
    /// <see cref="MediaFrameworkPlugins"/>.
    /// </summary>
    public static MediaFrameworkRuntimeBuilder UseFFmpeg(
        this MediaFrameworkRuntimeBuilder builder,
        string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        FFmpegRuntime.EnsureInitialized(rootPath);
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            MediaFrameworkPluginRegistration.EnsureRegistered();
        return builder;
    }
}
