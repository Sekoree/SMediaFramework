namespace PALib.Runtime;

public static class PortAudioLibraryNames
{
    public const string Default = "portaudio";

    public static readonly string[] LinuxCandidates = ["libportaudio.so.2", "libportaudio.so", "portaudio"];
    public static readonly string[] MacCandidates = ["libportaudio.2.dylib", "libportaudio.dylib", "portaudio"];
    public static readonly string[] WindowsCandidates = ["portaudio", "portaudio.dll"];
}
