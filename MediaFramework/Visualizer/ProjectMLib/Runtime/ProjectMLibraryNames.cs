namespace ProjectMLib.Runtime;

public static class ProjectMLibraryNames
{
    /// <summary>The library name used in <c>[LibraryImport]</c> attributes throughout ProjectMLib.</summary>
    public const string Default = "projectM-4";

    public static readonly string[] LinuxCandidates =
        ["libprojectM-4.so.4", "libprojectM-4.so", "libprojectM-4"];

    /// <summary>APK native libraries must be named <c>lib*.so</c> - no versioned soname exists.</summary>
    public static readonly string[] AndroidCandidates =
        ["libprojectM-4.so", "libprojectM-4"];

    public static readonly string[] WindowsCandidates =
        ["projectM-4", "libprojectM-4"];

    public static readonly string[] MacCandidates =
        ["libprojectM-4.4.dylib", "libprojectM-4.dylib"];
}
