using S.Media.Core.Video;

namespace S.Media.FFmpeg.Encode;

/// <summary>Video encoder settings for <see cref="FFmpegVideoFileOutput"/>.</summary>
public sealed record FFmpegVideoFileOutputOptions
{
    public FFmpegVideoCodec Codec { get; init; } = FFmpegVideoCodec.H264;

    /// <summary>Target bitrate in bits/s (0 = codec default).</summary>
    public long Bitrate { get; init; }

    /// <summary>GOP size in frames (0 = codec default).</summary>
    public int GopSize { get; init; }

    /// <summary>When set, forces the libav pixel format; otherwise derived from <see cref="IVideoOutput.Configure"/>.</summary>
    public PixelFormat? EncodePixelFormat { get; init; }
}
