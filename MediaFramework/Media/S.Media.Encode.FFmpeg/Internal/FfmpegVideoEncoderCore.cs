using S.Media.Encode.FFmpeg.Sinks;

namespace S.Media.Encode.FFmpeg.Internal;

/// <summary>
/// The session's video leg: swscale (pixel conversion + optional resolution scale) into a
/// pre-allocated <c>AVFrame</c>, avcodec encode, packets handed to the caller. Single-threaded -
/// every call runs on the session's encode worker. Salvaged from the pre-rewrite FfmpegVideoEncoder,
/// reshaped from an IVideoOutput into a packet-producing core (the sink fan-out lives in the session)
/// and extended with CRF/preset rate control and scaling.
/// </summary>
internal sealed unsafe class FfmpegVideoEncoderCore : IDisposable
{
    /// <summary>Timebase all video packets are stamped in (fine-grained so VFR/clamped PTS survive).</summary>
    internal static readonly AVRational VideoTimeBase = new() { num = 1, den = 90_000 };

    private readonly VideoEncodeOptions _options;
    private AVCodecContext* _codec;
    private AVCodecParameters* _codecParameters;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private readonly byte*[] _srcLines = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstLines = new byte*[8];
    private readonly int[] _dstStride = new int[8];
    private VideoFormat _sourceFormat;
    private PixelFormat _encodePixel;
    private int _encodeWidth;
    private int _encodeHeight;
    private bool _disposed;

    internal FfmpegVideoEncoderCore(VideoEncodeOptions options, VideoFormat sourceFormat)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        FFmpegRuntime.EnsureInitialized();
        _sourceFormat = sourceFormat;

        _encodePixel = FfmpegEncodeMaps.PickVideoEncodePixel(sourceFormat.PixelFormat, options.Codec, options.EncodePixelFormat);
        if (FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel) is null)
            throw new NotSupportedException($"encode pixel format {_encodePixel} is not supported by this FFmpeg build.");

        (_encodeWidth, _encodeHeight) = ResolveScale(sourceFormat.Width, sourceFormat.Height, options.ScaleWidth, options.ScaleHeight);
        OpenCodec();
    }

    internal AVCodecParameters* CodecParameters => _codecParameters;

    internal AVRational TimeBase => VideoTimeBase;

    internal VideoFormat EncodedFormat => _sourceFormat with
    {
        Width = _encodeWidth,
        Height = _encodeHeight,
        PixelFormat = _encodePixel,
        // Review H7: a fixed target FPS is now really enforced by the session's tick scheduler - report it.
        FrameRate = _options.Fps > 0 ? new Rational(_options.Fps, 1) : _sourceFormat.FrameRate,
    };

    /// <summary>Even-rounded output dimensions: 0/0 = source; one side 0 = derived preserving aspect.</summary>
    internal static (int Width, int Height) ResolveScale(int srcW, int srcH, int scaleW, int scaleH)
    {
        var w = scaleW;
        var h = scaleH;
        if (w <= 0 && h <= 0)
            (w, h) = (srcW, srcH);
        else if (w <= 0)
            w = (int)Math.Round((double)srcW * h / srcH);
        else if (h <= 0)
            h = (int)Math.Round((double)srcH * w / srcW);

        w = Math.Max(2, w & ~1);
        h = Math.Max(2, h & ~1);
        return (w, h);
    }

    /// <summary>Encodes one frame stamped at <paramref name="ptsIn90kHz"/>; invokes <paramref name="onPacket"/>
    /// for every completed packet (timestamps in <see cref="VideoTimeBase"/>). Does NOT dispose the frame.</summary>
    internal void Encode(VideoFrame frame, long ptsIn90kHz, Action<IntPtr> onPacket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.DmabufNv12 is not null || frame.DmabufP010 is not null || frame.DmabufP016 is not null || frame.Win32Nv12 is not null)
            throw new NotSupportedException("Hardware-backed frames must be converted to CPU memory before encoding.");
        frame.ValidateCpuGeometry();

        var wr = av_frame_make_writable(_frame);
        FFmpegException.ThrowIfError(wr, nameof(av_frame_make_writable));

        FillFrame(frame);
        _frame->pts = ptsIn90kHz;

        var ret = avcodec_send_frame(_codec, _frame);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPackets(onPacket);
    }

    /// <summary>Flush the encoder at end of session; drains the delayed packets.</summary>
    internal void Flush(Action<IntPtr> onPacket)
    {
        if (_disposed || _codec is null)
            return;
        var ret = avcodec_send_frame(_codec, null);
        if (ret < 0 && ret != AVERROR_EOF)
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPackets(onPacket);
    }

    private void FillFrame(VideoFrame src)
    {
        // sws handles both the pixel-format conversion and the resolution scale in one pass, straight
        // into the encoder frame's plane buffers - no intermediate VideoFrame.
        var srcAv = FfmpegVideoPixelMaps.ToAvPixelFormat(src.Format.PixelFormat)
                    ?? throw new NotSupportedException($"source pixel format {src.Format.PixelFormat} has no FFmpeg mapping.");
        var dstAv = FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel)!.Value;

        _sws = sws_getCachedContext(_sws,
            src.Format.Width, src.Format.Height, srcAv,
            _encodeWidth, _encodeHeight, dstAv,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws is null)
            throw new FFmpegException(0, "sws_getCachedContext returned NULL");

        var planeCount = Math.Min(src.Planes.Length, 8);
        var pins = new System.Buffers.MemoryHandle[planeCount];
        Array.Clear(_srcLines);
        Array.Clear(_srcStride);
        Array.Clear(_dstLines);
        Array.Clear(_dstStride);
        try
        {
            for (var i = 0; i < planeCount; i++)
            {
                pins[i] = src.Planes[i].Pin();
                _srcLines[i] = (byte*)pins[i].Pointer;
                _srcStride[i] = src.Strides[i];
            }

            for (uint i = 0; i < 4; i++)
            {
                _dstLines[i] = _frame->data[i];
                _dstStride[i] = _frame->linesize[i];
            }

            var ret = sws_scale(_sws, _srcLines, _srcStride, 0, src.Format.Height, _dstLines, _dstStride);
            if (ret <= 0)
                FFmpegException.ThrowIfError(ret < 0 ? ret : -1, nameof(sws_scale));
        }
        finally
        {
            for (var i = 0; i < planeCount; i++)
                pins[i].Dispose();
        }
    }

    private void DrainPackets(Action<IntPtr> onPacket)
    {
        while (true)
        {
            var ret = avcodec_receive_packet(_codec, _packet);
            if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
                break;
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_packet));
            av_packet_rescale_ts(_packet, _codec->time_base, VideoTimeBase);
            try
            {
                onPacket((IntPtr)_packet);
            }
            finally
            {
                av_packet_unref(_packet);
            }
        }
    }

    private void OpenCodec()
    {
        var codec = FfmpegEncodeMaps.FindVideoEncoder(_options.Codec);
        if (codec is null)
            throw new InvalidOperationException($"no FFmpeg encoder for {_options.Codec}");

        _codec = avcodec_alloc_context3(codec);
        if (_codec is null)
            throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        var avPix = FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel)!.Value;
        _codec->width = _encodeWidth;
        _codec->height = _encodeHeight;
        _codec->pix_fmt = avPix;
        _codec->time_base = VideoTimeBase;
        _codec->framerate = _options.Fps > 0
            ? new AVRational { num = _options.Fps, den = 1 }
            : FfmpegEncodeMaps.ToAvRational(_sourceFormat.FrameRate);
        if (_options.BitrateBps > 0)
            _codec->bit_rate = _options.BitrateBps;
        if (_options.GopSize > 0)
            _codec->gop_size = _options.GopSize;

        // CRF (quality target): x264/x265/AV1/VP9 accept a "crf" priv option. On encoders that don't
        // (ProRes/DNxHR/FFV1) av_opt_set silently no-ops - those are bitrate/profile driven.
        if (_options.Crf is { } crf)
            av_opt_set(_codec->priv_data, "crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
        // Named speed presets are an x264/x265 concept ("veryfast" …). AV1/VP9 use numeric presets and
        // reject the names, so only pass the string to the codecs that understand it.
        if (_options.Preset is { Length: > 0 } preset
            && _options.Codec is EncodeVideoCodec.H264 or EncodeVideoCodec.Hevc)
        {
            av_opt_set(_codec->priv_data, "preset", preset, 0);
        }

        // Encoder-specific profile/private options (ProRes 4444 alpha, DNxHR HR profile, …).
        foreach (var (key, value) in FfmpegEncodeMaps.VideoEncoderPrivateOptions(_options.Codec, _encodePixel))
            av_opt_set(_codec->priv_data, key, value, 0);

        // GLOBAL_HEADER unconditionally: the session's packets fan out to N muxers and mp4/mov/flv
        // demand out-of-band extradata. Muxers that don't need it (mpegts) ignore it.
        _codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;

        var ret = avcodec_open2(_codec, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _codecParameters = avcodec_parameters_alloc();
        if (_codecParameters is null)
            throw new OutOfMemoryException("avcodec_parameters_alloc returned NULL");
        ret = avcodec_parameters_from_context(_codecParameters, _codec);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_from_context));

        _frame = av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("av_frame_alloc returned NULL");
        _frame->format = (int)avPix;
        _frame->width = _encodeWidth;
        _frame->height = _encodeHeight;
        // Allocate the plane buffers ONCE; Encode() makes them writable and sws_scales into them.
        ret = av_frame_get_buffer(_frame, 32);
        FFmpegException.ThrowIfError(ret, nameof(av_frame_get_buffer));

        _packet = av_packet_alloc();
        if (_packet is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_codec is not null)
        {
            var c = _codec;
            avcodec_free_context(&c);
            _codec = null;
        }

        if (_codecParameters is not null)
        {
            var p = _codecParameters;
            avcodec_parameters_free(&p);
            _codecParameters = null;
        }

        if (_frame is not null)
        {
            var f = _frame;
            av_frame_free(&f);
            _frame = null;
        }

        if (_packet is not null)
        {
            var p = _packet;
            av_packet_free(&p);
            _packet = null;
        }

        if (_sws is not null)
        {
            sws_freeContext(_sws);
            _sws = null;
        }
    }
}
