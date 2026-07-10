using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PMLib.Runtime;

/// <summary>
/// Installs an assembly-local <see cref="NativeLibrary.SetDllImportResolver"/> that probes the
/// known PortMIDI library names for the current platform.
/// <para>
/// Call <see cref="Install"/> with a <see cref="ILoggerFactory"/> at application start-up
/// to attach a real logger. The resolver is also registered automatically via
/// <see cref="PMLibModuleInit"/> before any P/Invoke fires.
/// </para>
/// </summary>
public static class PortMIDILibraryResolver
{
    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Installs an assembly-local DllImport resolver that probes the known PortMIDI library names
    /// and optionally configures the resolver's internal logger.
    /// Safe to call multiple times - the resolver is only registered once, but the logger is
    /// updated on every call that supplies a non-null <paramref name="loggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Because <see cref="PMLibModuleInit"/> runs a parameter-less <c>Install()</c> via
    /// <c>[ModuleInitializer]</c> before any user code, a subsequent call such as
    /// <c>Install(myLoggerFactory)</c> at app startup is the correct way to attach a real logger.
    /// </remarks>
    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            // Always upgrade the logger when a factory is supplied - even if already installed.
            if (loggerFactory != null)
                _logger = loggerFactory.CreateLogger("PMLib.Runtime");

            if (_installed)
                return;

            NativeLibrary.SetDllImportResolver(typeof(PortMIDILibraryResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, PortMIDILibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        foreach (var candidate in GetCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                _logger.LogDebug("Loaded PortMIDI native library candidate '{Candidate}'.", candidate);
                return handle;
            }
        }

        _logger.LogDebug("Unable to load PortMIDI using PMLib fallback candidates.");
        return nint.Zero;
    }

    private static string[] GetCandidates()
        => OperatingSystem.IsWindows()
            ? PortMIDILibraryNames.WindowsCandidates
            : PortMIDILibraryNames.LinuxCandidates;
}
