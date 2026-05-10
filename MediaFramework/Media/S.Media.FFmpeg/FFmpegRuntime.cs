namespace S.Media.FFmpeg;

/// <summary>
/// One-time FFmpeg native binding initialization. Safe to call multiple times.
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen 8.x routes every <c>av_*</c> call through a function-pointer
/// table that must be populated up-front; without this you get
/// <see cref="NotSupportedException"/> from every API call. On Linux/macOS we
/// default <see cref="ffmpeg.RootPath"/> to empty so the system loader finds
/// the libs in standard paths (e.g. <c>/usr/lib/libavformat.so.62</c>); on
/// Windows the AutoGen default (the executable directory) is left alone so
/// the FFmpeg.AutoGen.Redist.windows.x64 companion package keeps working.
/// </remarks>
public static class FFmpegRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Initializes the dynamic bindings. Optionally override the lookup path
    /// for native libraries (e.g. a custom FFmpeg build directory).
    /// </summary>
    public static void EnsureInitialized(string? rootPath = null)
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            if (rootPath is not null)
                ffmpeg.RootPath = rootPath;
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                ffmpeg.RootPath = "";

            DynamicallyLoadedBindings.Initialize();
            _initialized = true;
        }
    }
}
