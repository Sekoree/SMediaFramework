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

    private static unsafe (bool, string?, string?) ProbeCore()
    {
        try
        {
            int major, minor, patch;
            Native.projectm_get_version_components(&major, &minor, &patch);
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
