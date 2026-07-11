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

    /// <summary>Env-override paths first (exact file, then dir + per-OS names), then the system names.</summary>
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
                {
                    var fileName = OperatingSystem.IsWindows() ? name + ".dll" : name;
                    yield return Path.Combine(overridePath, fileName);
                }
            }
        }

        foreach (var name in names)
            yield return name;
    }
}
