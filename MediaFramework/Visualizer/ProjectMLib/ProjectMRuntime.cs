namespace ProjectMLib;

/// <summary>
/// Availability probe + version info for the host-supplied libprojectM-4 (mirrors NDIRuntime's
/// graceful-degradation pattern: no native lib ⇒ the visualizer feature greys out with a reason,
/// nothing crashes). IMPORTANT: only <see cref="IsAvailable"/>/<see cref="Version"/> are safe without
/// a GL context - creating an instance requires a current OpenGL context on the calling thread.
/// </summary>
public static class ProjectMRuntime
{
    private static readonly Lazy<(bool Available, string? Version, string? Reason)> Probe = new(ProbeCore);

    /// <summary>True when libprojectM-4 loads and answers a version query.</summary>
    public static bool IsAvailable => Probe.Value.Available;

    /// <summary>"4.1.6"-style version of the loaded library (null when unavailable).</summary>
    public static string? Version => Probe.Value.Version;

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    public static string? UnavailableReason => Probe.Value.Reason;

    /// <summary>The on-disk path the native library resolved to (Linux; null elsewhere/unknown).
    /// Diagnostic - distinguishes the repo's dev build from a system install.</summary>
    public static string? LoadedLibraryPath
    {
        get
        {
            _ = Probe.Value; // ensure the load happened
            return Runtime.ElfNeededReader.TryFindLoadedLibraryPath("libprojectM-4");
        }
    }

    private static unsafe (bool, string?, string?) ProbeCore()
    {
        try
        {
            int major, minor, patch;
            Native.projectm_get_version_components(&major, &minor, &patch);

            // A GL-ES build of projectM (some distros, e.g. Arch, ship one linked against libGLESv2)
            // SEGFAULTS inside the driver during projectm_create when handed our desktop-GL context -
            // native crashes are uncatchable, so veto it HERE, before any create can run. The check is
            // deterministic: the loaded .so's DT_NEEDED entries.
            if (OperatingSystem.IsLinux()
                && Runtime.ElfNeededReader.TryFindLoadedLibraryPath("libprojectM-4") is { } loadedPath
                && Runtime.ElfNeededReader.TryReadNeeded(loadedPath)
                    .Any(n => n.StartsWith("libGLESv2", StringComparison.Ordinal)))
            {
                return (false, null,
                    $"the installed libprojectM-4 ({loadedPath}) is an OpenGL ES build, which crashes on the desktop-GL compositor - "
                    + $"build a desktop-GL projectM with scripts/build-projectm.sh and set {Runtime.ProjectMLibraryResolver.EnvironmentOverride}.");
            }

            return (true, $"{major}.{minor}.{patch}", null);
        }
        catch (DllNotFoundException)
        {
            return (false, null,
                $"libprojectM-4 not found - install the projectM 4.x package or set {Runtime.ProjectMLibraryResolver.EnvironmentOverride} (see scripts/build-projectm.sh).");
        }
        catch (Exception ex)
        {
            return (false, null, $"libprojectM-4 failed to load: {ex.Message}");
        }
    }
}
