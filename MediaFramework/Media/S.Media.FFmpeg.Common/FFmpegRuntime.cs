namespace S.Media.FFmpeg.Common;

/// <summary>
/// One-time FFmpeg native-binding initialization. Safe to call repeatedly. The first successful call
/// wins; a later disagreeing <paramref name="rootPath"/> is ignored (logged once).
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen 8.x routes every <c>av_*</c> call through a function-pointer table that must be
/// populated up-front (<see cref="DynamicallyLoadedBindings.Initialize"/>); without it every API call
/// throws <see cref="NotSupportedException"/>. On Linux/macOS <see cref="ffmpeg.RootPath"/> defaults to
/// empty so the system loader finds the libs; on Windows the AutoGen default is left alone.
/// <para>
/// This used to also install the old static <c>MediaFrameworkPlugins</c> capability slots; those now go
/// through the media registry in <c>FFmpegModule.Register</c> (P2). This type is just native init.
/// </para>
/// </remarks>
public static class FFmpegRuntime
{
    private static readonly Lock Gate = new();
    private static volatile bool _initialized;
    private static int _ignoredRootPathLogged;

    /// <summary>Initializes the dynamic bindings, optionally overriding the native lookup path.</summary>
    public static void EnsureInitialized(string? rootPath = null)
    {
        if (_initialized)
        {
            MaybeLogIgnoredRootPath(rootPath);
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                MaybeLogIgnoredRootPath(rootPath);
                return;
            }

            if (rootPath is not null)
                ffmpeg.RootPath = rootPath;
            else if (OperatingSystem.IsLinux())
                ffmpeg.RootPath = "";

            DynamicallyLoadedBindings.Initialize();
            _initialized = true;
        }
    }

    private static void MaybeLogIgnoredRootPath(string? requested)
    {
        if (requested is null)
            return;

        var current = ffmpeg.RootPath ?? "";
        if (string.Equals(current, requested, StringComparison.Ordinal))
            return;

        if (Interlocked.Exchange(ref _ignoredRootPathLogged, 1) != 0)
            return;

        MediaDiagnostics.LogWarning(
            "FFmpegRuntime.EnsureInitialized: bindings already initialized (RootPath '{0}'); ignoring requested rootPath '{1}'. Use a new process to load a different native FFmpeg build.",
            current,
            requested);
    }
}
