using S.Media.Core.Buses;
using S.Media.Decode.FFmpeg.Video;

namespace S.Media.Decode.FFmpeg;

/// <summary>
/// Lightweight container-tag probe for the metadata hub: opens the container (no decoders), reads the
/// format-level tags (title/artist/album) and the attached-picture stream's encoded bytes (cover art),
/// and closes. The host wires <see cref="TryRead"/> into <c>ShowSession</c>'s metadata-probe delegate;
/// Core/Session never reference FFmpeg. Runs off the dispatcher (the session probes on a worker).
/// </summary>
public static unsafe class MediaTagProbe
{
    public static MediaItemMetadata? TryRead(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* fmt = null;
        var ret = avformat_open_input(&fmt, mediaPath, null, null);
        if (ret < 0 || fmt is null)
            return null;
        try
        {
            avformat_find_stream_info(fmt, null);

            var title = GetTag(fmt->metadata, "title");
            var artist = GetTag(fmt->metadata, "artist") ?? GetTag(fmt->metadata, "album_artist");
            var album = GetTag(fmt->metadata, "album");
            var duration = fmt->duration > 0
                ? TimeSpan.FromTicks(fmt->duration * 10) // AV_TIME_BASE (µs) → 100 ns ticks
                : (TimeSpan?)null;

            CoverArt? cover = null;
            for (var i = 0; i < fmt->nb_streams; i++)
            {
                var stream = fmt->streams[i];
                if ((stream->disposition & AV_DISPOSITION_ATTACHED_PIC) == 0)
                    continue;
                var packet = stream->attached_pic;
                if (packet.data is null || packet.size <= 0)
                    continue;

                var bytes = new byte[packet.size];
                new ReadOnlySpan<byte>(packet.data, packet.size).CopyTo(bytes);
                var mime = stream->codecpar->codec_id switch
                {
                    AVCodecID.AV_CODEC_ID_MJPEG => "image/jpeg",
                    AVCodecID.AV_CODEC_ID_PNG => "image/png",
                    AVCodecID.AV_CODEC_ID_BMP => "image/bmp",
                    AVCodecID.AV_CODEC_ID_WEBP => "image/webp",
                    _ => null,
                };
                var encoded = new ReadOnlyMemory<byte>(bytes);
                cover = new CoverArt(encoded, mime)
                {
                    // Lazy pixel decode through the normal decode stack (attached pics open as
                    // single-frame video); only consumers that need pixels pay for it.
                    Decode = () => TryDecodeCover(bytes),
                };
                break;
            }

            if (title is null && artist is null && album is null && cover is null && duration is null)
                return null;

            return new MediaItemMetadata(title, artist, album, duration, mediaPath, cover);
        }
        catch
        {
            return null; // metadata is best-effort - never fail a fire over it
        }
        finally
        {
            avformat_close_input(&fmt);
        }
    }

    private static VideoFrame? TryDecodeCover(byte[] encodedBytes)
    {
        try
        {
            using var stream = new MemoryStream(encodedBytes, writable: false);
            using var decoder = MediaContainerDecoder.OpenStream(stream, isSeekable: true, probeHintName: "cover",
                new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            if (!decoder.HasVideo)
                return null;
            decoder.Video.SelectOutputFormat(PixelFormat.Bgra32);
            return decoder.Video.TryReadNextFrame(out var frame) ? frame : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTag(AVDictionary* dict, string key)
    {
        var entry = av_dict_get(dict, key, null, 0);
        if (entry is null)
            return null;
        var value = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)entry->value);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
