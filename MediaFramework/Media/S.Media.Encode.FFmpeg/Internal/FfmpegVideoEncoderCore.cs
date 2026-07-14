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
    private readonly bool _enableConstantBitrateFiller;
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
    private readonly AVRational _frameRate;
    private readonly AVRational _codecTimeBase;
    private readonly long _packetDuration90k;
    private bool _disposed;

    internal FfmpegVideoEncoderCore(
        VideoEncodeOptions options,
        VideoFormat sourceFormat,
        bool enableConstantBitrateFiller = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _enableConstantBitrateFiller = enableConstantBitrateFiller;
        FFmpegRuntime.EnsureInitialized();
        _sourceFormat = sourceFormat;
        _frameRate = options.Fps > 0
            ? new AVRational { num = options.Fps, den = 1 }
            : FfmpegEncodeMaps.ToAvRational(sourceFormat.FrameRate);
        if (_frameRate.num <= 0 || _frameRate.den <= 0)
            _frameRate = new AVRational { num = 30, den = 1 };
        // Every encoder needs the actual frame clock (inverse of the configured OR source-following
        // frame rate), not the MPEG-TS 90 kHz packet clock. libx264 writes codec time_base into SPS VUI,
        // while built-in MPEG-4 rejects 1/90000 outright for common source rates. Keep 90 kHz only as
        // the session/sink interchange clock and rescale at the codec boundary.
        _codecTimeBase = new AVRational { num = _frameRate.den, den = _frameRate.num };
        _packetDuration90k = Math.Max(1, 90_000L * _frameRate.den / _frameRate.num);

        _encodePixel = FfmpegEncodeMaps.PickVideoEncodePixel(sourceFormat.PixelFormat, options.Codec, options.EncodePixelFormat);
        if (FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel) is null)
            throw new NotSupportedException($"encode pixel format {_encodePixel} is not supported by this FFmpeg build.");

        (_encodeWidth, _encodeHeight) = ResolveScale(sourceFormat.Width, sourceFormat.Height, options.ScaleWidth, options.ScaleHeight);
        try
        {
            OpenCodec();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal AVCodecParameters* CodecParameters => _codecParameters;

    internal AVRational TimeBase => VideoTimeBase;

    internal AVRational FrameRate => _frameRate;

    internal AVRational CodecTimeBase => _codecTimeBase;

    internal long MinimumBitrate => _codec->rc_min_rate;

    internal long MaximumBitrate => _codec->rc_max_rate;

    internal int VbvBufferSize => _codec->rc_buffer_size;

    internal int MaximumBFrames => _codec->max_b_frames;

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
        _frame->pts = av_rescale_q(ptsIn90kHz, VideoTimeBase, _codec->time_base);

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
            // Several encoders (notably libx264 with B-frames) leave AVPacket.duration unset. Live
            // muxers cannot infer a stable cadence from a never-ending stream as reliably as a file
            // muxer can at trailer time; HLS then warns and some ingest servers report an arbitrary
            // observed FPS. Every video session has a locked cadence, so stamp that duration here.
            if (_packet->duration <= 0)
                _packet->duration = _packetDuration90k;
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
        _codec->time_base = _codecTimeBase;
        _codec->framerate = _frameRate;
        if (_options.BitrateBps > 0)
            _codec->bit_rate = _options.BitrateBps;
        if (_options.BitrateMode == EncodeVideoBitrateMode.Constant && _options.BitrateBps > 0)
        {
            // Equal min/max rates plus VBV make the bitrate a hard network envelope rather than a
            // long-term average. For live transport containers the codec-specific HRD option below
            // also enables filler/strict CBR behaviour; file containers keep the VBV constraint but
            // omit filler bytes that MP4/MOV do not carry reliably.
            _codec->rc_min_rate = _options.BitrateBps;
            _codec->rc_max_rate = _options.BitrateBps;
            var bufferBits = _options.BufferSizeBits > 0
                ? _options.BufferSizeBits
                : _options.BitrateBps; // a one-second VBV when the caller did not choose one
            _codec->rc_buffer_size = checked((int)Math.Min(bufferBits, int.MaxValue));
            _codec->rc_initial_buffer_occupancy = _codec->rc_buffer_size * 3 / 4;
        }
        if (_options.GopSize > 0)
            _codec->gop_size = _options.GopSize;
        if (_options.MaxBFrames is { } maxBFrames)
            _codec->max_b_frames = maxBFrames;

        // CRF (quality target): x264/x265/AV1/VP9 accept a "crf" priv option. On encoders that don't
        // (ProRes/DNxHR/FFV1) validation rejects CRF - those are bitrate/profile driven.
        if (_options.Crf is { } crf)
            SetPrivateOption("crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Named speed presets are an x264/x265 concept ("veryfast" …). AV1/VP9 use numeric presets and
        // reject the names, so only pass the string to the codecs that understand it.
        if (_options.Preset is { Length: > 0 } preset && _options.Codec.SupportsNamedPreset())
        {
            SetPrivateOption("preset", preset);
        }
        if (_options.LowLatencyTune && _options.Codec.SupportsLatencyControls())
        {
            SetPrivateOption("tune", "zerolatency");
        }
        if (_enableConstantBitrateFiller
            && _options.BitrateMode == EncodeVideoBitrateMode.Constant
            && _options.BitrateBps > 0)
        {
            // libx264's CBR HRD emits filler to maintain a constant transport rate. libx265 exposes
            // the equivalent stricter VBV/HRD switches through its parameter dictionary.
            if (_options.Codec == EncodeVideoCodec.H264)
                SetPrivateOption("nal-hrd", "cbr");
            else if (_options.Codec == EncodeVideoCodec.Hevc)
                SetPrivateOption("x265-params", "strict-cbr=1:hrd=1");
        }

        // Encoder-specific profile/private options (ProRes 4444 alpha, DNxHR HR profile, …).
        foreach (var (key, value) in FfmpegEncodeMaps.VideoEncoderPrivateOptions(_options.Codec, _encodePixel))
            SetPrivateOption(key, value);

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

    private void SetPrivateOption(string key, string value)
    {
        var ret = av_opt_set(_codec->priv_data, key, value, 0);
        if (ret < 0)
            throw new FFmpegException(ret, $"av_opt_set({key}={value})");
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
