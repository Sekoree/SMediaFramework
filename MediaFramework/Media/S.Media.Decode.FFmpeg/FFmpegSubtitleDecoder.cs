using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using S.Media.FFmpeg.Common;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.Decode.FFmpeg;

/// <summary>One decoded subtitle event in libass chunk form: the ASS dialogue body (the
/// <c>ReadOrder,Layer,Style,…,Text</c> fields, no timing) plus its absolute start and duration in milliseconds.</summary>
public readonly record struct DecodedSubtitleEvent(byte[] Body, long StartMs, long DurationMs);

/// <summary>An embedded font attachment (e.g. an MKV font) - its filename and raw bytes, fed to libass via AddFont.</summary>
public readonly record struct DecodedSubtitleFont(string Name, byte[] Data);

/// <summary>A subtitle stream decoded to libass-ready pieces: the ASS header (script info + styles + the events
/// format line), the timed dialogue events, and any embedded fonts.</summary>
public sealed record DecodedSubtitleTrack(
    byte[] Header,
    IReadOnlyList<DecodedSubtitleEvent> Events,
    IReadOnlyList<DecodedSubtitleFont> Fonts);

/// <summary>
/// Decodes a container's (or sidecar file's) subtitle stream into ASS events via libav. FFmpeg's text decoders
/// convert <em>every</em> text format - SRT/VTT/MicroDVD/SAMI/SubViewer/STL/… and ASS/SSA - into ASS dialogue
/// (the <c>SUBTITLE_ASS</c> rect form), so this single path feeds libass uniformly for sidecar files and
/// in-container streams alike. Embedded font attachments come along for libass to use. Bitmap subtitles
/// (PGS/DVB/VobSub) are <em>not</em> handled here - they are images, rendered without libass.
/// </summary>
public static unsafe class FFmpegSubtitleDecoder
{
    /// <summary>
    /// Decodes the subtitle stream at <paramref name="streamIndex"/> (or the best subtitle stream when
    /// <c>-1</c>) of <paramref name="path"/>. Throws if the file can't be opened or has no decodable text
    /// subtitle stream. This demuxes the WHOLE container before returning - fine for sidecar files and
    /// small containers; for playback overlays on large movie files use
    /// <see cref="FFmpegSubtitleStreamReader"/> and feed events incrementally instead.
    /// </summary>
    public static DecodedSubtitleTrack Decode(string path, int streamIndex = -1)
    {
        using var reader = FFmpegSubtitleStreamReader.Open(path, streamIndex);
        var events = new List<DecodedSubtitleEvent>();
        while (reader.ReadBatch(events, maxEvents: int.MaxValue))
        {
        }

        return new DecodedSubtitleTrack(reader.Header, events, reader.Fonts);
    }

    internal static void DecodePacket(
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

    internal static IReadOnlyList<DecodedSubtitleFont> ReadFonts(AVFormatContext* fmt)
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

    internal static byte[] CopyBytes(byte* ptr, int size) =>
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

/// <summary>
/// Incremental subtitle decode: opens the stream (the ASS <see cref="Header"/> and embedded
/// <see cref="Fonts"/> are available immediately) and decodes dialogue events in caller-pulled batches.
/// Built for large containers - <see cref="FFmpegSubtitleDecoder.Decode"/> demuxes the whole file before
/// returning anything, which on a multi-GB movie is tens of seconds to minutes of "no subtitles" after
/// playback starts. Feeding batches into <c>AssSubtitleLayerSource.AppendEvents</c> instead makes events
/// near the playhead render as soon as the demux sweep passes them. Every non-subtitle stream is set to
/// <c>AVDISCARD_ALL</c> so the demuxer skips their packets. Single-threaded; not thread-safe.
/// </summary>
public sealed unsafe class FFmpegSubtitleStreamReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _codecCtx;
    private AVPacket* _packet;
    private readonly int _subIndex;
    private readonly AVRational _timeBase;
    private static readonly AVRational MsBase = new() { num = 1, den = 1000 };
    private bool _eof;
    private bool _disposed;

    /// <summary>The stream's ASS header (script info + styles + events format line).</summary>
    public byte[] Header { get; }

    /// <summary>Embedded font attachments (e.g. MKV fonts) for libass.</summary>
    public IReadOnlyList<DecodedSubtitleFont> Fonts { get; }

    /// <summary>Best-effort demux frontier in milliseconds: the timestamp of the last subtitle packet
    /// surfaced by <see cref="ReadBatch"/> (or the <see cref="SeekTo"/> target right after a seek).
    /// Coverage tracking uses it to know how far a sweep has progressed.</summary>
    public long PositionMs { get; private set; }

    private FFmpegSubtitleStreamReader(
        AVFormatContext* fmt, AVCodecContext* codecCtx, AVPacket* packet, int subIndex, AVRational timeBase,
        byte[] header, IReadOnlyList<DecodedSubtitleFont> fonts)
    {
        _fmt = fmt;
        _codecCtx = codecCtx;
        _packet = packet;
        _subIndex = subIndex;
        _timeBase = timeBase;
        Header = header;
        Fonts = fonts;
    }

    /// <summary>Opens the subtitle stream at <paramref name="streamIndex"/> (or the best subtitle stream
    /// when <c>-1</c>). Throws if the file can't be opened or has no decodable text subtitle stream.</summary>
    public static FFmpegSubtitleStreamReader Open(string path, int streamIndex = -1)
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

            // The demuxer only needs the subtitle packets - discard everything else at the demux level so
            // a movie's video/audio payload (the overwhelming bulk of the file) is skipped, not surfaced
            // packet-by-packet just to be unref'd here.
            for (uint i = 0; i < fmt->nb_streams; i++)
            {
                if (i != (uint)subIndex)
                    fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }

            codecCtx = avcodec_alloc_context3(codec);
            if (codecCtx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");
            ret = avcodec_parameters_to_context(codecCtx, stream->codecpar);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));
            ret = avcodec_open2(codecCtx, codec, null);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

            var header = FFmpegSubtitleDecoder.CopyBytes(codecCtx->subtitle_header, codecCtx->subtitle_header_size);
            var fonts = FFmpegSubtitleDecoder.ReadFonts(fmt);

            packet = av_packet_alloc();
            if (packet == null)
                throw new OutOfMemoryException("av_packet_alloc returned NULL");

            return new FFmpegSubtitleStreamReader(fmt, codecCtx, packet, subIndex, stream->time_base, header, fonts);
        }
        catch
        {
            if (packet != null) { var p = packet; av_packet_free(&p); }
            if (codecCtx != null) { var c = codecCtx; avcodec_free_context(&c); }
            if (fmt != null) { var f = fmt; avformat_close_input(&f); }
            throw;
        }
    }

    /// <summary>
    /// Demuxes forward and appends decoded events to <paramref name="into"/> until <paramref name="maxEvents"/>
    /// have been appended in this call or the stream ends. Returns <c>false</c> once the end of the stream has
    /// been reached (no further calls will yield events); a <c>true</c> return with zero appended events cannot
    /// happen - the call only returns early on the event budget.
    /// </summary>
    public bool ReadBatch(List<DecodedSubtitleEvent> into, int maxEvents)
    {
        ArgumentNullException.ThrowIfNull(into);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_eof || maxEvents <= 0)
            return !_eof;

        var appended = 0;
        while (appended < maxEvents && av_read_frame(_fmt, _packet) >= 0)
        {
            try
            {
                if (_packet->stream_index != _subIndex)
                    continue;
                if (_packet->pts != AV_NOPTS_VALUE)
                    PositionMs = av_rescale_q(_packet->pts, _timeBase, MsBase);
                var before = into.Count;
                FFmpegSubtitleDecoder.DecodePacket(_codecCtx, _packet, _timeBase, MsBase, into);
                appended += into.Count - before;
            }
            finally
            {
                av_packet_unref(_packet);
            }
        }

        if (appended < maxEvents)
            _eof = true;
        return !_eof;
    }

    /// <summary>
    /// Jumps the demux position to (at or before) <paramref name="target"/> - the seek-aware sweep: when
    /// the playhead lands in a region the sequential sweep has not reached, the caller seeks near it
    /// instead of waiting for the sweep to demux the whole file up to that point. Clears a previous
    /// end-of-stream so reading can resume. Returns <c>false</c> when the container cannot seek (the
    /// caller keeps sweeping sequentially). Converted-format decoders reset their ReadOrder counter on
    /// the flush this performs - callers feeding libass must dedupe/re-order events themselves.
    /// </summary>
    public bool SeekTo(TimeSpan target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target < TimeSpan.Zero)
            target = TimeSpan.Zero;

        // The discards must be lifted for the seek call itself: the matroska demuxer resolves the target
        // against its reference (video) track, and with that track discarded every seek collapses back to
        // the start of the file (verified empirically). Reads never happen between lift and re-apply, so
        // no non-subtitle packet is ever surfaced.
        for (uint i = 0; i < _fmt->nb_streams; i++)
            _fmt->streams[i]->discard = AVDiscard.AVDISCARD_DEFAULT;
        var ret = av_seek_frame(_fmt, -1, (long)(target.TotalSeconds * AV_TIME_BASE), AVSEEK_FLAG_BACKWARD);
        for (uint i = 0; i < _fmt->nb_streams; i++)
        {
            if (i != (uint)_subIndex)
                _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }

        if (ret < 0)
            return false;

        avcodec_flush_buffers(_codecCtx);
        _eof = false;
        PositionMs = (long)target.TotalMilliseconds; // refined by the next packet's actual pts
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_packet != null) { var p = _packet; av_packet_free(&p); _packet = null; }
        if (_codecCtx != null) { var c = _codecCtx; avcodec_free_context(&c); _codecCtx = null; }
        if (_fmt != null) { var f = _fmt; avformat_close_input(&f); _fmt = null; }
    }
}
