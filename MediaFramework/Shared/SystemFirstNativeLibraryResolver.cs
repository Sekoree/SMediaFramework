using System.Reflection;
using System.Runtime.InteropServices;

namespace S.Media.NativeInterop;

/// <summary>
/// Shared native-library resolution policy for first-party P/Invoke wrapper assemblies.
/// System-installed libraries are always attempted before explicit app-local/development paths
/// and before the assembly-aware managed fallback (which may resolve a bundled RID asset).
/// This file is source-linked into each standalone wrapper to avoid coupling the low-level ABIs
/// to another runtime assembly.
/// </summary>
internal static class SystemFirstNativeLibraryResolver
{
    internal static bool TryLoad(
        Assembly assembly,
        DllImportSearchPath? searchPath,
        IReadOnlyList<string> systemNames,
        IEnumerable<string>? installedPaths,
        IEnumerable<string>? bundledPaths,
        out nint handle,
        out string? loadedCandidate)
    {
        foreach (var candidate in SystemCandidates(systemNames))
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                loadedCandidate = candidate;
                return true;
            }
        }

        if (TryLoadExplicitPaths(installedPaths, out handle, out loadedCandidate)
            || TryLoadExplicitPaths(bundledPaths, out handle, out loadedCandidate))
            return true;

        // Keep .NET's normal assembly/RID probing as the final compatibility fallback. It is
        // deliberately last because it may select an app-local native asset.
        foreach (var candidate in systemNames)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out handle))
            {
                loadedCandidate = candidate;
                return true;
            }
        }

        handle = nint.Zero;
        loadedCandidate = null;
        return false;
    }

    /// <summary>Human/test-visible high-level order. Windows expands each system name to System32
    /// and PATH full paths internally; Unix-like platforms pass the bare name to their loader.</summary>
    internal static IEnumerable<string> OrderedCandidates(
        IReadOnlyList<string> systemNames,
        IEnumerable<string>? installedPaths,
        IEnumerable<string>? bundledPaths)
    {
        foreach (var candidate in systemNames)
            yield return candidate;
        if (installedPaths is not null)
            foreach (var candidate in installedPaths)
                yield return candidate;
        if (bundledPaths is not null)
            foreach (var candidate in bundledPaths)
                yield return candidate;
    }

    internal static IEnumerable<string> AppLocalPaths(IReadOnlyList<string> names)
    {
        var appDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(appDirectory))
            yield break;

        foreach (var name in names)
            yield return Path.Combine(appDirectory, PlatformFileName(name));
    }

    internal static string PlatformFileName(string name)
    {
        if (OperatingSystem.IsWindows())
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        if (OperatingSystem.IsMacOS())
            return name.Contains(".dylib", StringComparison.OrdinalIgnoreCase) ? name : name + ".dylib";
        return name.Contains(".so", StringComparison.Ordinal) ? name : name + ".so";
    }

    private static IEnumerable<string> SystemCandidates(IReadOnlyList<string> names)
    {
        if (!OperatingSystem.IsWindows())
        {
            // dlopen/dyld searches the configured loader paths and system cache for a bare name;
            // neither treats AppContext.BaseDirectory as an implicit search directory.
            foreach (var name in names)
                yield return name;
            yield break;
        }

        // Windows' ordinary DLL search checks the application directory early. Use absolute
        // System32/PATH paths so an app-local DLL cannot shadow a system-installed copy.
        var comparer = StringComparer.OrdinalIgnoreCase;
        var appDirectory = NormalizeDirectory(AppContext.BaseDirectory);
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            directories.Add(Environment.SystemDirectory);
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var seen = new HashSet<string>(comparer);
        foreach (var directory in directories)
        {
            var normalized = NormalizeDirectory(directory);
            if (normalized is null || comparer.Equals(normalized, appDirectory) || !seen.Add(normalized))
                continue;
            foreach (var name in names)
                yield return Path.Combine(normalized, PlatformFileName(name));
        }
    }

    private static bool TryLoadExplicitPaths(
        IEnumerable<string>? candidates, out nint handle, out string? loadedCandidate)
    {
        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                    continue;
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    loadedCandidate = candidate;
                    return true;
                }
            }
        }

        handle = nint.Zero;
        loadedCandidate = null;
        return false;
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            return Path.GetFullPath(directory.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
