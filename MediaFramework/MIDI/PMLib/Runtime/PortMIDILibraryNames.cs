namespace PMLib.Runtime;

/// <summary>
/// Known library file names for the PortMIDI native library on each supported platform.
/// </summary>
public static class PortMIDILibraryNames
{
    /// <summary>The default DLL import alias used by <c>[LibraryImport]</c> attributes throughout PMLib.</summary>
    public const string Default = "portmidi";

    /// <summary>Probe order on Linux.</summary>
    public static readonly string[] LinuxCandidates = ["libportmidi.so.2", "libportmidi.so", "portmidi"];

    /// <summary>Probe order on Windows (portmidi.dll is found automatically by the OS loader).</summary>
    public static readonly string[] WindowsCandidates = ["portmidi"];
}

