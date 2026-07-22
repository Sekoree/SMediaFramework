using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S.Media.NativeInterop;

namespace PALib.Runtime;

public static class PortAudioLibraryResolver
{
    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Installs an assembly-local DllImport resolver that probes the known PortAudio library names
    /// and optionally configures the resolver's internal logger.
    /// Safe to call multiple times - the resolver is only registered once, but the logger is
    /// updated on every call that supplies a non-null <paramref name="loggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Because <see cref="PALibModuleInit"/> runs a parameter-less <c>Install()</c> via
    /// <c>[ModuleInitializer]</c> before any user code, a subsequent call such as
    /// <c>Install(myLoggerFactory)</c> at app startup is the correct way to attach a real logger.
    /// </remarks>
    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            // Always upgrade the logger when a factory is supplied - even if already installed.
            if (loggerFactory != null)
                _logger = loggerFactory.CreateLogger("PALib.Runtime");

            if (_installed)
                return;

            NativeLibrary.SetDllImportResolver(typeof(PortAudioLibraryResolver).Assembly, ResolveLibrary);
            _installed = true;
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, PortAudioLibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        var candidates = GetCandidates();
        if (SystemFirstNativeLibraryResolver.TryLoad(
                assembly,
                searchPath,
                candidates,
                installedPaths: null,
                SystemFirstNativeLibraryResolver.AppLocalPaths(candidates),
                out var handle,
                out var loadedCandidate))
        {
            _logger.LogDebug("Loaded PortAudio native library candidate '{Candidate}'.", loadedCandidate);
            return handle;
        }

        _logger.LogDebug("Unable to load PortAudio using PALib fallback candidates.");
        return nint.Zero;
    }

    internal static string[] GetCandidates()
        => OperatingSystem.IsWindows()
            ? PortAudioLibraryNames.WindowsCandidates
            : OperatingSystem.IsMacOS()
                ? PortAudioLibraryNames.MacCandidates
                : PortAudioLibraryNames.LinuxCandidates;
}
