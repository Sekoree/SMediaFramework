namespace S.Media.FFmpeg.Encode;

/// <summary>Audio encoder settings for <see cref="FFmpegAudioFileOutput"/>.</summary>
public sealed record FFmpegAudioFileOutputOptions
{
    public FFmpegAudioCodec Codec { get; init; } = FFmpegAudioCodec.Aac;

    /// <summary>Target bitrate in bits/s (0 = codec default).</summary>
    public long Bitrate { get; init; }
}
