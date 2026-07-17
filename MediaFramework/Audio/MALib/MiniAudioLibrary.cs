using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.NativeInterop;

namespace MALib;

/// <summary>
/// Resolves the <c>"miniaudio"</c> import name to the compiled vanilla miniaudio shared library
/// (<c>libminiaudio.so</c> / <c>miniaudio.dll</c> / <c>libminiaudio.dylib</c>). Installed once on module load.
/// </summary>
/// <remarks>
/// <para>Unlike the other first-party wrappers, this resolver deliberately does NOT use the shared
/// system-first policy. <see cref="MiniAudioNative"/> hand-mirrors miniaudio 0.11.25's structure
/// layouts, and upstream explicitly does not guarantee ABI compatibility between versions - a
/// mismatched system build would load fine and then corrupt process memory on the audio callback
/// thread. The exact app-local build ships with the application, so it is probed FIRST, and every
/// candidate (app-local or system) must prove it is exactly the pinned version via
/// <c>ma_version()</c> before it is accepted (P1-2).</para>
/// </remarks>
internal static unsafe class MiniAudioLibrary
{
    internal const string ImportName = "miniaudio";

    // The exact version MiniAudioNative's hand-mirrored layouts were derived from. Bump BOTH together:
    // re-verify every structure size/offset in MiniAudioNative against the new headers first.
    internal const uint PinnedMajor = 0;
    internal const uint PinnedMinor = 11;
    internal const uint PinnedRevision = 25;

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

        // App-local exact build first; loader-visible system names only as a gated fallback.
        foreach (var candidate in SystemFirstNativeLibraryResolver.AppLocalPaths(Candidates))
        {
            if (Path.IsPathFullyQualified(candidate)
                && NativeLibrary.TryLoad(candidate, out var handle)
                && AcceptExactPinnedVersion(candidate, ref handle))
                return handle;
        }

        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)
                && AcceptExactPinnedVersion(candidate, ref handle))
                return handle;
        }

        // .NET's assembly/RID probing as the final compatibility fallback (may resolve a bundled
        // runtimes/<rid>/native asset) - still version-gated.
        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle)
                && AcceptExactPinnedVersion(candidate, ref handle))
                return handle;
        }

        return nint.Zero;
    }

    /// <summary>Accepts a loaded candidate only when its <c>ma_version()</c> reports exactly the
    /// pinned 0.11.25. A build that cannot prove its version is rejected the same way as a wrong
    /// one - "loads fine" is not evidence of layout compatibility.</summary>
    private static bool AcceptExactPinnedVersion(string candidate, ref nint handle)
    {
        if (TryReadVersion(handle, out var major, out var minor, out var revision)
            && major == PinnedMajor && minor == PinnedMinor && revision == PinnedRevision)
            return true;

        MALibDiagnostics.LogRejectedCandidate(candidate, major, minor, revision);
        NativeLibrary.Free(handle);
        handle = nint.Zero;
        return false;
    }

    private static bool TryReadVersion(nint handle, out uint major, out uint minor, out uint revision)
    {
        major = minor = revision = 0;
        if (!NativeLibrary.TryGetExport(handle, "ma_version", out var export))
            return false;

        uint maj, min, rev;
        ((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void>)export)(&maj, &min, &rev);
        major = maj;
        minor = min;
        revision = rev;
        return true;
    }
}
