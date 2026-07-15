using System.Runtime.InteropServices;
using ProjectMLib;

namespace S.Media.Visualizer.ProjectM;

/// <summary>Finds the companion Milkdrop texture pack installed by build-projectm.sh and passes
/// stable UTF-8 paths to projectM before the first preset is loaded.</summary>
internal static class ProjectMTextureSearchPaths
{
    internal static string[] Resolve(string? presetDirectory)
    {
        if (string.IsNullOrWhiteSpace(presetDirectory))
            return [];

        try
        {
            var presetRoot = Path.GetFullPath(presetDirectory);
            var parent = Directory.GetParent(presetRoot);
            var grandparent = parent?.Parent;
            return new[]
                {
                    Path.Combine(presetRoot, "textures"),
                    parent is null ? null : Path.Combine(parent.FullName, "textures"),
                    grandparent is null ? null : Path.Combine(grandparent.FullName, "textures"),
                }
                .OfType<string>()
                .Where(Directory.Exists)
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static unsafe string[] Configure(nint projectM, string? presetDirectory)
    {
        var paths = Resolve(presetDirectory);
        if (paths.Length == 0)
            return paths;

        var nativePaths = new nint[paths.Length];
        try
        {
            for (var i = 0; i < paths.Length; i++)
                nativePaths[i] = Marshal.StringToCoTaskMemUTF8(paths[i]);

            fixed (nint* pathArray = nativePaths)
                Native.projectm_set_texture_search_paths(projectM, pathArray, (nuint)nativePaths.Length);
        }
        finally
        {
            foreach (var path in nativePaths)
            {
                if (path != 0)
                    Marshal.FreeCoTaskMem(path);
            }
        }

        return paths;
    }
}
