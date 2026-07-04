using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using S.Media.FFmpeg.Common;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.Decode.FFmpeg;

/// <summary>One decoded subtitle event in libass chunk form: the ASS dialogue body (the
/// <c>ReadOrder,Layer,Style,…,Text</c> fields, no timing) plus its absolute start and duration in milliseconds.</summary>
public readonly record struct DecodedSubtitleEvent(byte[] Body, long StartMs, long DurationMs);

/// <summary>An embedded font attachment (e.g. an MKV font) — its filename and raw bytes, fed to libass via AddFont.</summary>
public readonly record struct DecodedSubtitleFont(string Name, byte[] Data);

/// <summary>A subtitle stream decoded to libass-ready pieces: the ASS header (script info + styles + the events
/// format line), the timed dialogue events, and any embedded fonts.</summary>
public sealed record DecodedSubtitleTrack(
    byte[] Header,
    IReadOnlyList<DecodedSubtitleEvent> Events,
    IReadOnlyList<DecodedSubtitleFont> Fonts);

/// <summary>
/// Decodes a container's (or sidecar file's) subtitle stream into ASS events via libav. FFmpeg's text decoders
/// convert <em>every</em> text format — SRT/VTT/MicroDVD/SAMI/SubViewer/STL/… and ASS/SSA — into ASS dialogue
/// (the <c>SUBTITLE_ASS</c> rect form), so this single path feeds libass uniformly for sidecar files and
/// in-container streams alike. Embedded font attachments come along for libass to use. Bitmap subtitles
/// (PGS/DVB/VobSub) are <em>not</em> handled here — they are images, rendered without libass.
/// </summary>
public static unsafe class FFmpegSubtitleDecoder
{
    /// <summary>
    /// Decodes the subtitle stream at <paramref name="streamIndex"/> (or the best subtitle stream when
    /// <c>-1</c>) of <paramref name="path"/>. Throws if the file can't be opened or has no decodable text
    /// subtitle stream.
    /// </summary>
    public static DecodedSubtitleTrack Decode(string path, int streamIndex = -1)
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

            var header = CopyBytes(codecCtx->subtitle_header, codecCtx->subtitle_header_size);
            var fonts = ReadFonts(fmt);
            var events = new List<DecodedSubtitleEvent>();

            packet = av_packet_alloc();
            if (packet == null)
                throw new OutOfMemoryException("av_packet_alloc returned NULL");
            var msBase = new AVRational { num = 1, den = 1000 };

            while (av_read_frame(fmt, packet) >= 0)
            {
                if (packet->stream_index == subIndex)
                    DecodePacket(codecCtx, packet, stream->time_base, msBase, events);
                av_packet_unref(packet);
            }

            return new DecodedSubtitleTrack(header, events, fonts);
        }
        finally
        {
            if (packet != null) { var p = packet; av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; avcodec_free_context(&c); }
            if (fmt != null) { var f = fmt; avformat_close_input(&f); }
        }
    }

    private static void DecodePacket(
        AVCodecContext* codecCtx, AVPacket* packet, AVRational timeBase, AVRational msBase, List<DecodedSubtitleEvent> events)
    {
        AVSubtitle sub;
        var got = 0;
        var dret = avcodec_decode_subtitle2(codecCtx, &sub, &got, packet);
        if (dret < 0 || got == 0)
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
            var durationMs = sub.end_display_time > sub.start_display_time
                ? sub.end_display_time - sub.start_display_time
                : packet->duration > 0
                    ? av_rescale_q(packet->duration, timeBase, msBase)
                    : 0;
            for (uint i = 0; i < sub.num_rects; i++)
            {
                var rect = sub.rects[i];
                if (durationMs > 0 && rect->type == AVSubtitleType.SUBTITLE_ASS && rect->ass != null)
                    events.Add(new DecodedSubtitleEvent(CopyCString(rect->ass), startMs, durationMs));
            }
        }
        finally
        {
            avsubtitle_free(&sub);
        }
    }

    private static IReadOnlyList<DecodedSubtitleFont> ReadFonts(AVFormatContext* fmt)
    {
        List<DecodedSubtitleFont>? fonts = null;
        for (uint i = 0; i < fmt->nb_streams; i++)
        {
            var st = fmt->streams[i];
            if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_ATTACHMENT)
                continue;

            var data = CopyBytes(st->codecpar->extradata, st->codecpar->extradata_size);
            if (data.Length == 0)
                continue;

            var name = GetTag(st->metadata, "filename") ?? $"attachment{i}";
            var mime = GetTag(st->metadata, "mimetype") ?? string.Empty;
            if (!IsFont(name, mime))
                continue;

            (fonts ??= []).Add(new DecodedSubtitleFont(name, data));
        }

        return (IReadOnlyList<DecodedSubtitleFont>?)fonts ?? [];
    }

    private static bool IsFont(string name, string mime) =>
        mime.Contains("font", StringComparison.OrdinalIgnoreCase) ||
        mime.Contains("sfnt", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);

    private static byte[] CopyBytes(byte* ptr, int size) =>
        ptr == null || size <= 0 ? [] : new ReadOnlySpan<byte>(ptr, size).ToArray();

    private static byte[] CopyCString(byte* ptr)
    {
        if (ptr == null)
            return [];
        var len = 0;
        while (ptr[len] != 0)
            len++;
        return new ReadOnlySpan<byte>(ptr, len).ToArray();
    }

    private static string? GetTag(AVDictionary* dict, string key)
    {
        var entry = av_dict_get(dict, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((nint)entry->value);
    }
}
