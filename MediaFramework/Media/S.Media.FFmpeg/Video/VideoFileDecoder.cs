using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Pull-based decoder for the best video stream in a container. Implements
/// <see cref="IVideoSource"/>: <see cref="NativePixelFormats"/> reports the
/// codec's native layout (zero-copy pass-through) and
/// <see cref="SelectOutputFormat"/> swaps in an internal <c>sws_scale</c>
/// context if a different format is requested.
/// </summary>
/// <remarks>
/// Not thread-safe. <see cref="VideoFrame"/>s returned by <see cref="TryReadNextFrame"/>
/// must be <see cref="VideoFrame.Dispose"/>d — the release callback either
/// unrefs a cloned <c>AVFrame</c> (pass-through) or returns nothing
/// (conversion path owns a fresh byte array).
/// </remarks>
public sealed unsafe class VideoFileDecoder : IVideoSource, IDisposable
{
    private AVFormatContext* _formatCtx;
    private AVCodecContext* _codecCtx;
    private SwsContext* _swsCtx;
    private AVPacket* _packet;
    private AVFrame* _frame;

    private int _videoStreamIndex = -1;
    private AVRational _videoTimeBase;
    private AVPixelFormat _srcPixelFormat;
    private PixelFormat _nativePixelFormat;
    private PixelFormat _outPixelFormat;
    private PixelFormat[] _nativePixelFormats = [];
    private bool _passThrough;
    private long _framesEmitted;
    private bool _eofReached;
    private bool _disposed;
    private bool _drainPacketSent;

    public VideoFormat Format { get; private set; }
    public string CodecName { get; private set; } = "";
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    public bool IsAtEnd => _eofReached;

    /// <summary>Implements <see cref="IVideoSource.IsExhausted"/>.</summary>
    public bool IsExhausted => _eofReached;

    /// <summary>Pixel formats this decoder can deliver without a CPU conversion (just the codec's native layout).</summary>
    public IReadOnlyList<PixelFormat> NativePixelFormats => _nativePixelFormats;

    private VideoFileDecoder() { }

    public static VideoFileDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("video file not found", path);

        FFmpegRuntime.EnsureInitialized();

        var decoder = new VideoFileDecoder();
        try
        {
            decoder.OpenInternal(path);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0)
            {
                frame = BuildFrame();
                av_frame_unref(_frame);
                return true;
            }
            if (ret == AVERROR_EOF)
            {
                _eofReached = true;
                frame = null!;
                return false;
            }
            if (ret == AVERROR(EAGAIN))
            {
                FeedDecoder();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
        }
    }

    /// <summary>
    /// Reconfigures the decoder to emit frames in <paramref name="format"/>.
    /// If <paramref name="format"/> equals <see cref="NativePixelFormats"/>[0]
    /// the existing sws context (if any) is released and frames pass through
    /// zero-copy; otherwise a fresh sws context is built to convert into the
    /// requested format. Safe to call between reads.
    /// </summary>
    public void SelectOutputFormat(PixelFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format == PixelFormat.Unknown)
            throw new ArgumentException("cannot select Unknown pixel format", nameof(format));
        if (format == _outPixelFormat) return;

        if (format == _nativePixelFormat)
        {
            ReleaseSws();
            _passThrough = true;
        }
        else
        {
            var avTarget = ToAVPixelFormat(format)
                ?? throw new NotSupportedException($"pixel format {format} has no FFmpeg mapping");
            ReleaseSws();
            _swsCtx = sws_getCachedContext(null,
                _codecCtx->width, _codecCtx->height, _srcPixelFormat,
                _codecCtx->width, _codecCtx->height, avTarget,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, $"sws_getCachedContext for {format} returned NULL");
            _passThrough = false;
        }

        _outPixelFormat = format;
        Format = Format with { PixelFormat = format };
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));

        var ts = (long)(position.TotalSeconds * _videoTimeBase.den / _videoTimeBase.num);
        var ret = av_seek_frame(_formatCtx, _videoStreamIndex, ts, AVSEEK_FLAG_BACKWARD);
        FFmpegException.ThrowIfError(ret, nameof(av_seek_frame));

        avcodec_flush_buffers(_codecCtx);
        _eofReached = false;
        _drainPacketSent = false;
        Position = position;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_packet != null)    { var p = _packet;     av_packet_free(&p);          _packet = null; }
        if (_frame != null)     { var f = _frame;      av_frame_free(&f);           _frame = null; }
        ReleaseSws();
        if (_codecCtx != null)  { var c = _codecCtx;   avcodec_free_context(&c);    _codecCtx = null; }
        if (_formatCtx != null) { var f = _formatCtx;  avformat_close_input(&f);    _formatCtx = null; }
    }

    // --- internals ---------------------------------------------------------

    private void OpenInternal(string path)
    {
        AVFormatContext* fmt = null;
        var ret = avformat_open_input(&fmt, path, null, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
        _formatCtx = fmt;

        ret = avformat_find_stream_info(_formatCtx, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));

        AVCodec* codec = null;
        _videoStreamIndex = av_find_best_stream(_formatCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (_videoStreamIndex < 0 || codec == null)
            throw new FFmpegException(_videoStreamIndex, "no decodable video stream found");

        var stream = _formatCtx->streams[_videoStreamIndex];
        _videoTimeBase = stream->time_base;

        _codecCtx = avcodec_alloc_context3(codec);
        if (_codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        ret = avcodec_parameters_to_context(_codecCtx, stream->codecpar);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));

        ret = avcodec_open2(_codecCtx, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _srcPixelFormat = _codecCtx->pix_fmt;
        var native = MapNativePixelFormat(_srcPixelFormat);
        _nativePixelFormat = native != PixelFormat.Unknown ? native : PixelFormat.Bgra32;
        _passThrough = native != PixelFormat.Unknown;
        _outPixelFormat = _nativePixelFormat;
        _nativePixelFormats = native != PixelFormat.Unknown ? [native] : [];

        var fps = stream->avg_frame_rate;
        if (fps.num <= 0 || fps.den <= 0) fps = stream->r_frame_rate;
        var frameRate = new Rational(fps.num, Math.Max(fps.den, 1));

        Format = new VideoFormat(_codecCtx->width, _codecCtx->height, _outPixelFormat, frameRate);
        CodecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";

        if (stream->duration > 0)
            Duration = PtsToTimeSpan(stream->duration);
        else if (_formatCtx->duration > 0)
            Duration = TimeSpan.FromMicroseconds(_formatCtx->duration);

        if (!_passThrough)
        {
            // Codec's native format isn't in our enum — fall back to BGRA32.
            _swsCtx = sws_getCachedContext(null,
                _codecCtx->width, _codecCtx->height, _srcPixelFormat,
                _codecCtx->width, _codecCtx->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, "sws_getCachedContext returned NULL");
        }

        _packet = av_packet_alloc();
        if (_packet == null) throw new OutOfMemoryException("av_packet_alloc returned NULL");
        _frame = av_frame_alloc();
        if (_frame == null) throw new OutOfMemoryException("av_frame_alloc returned NULL");
    }

    private void FeedDecoder()
    {
        while (true)
        {
            av_packet_unref(_packet);
            var ret = av_read_frame(_formatCtx, _packet);
            if (ret == AVERROR_EOF)
            {
                if (_drainPacketSent) return;
                ret = avcodec_send_packet(_codecCtx, null);
                if (ret == 0 || ret == AVERROR(EAGAIN))
                {
                    _drainPacketSent = true;
                    return;
                }
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }
            FFmpegException.ThrowIfError(ret, nameof(av_read_frame));

            if (_packet->stream_index != _videoStreamIndex) continue;

            ret = avcodec_send_packet(_codecCtx, _packet);
            if (ret == 0)
            {
                _drainPacketSent = false;
                return;
            }
            if (ret == AVERROR(EAGAIN)) return;
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
        }
    }

    private VideoFrame BuildFrame()
    {
        var pts = ResolvePts();
        Position = pts;
        _framesEmitted++;

        return _passThrough
            ? BuildPassThroughFrame(pts)
            : BuildConvertedFrame(pts);
    }

    private VideoFrame BuildPassThroughFrame(TimeSpan pts)
    {
        // Clone the ref so the underlying buffer survives the next decode call.
        var clone = av_frame_alloc();
        if (clone == null) throw new OutOfMemoryException("av_frame_alloc (clone) returned NULL");
        var ret = av_frame_ref(clone, _frame);
        if (ret < 0)
        {
            var c = clone;
            av_frame_free(&c);
            FFmpegException.ThrowIfError(ret, nameof(av_frame_ref));
        }

        var planeCount = PixelFormatInfo.PlaneCount(_outPixelFormat);
        var planes = new ReadOnlyMemory<byte>[planeCount];
        var strides = new int[planeCount];
        for (var i = 0; i < planeCount; i++)
        {
            var stride = clone->linesize[(uint)i];
            var height = PixelFormatInfo.PlaneHeight(_outPixelFormat, Format.Height, i);
            var ptr = clone->data[(uint)i];
            planes[i] = new UnmanagedMemoryManager<byte>(ptr, stride * height).Memory;
            strides[i] = stride;
        }

        var clonePtr = (nint)clone;
        return new VideoFrame(pts, Format, planes, strides, release: () => FreeAVFrame(clonePtr));
    }

    private VideoFrame BuildConvertedFrame(TimeSpan pts)
    {
        // Single-plane conversion (BGRA32 / RGBA32 / BGR24 / RGB24). The
        // multi-plane targets (I420, NV12, …) are reachable via SelectOutputFormat
        // but if you're paying for sws_scale you usually want a packed RGB
        // layout for a CPU sink. Multi-plane conversion targets can be added
        // later as PlaneCount(_outPixelFormat) > 1 cases.
        var width = Format.Width;
        var height = Format.Height;
        var bytesPerPixel = BytesPerPackedPixel(_outPixelFormat);
        if (bytesPerPixel == 0)
            throw new NotSupportedException(
                $"sws conversion to {_outPixelFormat} (multi-plane / non-packed) is not implemented yet — pick a packed RGB target or extend BuildConvertedFrame");

        var stride = width * bytesPerPixel;
        var bytes = new byte[stride * height];

        // FFmpeg.AutoGen's sws_scale wrapper insists on managed byte*[] / int[]
        // arrays. Allocate inline (8 elements each) — small enough to ignore.
        var srcData = new byte*[]
        {
            _frame->data[0u], _frame->data[1u], _frame->data[2u], _frame->data[3u],
            _frame->data[4u], _frame->data[5u], _frame->data[6u], _frame->data[7u],
        };
        var srcStride = new int[]
        {
            _frame->linesize[0u], _frame->linesize[1u], _frame->linesize[2u], _frame->linesize[3u],
            _frame->linesize[4u], _frame->linesize[5u], _frame->linesize[6u], _frame->linesize[7u],
        };

        fixed (byte* dstPtr = bytes)
        {
            var dstData = new byte*[] { dstPtr, null, null, null, null, null, null, null };
            var dstStride = new int[] { stride, 0, 0, 0, 0, 0, 0, 0 };

            var ret = sws_scale(_swsCtx, srcData, srcStride, 0, height, dstData, dstStride);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(sws_scale));
        }

        return new VideoFrame(pts, Format, bytes, stride);
    }

    private void ReleaseSws()
    {
        if (_swsCtx == null) return;
        sws_freeContext(_swsCtx);
        _swsCtx = null;
    }

    private static void FreeAVFrame(nint ptr)
    {
        var f = (AVFrame*)ptr;
        av_frame_free(&f);
    }

    private TimeSpan ResolvePts()
    {
        var pts = _frame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE) pts = _frame->pts;
        if (pts == AV_NOPTS_VALUE)
        {
            var fps = Format.FrameRate.ToDouble();
            return fps > 0
                ? TimeSpan.FromSeconds(_framesEmitted / fps)
                : TimeSpan.Zero;
        }
        return PtsToTimeSpan(pts);
    }

    private TimeSpan PtsToTimeSpan(long pts)
        => TimeSpan.FromSeconds((double)pts * _videoTimeBase.num / _videoTimeBase.den);

    private static PixelFormat MapNativePixelFormat(AVPixelFormat src) => src switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P    => PixelFormat.I420,
        AVPixelFormat.AV_PIX_FMT_YUVJ420P   => PixelFormat.I420,
        AVPixelFormat.AV_PIX_FMT_NV12       => PixelFormat.Nv12,
        AVPixelFormat.AV_PIX_FMT_NV21       => PixelFormat.Nv21,
        AVPixelFormat.AV_PIX_FMT_BGRA       => PixelFormat.Bgra32,
        AVPixelFormat.AV_PIX_FMT_RGBA       => PixelFormat.Rgba32,
        AVPixelFormat.AV_PIX_FMT_BGR24      => PixelFormat.Bgr24,
        AVPixelFormat.AV_PIX_FMT_RGB24      => PixelFormat.Rgb24,
        AVPixelFormat.AV_PIX_FMT_UYVY422    => PixelFormat.Uyvy,
        AVPixelFormat.AV_PIX_FMT_YUYV422    => PixelFormat.Yuyv,
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE => PixelFormat.Yuv422P10Le,
        _                                   => PixelFormat.Unknown,
    };

    private static AVPixelFormat? ToAVPixelFormat(PixelFormat fmt) => fmt switch
    {
        PixelFormat.I420        => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Nv12        => AVPixelFormat.AV_PIX_FMT_NV12,
        PixelFormat.Nv21        => AVPixelFormat.AV_PIX_FMT_NV21,
        PixelFormat.Bgra32      => AVPixelFormat.AV_PIX_FMT_BGRA,
        PixelFormat.Rgba32      => AVPixelFormat.AV_PIX_FMT_RGBA,
        PixelFormat.Bgr24       => AVPixelFormat.AV_PIX_FMT_BGR24,
        PixelFormat.Rgb24       => AVPixelFormat.AV_PIX_FMT_RGB24,
        PixelFormat.Uyvy        => AVPixelFormat.AV_PIX_FMT_UYVY422,
        PixelFormat.Yuyv        => AVPixelFormat.AV_PIX_FMT_YUYV422,
        PixelFormat.Yv12        => AVPixelFormat.AV_PIX_FMT_YUV420P, // FFmpeg has no YV12 enum; same layout, U/V swap.
        PixelFormat.Yuv422P10Le => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        _                       => null,
    };

    private static int BytesPerPackedPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => 4,
        PixelFormat.Bgr24  or PixelFormat.Rgb24  => 3,
        PixelFormat.Uyvy   or PixelFormat.Yuyv   => 2,
        _ => 0,
    };
}
