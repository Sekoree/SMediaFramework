using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S.Media.NativeInterop;

namespace ProjectMLib.Runtime;

/// <summary>
/// Cross-platform DllImport resolver for libprojectM-4 (same pattern as NDILibraryResolver).
/// Probe order: system-installed projectM, then the <c>MFP_PROJECTM_LIB</c> environment fallback
/// (a full library path OR a directory containing the library), development builds, and app-local assets.
/// Registered by <see cref="ProjectMLibModuleInit"/> before any P/Invoke fires.
/// </summary>
public static class ProjectMLibraryResolver
{
    /// <summary>Full path to libprojectM-4, or a directory containing it (dev builds from Reference/).</summary>
    public const string EnvironmentOverride = "MFP_PROJECTM_LIB";

    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            if (loggerFactory != null)
                _logger = loggerFactory.CreateLogger("ProjectMLib.Runtime");

            if (_installed)
                return;

            NativeLibrary.SetDllImportResolver(typeof(ProjectMLibraryResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ProjectMLibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        var names = PlatformNames();
        if (SystemFirstNativeLibraryResolver.TryLoad(
                assembly,
                searchPath,
                names,
                EnvironmentFallbackPaths(names),
                BundledFallbackPaths(names),
                out var handle,
                out var loadedCandidate,
                acceptCandidate: IsUsableProjectMBuild))
        {
            _logger.LogDebug("Loaded projectM native library candidate '{Candidate}'.", loadedCandidate);
            return handle;
        }

        _logger.LogDebug("Unable to load libprojectM-4 using ProjectMLib fallback candidates.");
        return nint.Zero;
    }

    /// <summary>High-level probe order used by tests and diagnostics. Bare system names are always
    /// first; explicit override/development/application paths are fallbacks.</summary>
    internal static IEnumerable<string> GetCandidates()
    {
        var names = PlatformNames();
        return SystemFirstNativeLibraryResolver.OrderedCandidates(
            names,
            EnvironmentFallbackPaths(names),
            BundledFallbackPaths(names));
    }

    /// <summary>Rejects a projectM candidate that is an OpenGL ES build (DT_NEEDED <c>libGLESv2</c>). Such
    /// a build loads fine but SEGFAULTS - uncatchably - inside <c>projectm_create</c> when handed the
    /// desktop-GL compositor context. Rejecting it here lets probing continue past an unusable system
    /// default (Arch/CachyOS ship a GLES build) to a usable desktop-GL build supplied via the env override,
    /// the dev build, or an app-local bundle. Non-Linux and undeterminable cases are accepted; the
    /// post-load <see cref="ProjectMRuntime"/> probe remains the backstop. Android deliberately
    /// lands in the accept path (<c>IsLinux()</c> is false there): its bundled projectM IS a GLES
    /// build and the renderer runs on a GLES context, so the desktop veto must not apply.</summary>
    private static bool IsUsableProjectMBuild(string candidate)
    {
        if (!OperatingSystem.IsLinux())
            return true;

        var path = File.Exists(candidate)
            ? candidate
            : ElfNeededReader.TryFindLoadedLibraryPath("libprojectM-4");
        if (path is null)
            return true; // cannot inspect - don't over-veto

        if (ElfNeededReader.TryReadNeeded(path).Any(n => n.StartsWith("libGLESv2", StringComparison.Ordinal)))
        {
            _logger.LogDebug(
                "Skipping OpenGL ES projectM build '{Path}' - it crashes the desktop-GL compositor; continuing probe.",
                path);
            return false;
        }

        return true;
    }

    private static string[] PlatformNames() =>
        OperatingSystem.IsWindows() ? ProjectMLibraryNames.WindowsCandidates
        : OperatingSystem.IsMacOS() ? ProjectMLibraryNames.MacCandidates
        : OperatingSystem.IsAndroid() ? ProjectMLibraryNames.AndroidCandidates
        : ProjectMLibraryNames.LinuxCandidates;

    private static IEnumerable<string> EnvironmentFallbackPaths(IReadOnlyList<string> names)
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentOverride);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
                yield return overridePath;
            else if (Directory.Exists(overridePath))
            {
                foreach (var name in names)
                    yield return Path.Combine(overridePath, LibraryFileName(name));
            }
        }
    }

    private static IEnumerable<string> BundledFallbackPaths(IReadOnlyList<string> names)
    {
        if (TryFindDevBuildRoot() is { } devRoot)
        {
            foreach (var libDir in DevLibDirectories(devRoot))
            foreach (var name in names)
                yield return Path.Combine(libDir, LibraryFileName(name));
        }

        // A deployed artifact commonly ships the projectM native library next to the executable.
        foreach (var path in SystemFirstNativeLibraryResolver.AppLocalPaths(names))
            yield return path;
    }

    private static string LibraryFileName(string name) =>
        OperatingSystem.IsWindows() ? name + ".dll" : name;

    private static IEnumerable<string> DevLibDirectories(string devRoot)
    {
        var lib = Path.Combine(devRoot, "lib");
        if (Directory.Exists(lib))
            yield return lib;
        var lib64 = Path.Combine(devRoot, "lib64");
        if (Directory.Exists(lib64))
            yield return lib64;
        var bin = Path.Combine(devRoot, "bin"); // windows installs DLLs under bin/
        if (Directory.Exists(bin))
            yield return bin;
    }

    /// <summary>The scripts/build-projectm.sh install root for this platform
    /// (<c>External/projectm/&lt;rid&gt;</c>), found by walking up from the app base directory. Also
    /// used by the UI for the default preset directory (<c>&lt;root&gt;/presets</c>). Null when no dev
    /// build exists (deployed installs rely on the env override or the system library).</summary>
    public static string? TryFindDevBuildRoot()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        var rid = OperatingSystem.IsWindows() ? $"win-{arch}"
            : OperatingSystem.IsMacOS() ? $"osx-{arch}"
            : $"linux-{arch}";

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "External", "projectm", rid);
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}
