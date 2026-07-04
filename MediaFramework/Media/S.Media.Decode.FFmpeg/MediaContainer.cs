using S.Media.Decode.FFmpeg.Video;

namespace S.Media.Decode.FFmpeg;

/// <summary>
/// Discoverable entry point for combined audio+video container decode.
/// Forwards to <see cref="MediaContainerDecoder"/>.
/// </summary>
public static class MediaContainer
{
    /// <summary>Opens a local media file (path or file URI).</summary>
    public static MediaContainerDecoder OpenFile(string path, VideoDecoderOpenOptions? options = null) =>
        MediaContainerDecoder.Open(path, options);

    /// <summary>Opens a media URI (http, rtsp, file, …).</summary>
    public static MediaContainerDecoder OpenUri(Uri uri, VideoDecoderOpenOptions? options = null) =>
        MediaContainerDecoder.OpenUri(uri, options);

    /// <summary>Opens a finite stream via in-memory AVIO (no temp-file spool).</summary>
    public static MediaContainerDecoder OpenStream(
        Stream stream,
        bool isSeekable = false,
        string? probeHintName = null,
        VideoDecoderOpenOptions? options = null) =>
        MediaContainerDecoder.OpenStream(stream, isSeekable, probeHintName, options);

    /// <summary>Opens a stream by spooling to a temporary file on disk.</summary>
    public static MediaContainerDecoder OpenStreamSpooled(
        Stream stream,
        string? probeHintName = null,
        VideoDecoderOpenOptions? options = null) =>
        MediaContainerDecoder.OpenStreamSpooled(stream, probeHintName, options);
}
