namespace NDILib.Runtime;

public static class NDILibraryNames
{
    /// <summary>The library name used in <c>[LibraryImport]</c> attributes throughout NDILib.</summary>
    public const string Default = "libndi.so.6";

    public static readonly string[] LinuxCandidates   = ["libndi.so.6", "libndi.so", "libndi"];
    /// <summary>APK native libraries must be named <c>lib*.so</c> - no versioned soname exists.</summary>
    public static readonly string[] AndroidCandidates = ["libndi.so", "libndi"];
    public static readonly string[] MacCandidates     = ["libndi.6.dylib", "libndi.dylib", "libndi"];
    public static readonly string[] WindowsCandidates = ["Processing.NDI.Lib.x64", "Processing.NDI.Lib.x86"];
}
