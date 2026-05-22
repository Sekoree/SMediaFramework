using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg.Encode.Internal;

internal sealed unsafe class FfmpegVideoEncoder : IVideoOutput, IDisposable
{
    private static readonly PixelFormat[] Accepted =
    [
        PixelFormat.Yuv420P10Le,
        PixelFormat.P010,
        PixelFormat.Yuv444P12Le,
        PixelFormat.Yuva444P12Le,
        PixelFormat.Yuv420P12Le,
        PixelFormat.Nv12,
        PixelFormat.I420,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
    ];

    private readonly FfmpegMuxContext _mux;
    private readonly FFmpegVideoFileOutputOptions _options;
    private readonly Lock _gate = new();
    private VideoCpuFrameConverter? _converter;
    private AVCodecContext* _codec;
    private AVStream* _stream;
    private AVFrame* _frame;
    private PixelFormat _encodePixel;
    private VideoFormat _format;
    private long _frameIndex;
    private bool _configured;
    private bool _disposed;

    public FfmpegVideoEncoder(FfmpegMuxContext mux, FFmpegVideoFileOutputOptions options)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        FFmpegRuntime.EnsureInitialized();
    }

    public VideoFormat Format => _format;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => Accepted;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_configured)
                throw new InvalidOperationException("FFmpeg video encoder already configured.");

            _encodePixel = FfmpegEncodeMaps.PickVideoEncodePixel(format.PixelFormat, _options.Codec, _options.EncodePixelFormat);
            if (FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel) is null)
                throw new NotSupportedException($"encode pixel format {_encodePixel} is not supported by this FFmpeg build.");

            _format = format with { PixelFormat = _encodePixel };
            OpenCodecLocked();
            _configured = true;
            _mux.NotifyVideoConfigured();
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
            throw new InvalidOperationException("Configure must be called before Submit.");

        try
        {
            lock (_gate)
            {
                var working = frame;
                var disposeConverted = false;
                if (frame.Format.PixelFormat != _encodePixel)
                {
                    _converter ??= new VideoCpuFrameConverter();
                    _converter.Configure(frame.Format.PixelFormat, _encodePixel, frame.Format.Width, frame.Format.Height);
                    working = _converter.Convert(frame, frame.ColorTransferHint);
                    disposeConverted = true;
                }

                try
                {
                    av_frame_make_writable(_frame);
                    FfmpegAvFrameFill.CopyVideoFrame(working, _frame, _encodePixel);
                    _frame->pts = ResolvePts(frame.PresentationTime);
                    EncodeFrameLocked(_frame);
                }
                finally
                {
                    if (disposeConverted)
                        working.Dispose();
                }
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    private long ResolvePts(TimeSpan presentationTime)
    {
        if (presentationTime > TimeSpan.Zero)
        {
            var srcTb = new AVRational { num = 1, den = 10_000_000 };
            var dstTb = _codec->time_base;
            return av_rescale_q(presentationTime.Ticks, srcTb, dstTb);
        }

        return _frameIndex++;
    }

    private void EncodeFrameLocked(AVFrame* frame)
    {
        var ret = avcodec_send_frame(_codec, frame);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPacketsLocked(sendFlush: false);
    }

    private void DrainPacketsLocked(bool sendFlush)
    {
        if (sendFlush)
        {
            var send = avcodec_send_frame(_codec, null);
            if (send < 0 && send != ffmpeg.AVERROR_EOF)
                FFmpegException.ThrowIfError(send, nameof(avcodec_send_frame));
        }

        var pkt = av_packet_alloc();
        if (pkt is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");
        try
        {
            while (true)
            {
                var ret = avcodec_receive_packet(_codec, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_packet));
                av_packet_rescale_ts(pkt, _codec->time_base, _stream->time_base);
                _mux.WritePacket(pkt, _stream->index);
                av_packet_unref(pkt);
            }
        }
        finally
        {
            av_packet_free(&pkt);
        }
    }

    private void OpenCodecLocked()
    {
        var codecId = FfmpegEncodeMaps.VideoCodecId(_options.Codec);
        AVCodec* codec = null;
        var name = FfmpegEncodeMaps.VideoEncoderName(_options.Codec);
        if (name is not null)
            codec = avcodec_find_encoder_by_name(name);
        if (codec is null)
            codec = avcodec_find_encoder(codecId);
        if (codec is null)
            throw new InvalidOperationException($"no FFmpeg encoder for {_options.Codec}");

        _stream = avformat_new_stream(_mux.FormatContext, null);
        if (_stream is null)
            throw new OutOfMemoryException("avformat_new_stream returned NULL");

        _codec = avcodec_alloc_context3(codec);
        if (_codec is null)
            throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        var avPix = FfmpegVideoPixelMaps.ToAvPixelFormat(_encodePixel)!.Value;
        _codec->width = _format.Width;
        _codec->height = _format.Height;
        _codec->pix_fmt = avPix;
        _codec->time_base = FfmpegEncodeMaps.TimeBaseFromFrameRate(_format.FrameRate);
        _codec->framerate = FfmpegEncodeMaps.ToAvRational(_format.FrameRate);
        if (_options.Bitrate > 0)
            _codec->bit_rate = _options.Bitrate;
        if (_options.GopSize > 0)
            _codec->gop_size = _options.GopSize;

        if ((_mux.FormatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        var ret = avcodec_open2(_codec, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        ret = avcodec_parameters_from_context(_stream->codecpar, _codec);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_from_context));
        _stream->time_base = _codec->time_base;

        _frame = av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("av_frame_alloc returned NULL");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            if (_codec is not null)
            {
                MediaDiagnostics.SwallowDisposeErrors(() => DrainPacketsLocked(sendFlush: true), "FfmpegVideoEncoder.Dispose: flush");
                var c = _codec;
                avcodec_free_context(&c);
                _codec = null;
            }

            if (_frame is not null)
            {
                var f = _frame;
                av_frame_free(&f);
                _frame = null;
            }

            MediaDiagnostics.SwallowDisposeErrors(() => _converter?.Dispose(), "FfmpegVideoEncoder.Dispose: converter");
        }
    }
}
