using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.MiniAudio.Runtime;

public static class MiniAudioLibraryResolver
{
    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            if (loggerFactory is not null)
                _logger = loggerFactory.CreateLogger("S.Media.MiniAudio.Runtime");

            if (_installed)
                return;

            NativeLibrary.SetDllImportResolver(typeof(MiniAudioLibraryResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, MiniAudioLibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        foreach (var candidate in GetCandidates())
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                var fullPath = Path.Combine(baseDir, candidate);
                if (NativeLibrary.TryLoad(fullPath, out var fullPathHandle))
                {
                    _logger.LogDebug("Loaded miniaudio native shim from '{Path}'.", fullPath);
                    return fullPathHandle;
                }
            }

            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                _logger.LogDebug("Loaded miniaudio native shim candidate '{Candidate}'.", candidate);
                return handle;
            }
        }

        _logger.LogDebug("Unable to load miniaudio native shim using fallback candidates.");
        return nint.Zero;
    }

    private static string[] GetCandidates()
    {
        if (OperatingSystem.IsWindows()) return MiniAudioLibraryNames.WindowsCandidates;
        if (OperatingSystem.IsMacOS()) return MiniAudioLibraryNames.MacCandidates;
        return MiniAudioLibraryNames.LinuxCandidates;
    }
}
