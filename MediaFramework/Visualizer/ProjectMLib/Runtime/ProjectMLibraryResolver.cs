using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProjectMLib.Runtime;

/// <summary>
/// Cross-platform DllImport resolver for libprojectM-4 (same pattern as NDILibraryResolver).
/// Probe order: the <c>MFP_PROJECTM_LIB</c> environment variable (a full library path OR a directory
/// containing the library - what scripts/build-projectm.sh prints), then the per-OS system candidates.
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

        foreach (var candidate in GetCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                _logger.LogDebug("Loaded projectM native library candidate '{Candidate}'.", candidate);
                return handle;
            }
        }

        _logger.LogDebug("Unable to load libprojectM-4 using ProjectMLib fallback candidates.");
        return nint.Zero;
    }

    /// <summary>Probe order: env override (exact file, then dir + per-OS names), the repo's dev build
    /// (<c>External/projectm/&lt;rid&gt;</c> from scripts/build-projectm.sh, discovered by walking up
    /// from the app directory - zero-setup for dev runs), then the system names.</summary>
    internal static IEnumerable<string> GetCandidates()
    {
        var names = OperatingSystem.IsWindows() ? ProjectMLibraryNames.WindowsCandidates
            : OperatingSystem.IsMacOS() ? ProjectMLibraryNames.MacCandidates
            : ProjectMLibraryNames.LinuxCandidates;

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

        if (TryFindDevBuildRoot() is { } devRoot)
        {
            foreach (var libDir in DevLibDirectories(devRoot))
            foreach (var name in names)
                yield return Path.Combine(libDir, LibraryFileName(name));
        }

        foreach (var name in names)
            yield return name;
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
