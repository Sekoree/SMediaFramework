using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.NativeInterop;

namespace S.Media.Source.MMD;

/// <summary>System-first resolver for the optional Bullet C shim. A machine-installed ABI-compatible
/// shim wins; the copy staged beside HaPlay by the MMD project is the portable fallback.</summary>
internal static class MMDBulletLibraryResolver
{
    internal const string ImportName = "mmd_bullet";

    private static readonly string[] Candidates =
        OperatingSystem.IsWindows() ? ["mmd_bullet", "libmmd_bullet"]
        : OperatingSystem.IsMacOS() ? ["libmmd_bullet.dylib", "mmd_bullet"]
        : ["libmmd_bullet.so", "mmd_bullet"];

    private static int _installed;

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(MMDBulletLibraryResolver).Assembly, Resolve);
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
