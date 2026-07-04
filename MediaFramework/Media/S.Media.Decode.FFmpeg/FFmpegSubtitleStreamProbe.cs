using FFmpeg.AutoGen;
using S.Media.FFmpeg.Common;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.Decode.FFmpeg;

public enum FFmpegSubtitleStreamKind
{
    Unsupported,
    Text,
    Bitmap,
}

/// <summary>Cheap subtitle codec probe used to choose the text or bitmap decoder without scanning a stream twice.</summary>
public static unsafe class FFmpegSubtitleStreamProbe
{
    public static FFmpegSubtitleStreamKind Probe(string path, int streamIndex = -1)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* format = null;
        try
        {
            FFmpegException.ThrowIfError(avformat_open_input(&format, path, null, null), nameof(avformat_open_input));
            FFmpegException.ThrowIfError(avformat_find_stream_info(format, null), nameof(avformat_find_stream_info));

            var index = streamIndex >= 0
                ? streamIndex
                : av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_SUBTITLE, -1, -1, null, 0);
            if (index < 0 || index >= format->nb_streams)
                return FFmpegSubtitleStreamKind.Unsupported;

            var stream = format->streams[index];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                return FFmpegSubtitleStreamKind.Unsupported;

            var descriptor = avcodec_descriptor_get(stream->codecpar->codec_id);
            if (descriptor == null)
                return FFmpegSubtitleStreamKind.Unsupported;
            if ((descriptor->props & AV_CODEC_PROP_TEXT_SUB) != 0)
                return FFmpegSubtitleStreamKind.Text;
            if ((descriptor->props & AV_CODEC_PROP_BITMAP_SUB) != 0)
                return FFmpegSubtitleStreamKind.Bitmap;
            return FFmpegSubtitleStreamKind.Unsupported;
        }
        finally
        {
            if (format != null)
            {
                var f = format;
                avformat_close_input(&f);
            }
        }
    }
}
