namespace PALib.Runtime;

public static class PortAudioLibraryNames
{
    public const string Default = "portaudio";

    public static readonly string[] LinuxCandidates = ["portaudio", "libportaudio.so.2"];
    public static readonly string[] WindowsCandidates = ["portaudio", "portaudio.dll"];
}
