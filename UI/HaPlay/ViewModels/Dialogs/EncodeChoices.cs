namespace HaPlay.ViewModels.Dialogs;

/// <summary>A combo entry: user-facing <see cref="Label"/> over the persisted enum-name <see cref="Value"/>.
/// The dialogs bind <c>SelectedValue</c> to their string property and <c>SelectedValueBinding</c> to
/// <see cref="Value"/>, so the raw enum names never show while persistence stays string-based.</summary>
public sealed record EncodeChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>The labelled choice lists for the encode dialogs (file record + live stream). Video/audio
/// codecs are grouped consumer-first, then professional/mastering.</summary>
public static class EncodeChoices
{
    public static readonly EncodeChoice[] Containers =
    [
        new("MP4", "Mp4"),
        new("Matroska (MKV)", "Matroska"),
        new("QuickTime (MOV)", "Mov"),
    ];

    public static readonly EncodeChoice[] OutputModes =
    [
        new("Video + audio", "VideoAndAudio"),
        new("Video only", "VideoOnly"),
        new("Audio only", "AudioOnly"),
    ];

    /// <summary>Full video codec set - shown by the file recorder (all containers). Consumer first.</summary>
    public static readonly EncodeChoice[] VideoCodecs =
    [
        new("H.264 / AVC", "H264"),
        new("H.265 / HEVC", "Hevc"),
        new("AV1", "Av1"),
        new("VP9", "Vp9"),
        new("Apple ProRes 422", "ProRes422"),
        new("Apple ProRes 4444 (alpha)", "ProRes4444"),
        new("Avid DNxHR", "DnxHr"),
        new("FFV1 (lossless)", "Ffv1"),
        new("MPEG-4 (fallback)", "Mpeg4"),
    ];

    /// <summary>Streaming video codecs - only the ones that carry over RTMP/SRT/MPEG-TS/HLS.</summary>
    public static readonly EncodeChoice[] StreamVideoCodecs =
    [
        new("H.264 / AVC", "H264"),
        new("H.265 / HEVC", "Hevc"),
        new("AV1", "Av1"),
    ];

    public static readonly EncodeChoice[] AudioCodecs =
    [
        new("AAC", "Aac"),
        new("Opus", "Opus"),
        new("MP3", "Mp3"),
        new("Dolby Digital (AC-3)", "Ac3"),
        new("Dolby Digital Plus (E-AC-3)", "Eac3"),
        new("FLAC (lossless)", "Flac"),
        new("Apple Lossless (ALAC)", "Alac"),
        new("PCM 16-bit", "Pcm16"),
        new("PCM 24-bit", "Pcm24"),
    ];

    /// <summary>Streaming audio codecs (broadcast-safe).</summary>
    public static readonly EncodeChoice[] StreamAudioCodecs =
    [
        new("AAC", "Aac"),
        new("Dolby Digital (AC-3)", "Ac3"),
        new("Dolby Digital Plus (E-AC-3)", "Eac3"),
        new("MP3", "Mp3"),
        new("Opus", "Opus"),
    ];

    public static readonly EncodeChoice[] PushProtocols =
    [
        new("RTMP", "Rtmp"),
        new("SRT", "Srt"),
        new("RTSP", "Rtsp"),
    ];

    public static readonly EncodeChoice[] VideoBitrateModes =
    [
        new("Average bitrate (ABR)", "Average"),
        new("Constant bitrate (CBR)", "Constant"),
    ];

    public static readonly EncodeChoice[] MaximumBFrames =
    [
        new("Automatic", "Auto"),
        new("Disabled (0)", "0"),
        new("1", "1"),
        new("2", "2"),
        new("3", "3"),
    ];

    public static readonly string[] Presets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow"];
}
