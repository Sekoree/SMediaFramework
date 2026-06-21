using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MALib;

/// <summary>
/// Resolves the <c>"miniaudio"</c> import name to the compiled vanilla miniaudio shared library
/// (<c>libminiaudio.so</c> / <c>miniaudio.dll</c> / <c>libminiaudio.dylib</c>), which MALib's build emits and
/// copies next to the consuming app. Installed once on module load.
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

        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        }

        return nint.Zero;
    }
}
