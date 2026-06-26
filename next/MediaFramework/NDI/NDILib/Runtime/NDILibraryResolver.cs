using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NDILib.Runtime;

/// <summary>
/// Cross-platform DllImport resolver for the NDI native library.
/// Automatically registered by <see cref="NDILibModuleInit"/> before any P/Invoke call fires.
/// Consumers may call <see cref="Install"/> again at startup to attach a logger.
/// </summary>
public static class NDILibraryResolver
{
    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Installs the assembly-local DllImport resolver that probes the known NDI library names.
    /// Optionally configures the resolver's internal logger.
    /// Safe to call multiple times — the resolver is only registered once, but the logger is
    /// updated on every call that supplies a non-null <paramref name="loggerFactory"/>.
    /// </summary>
    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            if (loggerFactory != null)
                _logger = loggerFactory.CreateLogger("NDILib.Runtime");

            if (_installed)
                return;

            NativeLibrary.SetDllImportResolver(typeof(NDILibraryResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NDILibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        // On Windows the NDI installer sets NDI_RUNTIME_DIR_V6 to the install directory.
        // Probe that path first so the installed SDK is preferred over PATH.
        if (OperatingSystem.IsWindows())
        {
            var runtimeDir = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6");
            if (!string.IsNullOrEmpty(runtimeDir))
            {
                foreach (var candidate in NDILibraryNames.WindowsCandidates)
                {
                    var fullPath = Path.Combine(runtimeDir, candidate + ".dll");
                    if (NativeLibrary.TryLoad(fullPath, out var hdl))
                    {
                        _logger.LogDebug("Loaded NDI native library from NDI_RUNTIME_DIR_V6: '{Path}'.", fullPath);
                        return hdl;
                    }
                }
            }
        }

        foreach (var candidate in GetCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                _logger.LogDebug("Loaded NDI native library candidate '{Candidate}'.", candidate);
                return handle;
            }
        }

        _logger.LogDebug("Unable to load NDI native library using NDILib fallback candidates.");
        return nint.Zero;
    }

    private static string[] GetCandidates()
        => OperatingSystem.IsWindows()
            ? NDILibraryNames.WindowsCandidates
            : NDILibraryNames.LinuxCandidates;
}

