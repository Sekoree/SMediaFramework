namespace S.Media.FFmpeg.Common;

/// <summary>
/// One-time FFmpeg native-binding initialization. Safe to call repeatedly. The first successful call
/// wins; a later disagreeing <paramref name="rootPath"/> is ignored (logged once).
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen 8.x routes every <c>av_*</c> call through a function-pointer table that must be
/// populated up-front (<see cref="DynamicallyLoadedBindings.Initialize"/>); without it every API call
/// throws <see cref="NotSupportedException"/>. On Linux/macOS <see cref="ffmpeg.RootPath"/> is empty so
/// the platform loader finds system libraries. On Windows a complete FFmpeg installation in System32
/// or PATH is selected before the application-local native bundle.
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

            ffmpeg.RootPath = rootPath ?? ResolveDefaultRootPath();

            DynamicallyLoadedBindings.Initialize();
            _initialized = true;
        }
    }

    internal static string ResolveDefaultRootPath()
    {
        // dlopen/dyld resolve AutoGen's versioned bare library names through the configured
        // system loader paths. The shipped FFmpeg.GPL fallback is Windows-only.
        if (!OperatingSystem.IsWindows())
            return "";

        var requiredFiles = ffmpeg.LibraryVersionMap
            .Select(entry => $"{entry.Key}-{entry.Value}.dll")
            .ToArray();

        return FindCompleteNativeDirectory(
                   WindowsSystemDirectories(), requiredFiles, AppContext.BaseDirectory)
               ?? AppContext.BaseDirectory;
    }

    /// <summary>Returns the first directory containing one coherent native-library set.</summary>
    /// <remarks>
    /// Requiring the entire FFmpeg ABI set prevents mixing a system avcodec with bundled avutil (or
    /// vice versa), which is unsafe even when the individual major versions appear compatible.
    /// </remarks>
    internal static string? FindCompleteNativeDirectory(
        IEnumerable<string> directories,
        IReadOnlyCollection<string> requiredFiles,
        string? excludedDirectory = null)
    {
        if (requiredFiles.Count == 0)
            return null;

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var excluded = NormalizeDirectory(excludedDirectory);
        var seen = new HashSet<string>(comparer);

        foreach (var directory in directories)
        {
            var normalized = NormalizeDirectory(directory);
            if (normalized is null || comparer.Equals(normalized, excluded) || !seen.Add(normalized))
                continue;
            if (requiredFiles.All(file => File.Exists(Path.Combine(normalized, file))))
                return normalized;
        }

        return null;
    }

    private static IEnumerable<string> WindowsSystemDirectories()
    {
        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            yield return Environment.SystemDirectory;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            yield break;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory;
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            return Path.GetFullPath(directory.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
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
