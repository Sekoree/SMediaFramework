using FFmpeg.AutoGen;
using S.Media.FFmpeg.Common;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.Decode.FFmpeg;

/// <summary>One placed bitmap of a bitmap-subtitle cue: premultiplied BGRA32 pixels at (<see cref="X"/>,
/// <see cref="Y"/>), sized <see cref="W"/>×<see cref="H"/>, in the subtitle's authored frame.</summary>
public readonly record struct BitmapSubtitleImage(byte[] Bgra, int X, int Y, int W, int H);

/// <summary>A bitmap-subtitle cue shown over <c>[StartMs, EndMs)</c> - one or more placed images.</summary>
public sealed record BitmapSubtitleCue(long StartMs, long EndMs, IReadOnlyList<BitmapSubtitleImage> Images);

/// <summary>A decoded bitmap subtitle (PGS / DVB / DVD-VobSub): the authored frame size the images sit in, plus
/// the timed cues.</summary>
public sealed record DecodedBitmapSubtitle(int Width, int Height, IReadOnlyList<BitmapSubtitleCue> Cues);

/// <summary>
/// Decodes a bitmap subtitle stream (PGS / DVB / DVD-VobSub) via libav into placed, premultiplied-BGRA images with
/// timing. Bitmap subtitles are images - composited directly, with no libass. Each presentation's bitmap shows
/// until the next packet replaces or clears it, so a cue's end time is the next presentation's start.
/// </summary>
public static unsafe class FFmpegBitmapSubtitleDecoder
{
    public static DecodedBitmapSubtitle Decode(string path, int streamIndex = -1)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* fmt = null;
        AVCodecContext* codecCtx = null;
        AVPacket* packet = null;
        try
        {
            var ret = avformat_open_input(&fmt, path, null, null);
            FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
            ret = avformat_find_stream_info(fmt, null);
            FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));

            var subIndex = streamIndex >= 0
                ? streamIndex
                : av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_SUBTITLE, -1, -1, null, 0);
            if (subIndex < 0 || subIndex >= fmt->nb_streams)
                throw new InvalidOperationException($"No subtitle stream found in '{path}'.");

            var stream = fmt->streams[subIndex];
            var codec = avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"No decoder for subtitle codec id {stream->codecpar->codec_id}.");

            codecCtx = avcodec_alloc_context3(codec);
            if (codecCtx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");
            ret = avcodec_parameters_to_context(codecCtx, stream->codecpar);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));
            ret = avcodec_open2(codecCtx, codec, null);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

            packet = av_packet_alloc();
            if (packet == null)
                throw new OutOfMemoryException("av_packet_alloc returned NULL");
            var msBase = new AVRational { num = 1, den = 1000 };

            // Collect (start, optional explicit end, images); an empty image list is a "clear".
            var presentations = new List<BitmapPresentation>();
            var maxW = codecCtx->width;
            var maxH = codecCtx->height;

            while (av_read_frame(fmt, packet) >= 0)
            {
                if (packet->stream_index == subIndex)
                    DecodePacket(codecCtx, packet, stream->time_base, msBase, presentations, ref maxW, ref maxH);
                av_packet_unref(packet);
            }

            presentations.Sort(static (left, right) => left.StartMs.CompareTo(right.StartMs));
            var cues = new List<BitmapSubtitleCue>();
            for (var i = 0; i < presentations.Count; i++)
            {
                if (presentations[i].Images.Count == 0)
                    continue;
                var presentation = presentations[i];
                var nextStart = i + 1 < presentations.Count
                    ? presentations[i + 1].StartMs
                    : presentation.StartMs + 5000;
                var end = presentation.EndMs is { } explicitEnd
                    ? Math.Min(explicitEnd, nextStart)
                    : nextStart;
                if (end > presentation.StartMs)
                    cues.Add(new BitmapSubtitleCue(presentation.StartMs, end, presentation.Images));
            }

            return new DecodedBitmapSubtitle(Math.Max(1, maxW), Math.Max(1, maxH), cues);
        }
        finally
        {
            if (packet != null) { var p = packet; av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; avcodec_free_context(&c); }
            if (fmt != null) { var f = fmt; avformat_close_input(&f); }
        }
    }

    private static void DecodePacket(
        AVCodecContext* codecCtx, AVPacket* packet, AVRational timeBase, AVRational msBase,
        List<BitmapPresentation> presentations, ref int maxW, ref int maxH)
    {
        AVSubtitle sub;
        var got = 0;
        if (avcodec_decode_subtitle2(codecCtx, &sub, &got, packet) < 0 || got == 0)
            return;

        try
        {
            var packetStartMs = packet->pts != AV_NOPTS_VALUE
                ? av_rescale_q(packet->pts, timeBase, msBase)
                : 0;
            var decodedStartMs = sub.pts != AV_NOPTS_VALUE
                ? av_rescale_q(sub.pts, new AVRational { num = 1, den = AV_TIME_BASE }, msBase)
                : packetStartMs;
            var startMs = decodedStartMs + sub.start_display_time;
            long? endMs = sub.end_display_time > sub.start_display_time
                ? decodedStartMs + sub.end_display_time
                : null;
            var images = new List<BitmapSubtitleImage>();
            for (uint i = 0; i < sub.num_rects; i++)
            {
                var rect = sub.rects[i];
                if (rect->type != AVSubtitleType.SUBTITLE_BITMAP || rect->w <= 0 || rect->h <= 0)
                    continue;
                images.Add(new BitmapSubtitleImage(ConvertRect(rect), rect->x, rect->y, rect->w, rect->h));
                maxW = Math.Max(maxW, rect->x + rect->w);
                maxH = Math.Max(maxH, rect->y + rect->h);
            }

            presentations.Add(new BitmapPresentation(startMs, endMs, images));
        }
        finally
        {
            avsubtitle_free(&sub);
        }
    }

    private sealed record BitmapPresentation(
        long StartMs,
        long? EndMs,
        List<BitmapSubtitleImage> Images);

    // Palette-indexed (PAL8) → premultiplied BGRA32. libav's subtitle palette is RGB32: each uint32 reads as
    // 0xAARRGGBB on a little-endian host.
    private static byte[] ConvertRect(AVSubtitleRect* rect)
    {
        int w = rect->w, h = rect->h;
        var indices = rect->data[0];
        var stride = rect->linesize[0];
        var palette = (uint*)rect->data[1];
        var bgra = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var srcRow = indices + (nint)y * stride;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var color = palette[srcRow[x]];
                int a = (byte)(color >> 24);
                int r = (byte)(color >> 16);
                int g = (byte)(color >> 8);
                int b = (byte)color;
                var di = dstRow + x * 4;
                bgra[di + 0] = (byte)(b * a / 255);
                bgra[di + 1] = (byte)(g * a / 255);
                bgra[di + 2] = (byte)(r * a / 255);
                bgra[di + 3] = (byte)a;
            }
        }

        return bgra;
    }
}
