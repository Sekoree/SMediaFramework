namespace S.Media.MiniAudio.Runtime;

public static class MiniAudioLibraryNames
{
    public const string Default = "smedia_miniaudio";

    public static readonly string[] LinuxCandidates = ["smedia_miniaudio", "libsmedia_miniaudio.so"];
    public static readonly string[] MacCandidates = ["smedia_miniaudio", "libsmedia_miniaudio.dylib"];
    public static readonly string[] WindowsCandidates = ["smedia_miniaudio", "smedia_miniaudio.dll"];
}
