using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

/// <summary>Options for <see cref="MediaContainerDecoder.OpenStream"/>.</summary>
public sealed class MediaContainerOpenStreamOptions
{
    /// <summary>When true, the stream supports libav seek (maps to <see cref="Stream.CanSeek"/> as well).</summary>
    public bool IsSeekable { get; init; }

    /// <summary>When true, copies the stream to a temp file instead of using in-memory AVIO.</summary>
    public bool SpoolToDisk { get; init; }

    /// <summary>Optional filename hint (e.g. <c>clip.mkv</c>) to help format probing.</summary>
    public string? ProbeHintName { get; init; }

    public VideoDecoderOpenOptions? VideoOptions { get; init; }
}
