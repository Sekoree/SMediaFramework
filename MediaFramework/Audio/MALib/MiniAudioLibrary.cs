using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.NativeInterop;

namespace MALib;

/// <summary>
/// Resolves the <c>"miniaudio"</c> import name to the compiled vanilla miniaudio shared library
/// (<c>libminiaudio.so</c> / <c>miniaudio.dll</c> / <c>libminiaudio.dylib</c>). The native library is built
/// and deployed externally; MALib only resolves it. Installed once on module load.
/// </summary>
internal static class MiniAudioLibrary
{
    internal const string ImportName = "miniaudio";

    private static int _installed;

    private static readonly string[] Candidates =
        OperatingSystem.IsWindows() ? ["miniaudio", "miniaudio.dll"]
        : OperatingSystem.IsMacOS() ? ["libminiaudio.dylib", "miniaudio"]
        : ["libminiaudio.so", "miniaudio"];

#pragma warning disable CA2255 // ModuleInitializer is intentional: install the resolver before any ma_* call.
    [ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(MiniAudioLibrary).Assembly, Resolve);
    }
#pragma warning restore CA2255

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ImportName, StringComparison.Ordinal))
            return nint.Zero;

        if (SystemFirstNativeLibraryResolver.TryLoad(
                assembly,
                searchPath,
                Candidates,
                installedPaths: null,
                SystemFirstNativeLibraryResolver.AppLocalPaths(Candidates),
                out var handle,
                out _))
            return handle;

        return nint.Zero;
    }
}
