using Microsoft.Extensions.Logging;
using S.Media.Encode.FFmpeg.Internal;

namespace S.Media.Encode.FFmpeg.Sinks;

/// <summary>
/// Writes the session's packets through one libavformat muxer to an <see cref="EncodeIoTarget"/> -
/// a local file or a protocol URL (rtmp/srt/rtsp/udp). Owns the <c>AVFormatContext</c>; streams are
/// created from the session's codec parameters in <see cref="OnStreamsReady"/>, the header is written
/// there, packets are rescaled from the session (encoder) timebase to each stream's muxer timebase,
/// and <see cref="Finish"/> writes the trailer. Runs entirely on the caller's thread (the session
/// encode worker; Phase 3 wraps push targets in a queue+drain thread so a stalled network write
/// cannot stall the encoder).
/// </summary>
internal sealed unsafe class MuxPacketSink : IEncodedPacketSink
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Encode.FFmpeg.MuxPacketSink");

    private readonly EncodeIoTarget _target;
    private readonly EncodeContainer _container;
    private AVFormatContext* _fmt;
    private AVPacket* _scratch;
    private WriteCallbackAvio? _callbackAvio;
    private AVRational[] _sourceTimeBases = [];
    private bool _headerWritten;
    private bool _finished;
    private bool _disposed;
    private long _packetsWritten;

    public MuxPacketSink(EncodeIoTarget target, EncodeContainer container)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _container = container;
        FFmpegRuntime.EnsureInitialized();
    }

    /// <summary>When set, the sink flushes avio after every packet and reports the boundary (true for a
    /// key video packet). The LAN TS fan-out uses this to record client join points without parsing TS.</summary>
    public Action<bool>? PacketBoundaryWritten { get; init; }

    public string Name => _target switch
    {
        FileEncodeTarget f => $"file:{f.Path}",
        UrlEncodeTarget u => u.Url,
        _ => _target.AvioUrl,
    };

    public long PacketsWritten => Volatile.Read(ref _packetsWritten);

    /// <summary>Container bytes written so far (avio position); 0 before the header goes out.</summary>
    public long BytesWritten
    {
        get
        {
            if (_callbackAvio is { } cb)
                return cb.BytesWritten;
            var fmt = _fmt;
            if (fmt is null || fmt->pb is null || !_headerWritten)
                return 0;
            var pos = avio_tell(fmt->pb);
            return pos > 0 ? pos : 0;
        }
    }

    public void OnStreamsReady(IReadOnlyList<EncodedStreamInfo> streams)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fmt is not null)
            throw new InvalidOperationException("OnStreamsReady called twice.");

        var formatName = _target.FormatNameOverride ?? FfmpegEncodeMaps.ContainerShortName(_container);
        AVFormatContext* ctx = null;
        var ret = avformat_alloc_output_context2(&ctx, null, formatName, _target.AvioUrl);
        FFmpegException.ThrowIfError(ret, nameof(avformat_alloc_output_context2));
        if (ctx is null)
            throw new FFmpegException(0, "avformat_alloc_output_context2 returned NULL");
        _fmt = ctx;

        _sourceTimeBases = new AVRational[streams.Count];
        for (var i = 0; i < streams.Count; i++)
        {
            var info = streams[i];
            var stream = avformat_new_stream(_fmt, null);
            if (stream is null)
                throw new OutOfMemoryException("avformat_new_stream returned NULL");

            ret = avcodec_parameters_copy(stream->codecpar, info.CodecParameters);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_copy));
            stream->time_base = info.TimeBase; // muxer may adjust in write_header; packets rescale to the final value
            _sourceTimeBases[i] = info.TimeBase;

            if (info.Name is { Length: > 0 } name)
                av_dict_set(&stream->metadata, "title", name, 0);
            if (info.Language is { Length: > 0 } lang)
                av_dict_set(&stream->metadata, "language", lang, 0);
        }

        if (_target is CallbackEncodeTarget callback)
        {
            _callbackAvio = new WriteCallbackAvio(callback.OnBytes);
            _fmt->pb = _callbackAvio.Context;
            _fmt->flags |= AVFMT_FLAG_CUSTOM_IO;
        }
        else if ((_fmt->oformat->flags & AVFMT_NOFILE) == 0)
        {
            ret = avio_open(&_fmt->pb, _target.AvioUrl, AVIO_FLAG_WRITE);
            FFmpegException.ThrowIfError(ret, nameof(avio_open));
        }

        AVDictionary* muxerOptions = null;
        if (_target.MuxerOptions is { Count: > 0 } opts)
            foreach (var (key, value) in opts)
                av_dict_set(&muxerOptions, key, value, 0);
        try
        {
            ret = avformat_write_header(_fmt, &muxerOptions);
            FFmpegException.ThrowIfError(ret, nameof(avformat_write_header));
        }
        finally
        {
            av_dict_free(&muxerOptions);
        }

        _headerWritten = true;

        _scratch = av_packet_alloc();
        if (_scratch is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");

        Trace.LogDebug("MuxPacketSink {Name}: header written ({Streams} streams, muxer={Muxer})",
            Name, streams.Count, formatName);
    }

    public void OnPacket(AVPacket* packet, bool keyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_headerWritten || _finished)
            throw new InvalidOperationException("OnPacket outside OnStreamsReady…Finish window.");

        // The session's packet is shared across sinks - never mutate it. Work on an owned ref whose
        // timestamps we rescale to this muxer's stream timebase; av_interleaved_write_frame unrefs it.
        var ret = av_packet_ref(_scratch, packet);
        FFmpegException.ThrowIfError(ret, nameof(av_packet_ref));
        try
        {
            var stream = _fmt->streams[_scratch->stream_index];
            var isVideo = stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
            av_packet_rescale_ts(_scratch, _sourceTimeBases[_scratch->stream_index], stream->time_base);
            ret = av_interleaved_write_frame(_fmt, _scratch);
            FFmpegException.ThrowIfError(ret, nameof(av_interleaved_write_frame));
            Interlocked.Increment(ref _packetsWritten);

            if (PacketBoundaryWritten is { } boundary)
            {
                // Flush per packet so the byte callback observes complete container writes at packet
                // granularity (the fan-out buffer records join points at these boundaries).
                if (_fmt->pb is not null)
                    avio_flush(_fmt->pb);
                boundary(isVideo && keyframe);
            }
        }
        catch
        {
            av_packet_unref(_scratch);
            throw;
        }
    }

    public void Finish()
    {
        if (_disposed || _finished || !_headerWritten)
        {
            _finished = true;
            return;
        }

        _finished = true;
        var ret = av_write_trailer(_fmt);
        FFmpegException.ThrowIfError(ret, nameof(av_write_trailer));
        Trace.LogDebug("MuxPacketSink {Name}: trailer written ({Packets} packets)", Name, PacketsWritten);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_fmt is not null)
        {
            if (_headerWritten && !_finished)
                MediaDiagnostics.SwallowDisposeErrors(() => av_write_trailer(_fmt), "MuxPacketSink.Dispose: av_write_trailer");

            if (_callbackAvio is not null)
            {
                _fmt->pb = null; // custom AVIO is owned by the bridge, not avformat
            }
            else if ((_fmt->oformat->flags & AVFMT_NOFILE) == 0 && _fmt->pb is not null)
            {
                MediaDiagnostics.SwallowDisposeErrors(() =>
                {
                    var f = _fmt;
                    avio_closep(&f->pb);
                }, "MuxPacketSink.Dispose: avio_closep");
            }

            var fmt = _fmt;
            avformat_free_context(fmt);
            _fmt = null;
        }

        if (_callbackAvio is not null)
        {
            MediaDiagnostics.SwallowDisposeErrors(_callbackAvio.Dispose, "MuxPacketSink.Dispose: callback avio");
            _callbackAvio = null;
        }

        if (_scratch is not null)
        {
            var p = _scratch;
            av_packet_free(&p);
            _scratch = null;
        }
    }
}
