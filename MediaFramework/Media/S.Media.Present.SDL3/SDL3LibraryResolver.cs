using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.NativeInterop;

namespace S.Media.Present.SDL3;

/// <summary>
/// Applies the framework's system-first policy to the native libraries imported by SDL3-CS.
/// The final assembly-aware probe preserves its NuGet RID native-asset fallback.
/// </summary>
internal static class SDL3LibraryResolver
{
    private static int _installed;

    private static readonly HashSet<string> KnownImports =
    [
        "SDL3",
        "SDL3_image",
        "SDL3_mixer",
        "SDL3_shadercross",
        "SDL3_ttf",
    ];

#pragma warning disable CA2255 // Intentional: resolver must be installed before the first SDL3-CS P/Invoke.
    [ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, ResolveLibrary);
    }
#pragma warning restore CA2255

    private static nint ResolveLibrary(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!KnownImports.Contains(libraryName))
            return nint.Zero;

        var candidates = SystemNames(libraryName);
        return SystemFirstNativeLibraryResolver.TryLoad(
            assembly,
            searchPath,
            candidates,
            installedPaths: null,
            SystemFirstNativeLibraryResolver.AppLocalPaths(candidates),
            out var handle,
            out _)
            ? handle
            : nint.Zero;
    }

    internal static string[] SystemNames(string importName)
    {
        if (OperatingSystem.IsWindows())
            return [importName];
        if (OperatingSystem.IsMacOS())
            return [$"lib{importName}.0.dylib", $"lib{importName}.dylib"];
        return [$"lib{importName}.so.0", $"lib{importName}.so"];
    }
}
