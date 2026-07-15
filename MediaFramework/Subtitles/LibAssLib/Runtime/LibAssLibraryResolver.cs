using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.NativeInterop;

namespace LibAssLib.Runtime;

/// <summary>System-first resolver for libass. Distro/installed copies win; an ass/libass binary
/// deployed beside the application remains an explicit fallback for portable Windows builds.</summary>
internal static class LibAssLibraryResolver
{
    internal const string ImportName = "ass";

    private static readonly string[] Candidates =
        OperatingSystem.IsWindows() ? ["ass", "libass"]
        : OperatingSystem.IsMacOS() ? ["libass.9.dylib", "libass.dylib", "ass"]
        : ["libass.so.9", "libass.so", "ass"];

    private static int _installed;

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(LibAssLibraryResolver).Assembly, Resolve);
    }
#pragma warning restore CA2255

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ImportName, StringComparison.Ordinal))
            return nint.Zero;

        return SystemFirstNativeLibraryResolver.TryLoad(
            assembly,
            searchPath,
            Candidates,
            installedPaths: null,
            SystemFirstNativeLibraryResolver.AppLocalPaths(Candidates),
            out var handle,
            out _)
            ? handle
            : nint.Zero;
    }

    internal static IEnumerable<string> GetCandidates() =>
        SystemFirstNativeLibraryResolver.OrderedCandidates(
            Candidates,
            installedPaths: null,
            SystemFirstNativeLibraryResolver.AppLocalPaths(Candidates));
}
