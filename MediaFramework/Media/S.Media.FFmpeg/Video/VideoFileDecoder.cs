using System.Buffers;
using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video.Internal;

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
/// Hardware decode without Linux DRM dma-bufs still lists <see cref="PixelFormat.Nv12"/> in
/// <see cref="NativePixelFormats"/> while the default output stays <see cref="PixelFormat.Bgra32"/> CPU conversion —
/// callers can negotiate NV12 zero-copy decode when downstream accepts planar frames.
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
    private VideoFrame? _primedAfterSeek;
    private VideoHardwareDecodeContext? _hwAccel;
    private bool _drmGpuNv12Path;

    /// <remarks>Caches FFmpeg swscale pointer/strides — do not reorder across concurrent calls (<see cref="TryReadNextFrame"/> is single-threaded).</remarks>
    private readonly byte*[] _swScaleSrcLines = new byte*[8];

    private readonly int[] _swScaleSrcStride = new int[8];
    private readonly byte*[] _swScaleDstLines = new byte*[8];

    private readonly int[] _swScaleDstStride = new int[8];

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

    public static VideoFileDecoder Open(string path) => Open(path, options: null);

    public static VideoFileDecoder Open(string path, VideoDecoderOpenOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("video file not found", path);

        FFmpegRuntime.EnsureInitialized();

        var decoder = new VideoFileDecoder();
        try
        {
            decoder.OpenInternal(path, options);
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

        if (_primedAfterSeek is { } primed)
        {
            frame = primed;
            _primedAfterSeek = null;
            return true;
        }

        while (true)
        {
            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0)
            {
                var workFrame = ResolveWorkFrame();
                var trc = _frame->color_trc;
                SyncCodecPixelFormatIfNeeded(workFrame);
                frame = BuildFrame(workFrame, trc);
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

        if (_drmGpuNv12Path)
        {
            if (format != PixelFormat.Nv12)
                throw new NotSupportedException(
                    "DRM PRIME zero-copy decoding only supports PixelFormat.Nv12 output matching the GPU-exported NV12 dma-bufs.");
            if (_outPixelFormat == format)
                return;
            _passThrough = true;
            ReleaseSws();
            _outPixelFormat = format;
            Format = Format with { PixelFormat = format };
            return;
        }

        if (format == _outPixelFormat) return;

        if (_hwAccel != null && _srcPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            _outPixelFormat = format;
            Format = Format with { PixelFormat = format };
            return;
        }

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
        _primedAfterSeek = null;
        Position = position;
        ConsumeDecoderUntilPts(position);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _primedAfterSeek?.Dispose();
        _primedAfterSeek = null;

        if (_packet != null)    { var p = _packet;     av_packet_free(&p);          _packet = null; }
        if (_frame != null)     { var f = _frame;      av_frame_free(&f);           _frame = null; }
        ReleaseSws();
        if (_codecCtx != null)
        {
            _hwAccel?.DetachFromCodec(_codecCtx);
            var c = _codecCtx;
            avcodec_free_context(&c);
            _codecCtx = null;
        }

        _hwAccel?.Dispose();
        _hwAccel = null;

        if (_formatCtx != null) { var f = _formatCtx;  avformat_close_input(&f);    _formatCtx = null; }
    }

    // --- internals ---------------------------------------------------------

    private void OpenInternal(string path, VideoDecoderOpenOptions? options)
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

        VideoHardwareDecodeContext? hw = null;
        if (options?.TryHardwareAcceleration == true)
            hw = VideoHardwareDecodeContext.TryCreate(codec, _codecCtx, options.PreferredDeviceTypes,
                options.RetainDmabufForGl);

        AVDictionary* openOpts = null;
        if (hw?.OutputsDrmPrimeGpuFrame == true)
            av_dict_set(&openOpts, "hwaccel_output_format", "drm_prime", 0);

        ret = avcodec_open2(_codecCtx, codec, &openOpts);
        if (openOpts != null)
            av_dict_free(&openOpts);

        if (ret < 0 && hw != null)
        {
            hw.DetachFromCodec(_codecCtx);
            hw.Dispose();
            hw = null;
            ret = avcodec_open2(_codecCtx, codec, null);
        }
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _hwAccel = hw;
        _drmGpuNv12Path = hw?.OutputsDrmPrimeGpuFrame == true;

        if (hw != null)
        {
            if (_drmGpuNv12Path)
            {
                _srcPixelFormat = AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
                _nativePixelFormat = PixelFormat.Nv12;
                _passThrough = true;
                _outPixelFormat = PixelFormat.Nv12;
                _nativePixelFormats = [PixelFormat.Nv12];
            }
            else
            {
                // Post-av_hwframe_transfer_data layouts are codec-dependent; NV12 covers VAAPI/D3D11VA-style paths for negotiation.
                _srcPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
                _nativePixelFormat = PixelFormat.Nv12;
                _passThrough = false;
                _outPixelFormat = PixelFormat.Bgra32;
                _nativePixelFormats = [PixelFormat.Nv12];
            }
        }
        else
        {
            _srcPixelFormat = _codecCtx->pix_fmt;
            var nativeSoft = MapNativePixelFormat(_srcPixelFormat);
            _nativePixelFormat = nativeSoft != PixelFormat.Unknown ? nativeSoft : PixelFormat.Bgra32;
            _passThrough = nativeSoft != PixelFormat.Unknown;
            _outPixelFormat = _nativePixelFormat;
            _nativePixelFormats = nativeSoft != PixelFormat.Unknown ? [nativeSoft] : [];
        }

        var fps = stream->avg_frame_rate;
        if (fps.num <= 0 || fps.den <= 0) fps = stream->r_frame_rate;
        var frameRate = new Rational(fps.num, Math.Max(fps.den, 1));

        Format = new VideoFormat(_codecCtx->width, _codecCtx->height, _outPixelFormat, frameRate);
        CodecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";

        if (stream->duration > 0)
            Duration = PtsToTimeSpan(stream->duration);
        else if (_formatCtx->duration > 0)
            Duration = TimeSpan.FromMicroseconds(_formatCtx->duration);

        if (hw == null && !_passThrough)
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

    private AVFrame* ResolveWorkFrame()
    {
        var fmt = (AVPixelFormat)_frame->format;

        if (_drmGpuNv12Path && fmt == AVPixelFormat.AV_PIX_FMT_DRM_PRIME)
            return _frame;

        if (_hwAccel != null && fmt == _hwAccel.HwAccelPixFmt)
            return _hwAccel.TransferToScratch(_frame);
        return _frame;
    }

    private VideoFrame BuildFrame(AVFrame* work, AVColorTransferCharacteristic trc)
    {
        var pts = ResolvePts(work);
        Position = pts;
        _framesEmitted++;

        if (_drmGpuNv12Path)
            return BuildNv12DrmDmabufGpuFrame(work, pts, trc);

        return _passThrough
            ? BuildPassThroughFrame(work, pts, trc)
            : BuildConvertedFrame(work, pts, trc);
    }

    private VideoFrame BuildNv12DrmDmabufGpuFrame(AVFrame* drmFrame, TimeSpan pts, AVColorTransferCharacteristic trc)
    {
        var backing = DrmPrimeNv12BackingFactory.TryCreateBacking(drmFrame);
        if (backing == null)
            throw new InvalidOperationException(
                "DRM PRIME frame could not be mapped to NV12 dma-bufs. Disable decoder option RetainDmabufForGl or try a CPU upload path.");

        return VideoFrame.CreateNv12Dmabuf(pts, Format, backing, MapTransferHint(trc));
    }

    private VideoFrame BuildPassThroughFrame(AVFrame* work, TimeSpan pts, AVColorTransferCharacteristic trc)
    {
        // Clone the ref so the underlying buffer survives the next decode call.
        var clone = av_frame_alloc();
        if (clone == null) throw new OutOfMemoryException("av_frame_alloc (clone) returned NULL");
        var ret = av_frame_ref(clone, work);
        if (ret < 0)
        {
            var c = clone;
            av_frame_free(&c);
            FFmpegException.ThrowIfError(ret, nameof(av_frame_ref));
        }

        var planeCount = PixelFormatInfo.PlaneCount(_outPixelFormat);
        var planes = new ReadOnlyMemory<byte>[planeCount];
        var strides = new int[planeCount];
        var hint = MapTransferHint(trc);
        for (var i = 0; i < planeCount; i++)
        {
            var rawStride = clone->linesize[(uint)i];
            var absStride = Math.Abs(rawStride);
            var planeH = PixelFormatInfo.PlaneHeight(_outPixelFormat, Format.Height, i);
            var ptr = clone->data[(uint)i];
            if (rawStride < 0)
                ptr += rawStride * (planeH - 1);
            planes[i] = new UnmanagedMemoryManager<byte>(ptr, absStride * planeH).Memory;
            strides[i] = absStride;
        }

        var clonePtr = (nint)clone;
        return new VideoFrame(pts, Format, planes, strides, hint, release: () => FreeAVFrame(clonePtr));
    }

    private VideoFrame BuildConvertedFrame(AVFrame* work, TimeSpan pts, AVColorTransferCharacteristic trc)
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
        var contiguous = stride * height;
        var rented = ArrayPool<byte>.Shared.Rent(contiguous);
        var dstMem = rented.AsMemory(0, contiguous);

        _swScaleSrcLines[0] = work->data[0];
        _swScaleSrcLines[1] = work->data[1];
        _swScaleSrcLines[2] = work->data[2];
        _swScaleSrcLines[3] = work->data[3];
        _swScaleSrcLines[4] = work->data[4];
        _swScaleSrcLines[5] = work->data[5];
        _swScaleSrcLines[6] = work->data[6];
        _swScaleSrcLines[7] = work->data[7];
        _swScaleSrcStride[0] = work->linesize[0];
        _swScaleSrcStride[1] = work->linesize[1];
        _swScaleSrcStride[2] = work->linesize[2];
        _swScaleSrcStride[3] = work->linesize[3];
        _swScaleSrcStride[4] = work->linesize[4];
        _swScaleSrcStride[5] = work->linesize[5];
        _swScaleSrcStride[6] = work->linesize[6];
        _swScaleSrcStride[7] = work->linesize[7];

        fixed (byte* dstPtr = dstMem.Span)
        {
            _swScaleDstLines[0] = dstPtr;
            _swScaleDstLines[1] = null;
            _swScaleDstLines[2] = null;
            _swScaleDstLines[3] = null;
            _swScaleDstLines[4] = null;
            _swScaleDstLines[5] = null;
            _swScaleDstLines[6] = null;
            _swScaleDstLines[7] = null;

            Array.Clear(_swScaleDstStride);
            _swScaleDstStride[0] = stride;

            var ret = sws_scale(_swsCtx, _swScaleSrcLines, _swScaleSrcStride, 0, height, _swScaleDstLines,
                _swScaleDstStride);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(sws_scale));
        }

        var hint = MapTransferHint(trc);
        byte[] pooled = rented;
        return new VideoFrame(pts, Format, dstMem, stride, hint,
            release: () => ArrayPool<byte>.Shared.Return(pooled));
    }

    private void ConsumeDecoderUntilPts(TimeSpan targetPresentationTime)
    {
        while (true)
        {
            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == AVERROR_EOF)
            {
                _eofReached = true;
                return;
            }
            if (ret == AVERROR(EAGAIN))
            {
                FeedDecoder();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));

            var workFrame = ResolveWorkFrame();
            var trc = _frame->color_trc;
            SyncCodecPixelFormatIfNeeded(workFrame);

            var pts = ResolvePts(workFrame);
            if (pts >= targetPresentationTime)
            {
                _primedAfterSeek = BuildFrame(workFrame, trc);
                av_frame_unref(_frame);
                return;
            }

            av_frame_unref(_frame);
        }
    }

    /// <summary>Rebuild native / scaler state when libav reports a different software pixel layout mid-stream.</summary>
    private void SyncCodecPixelFormatIfNeeded(AVFrame* workFrame)
    {
        if (_drmGpuNv12Path)
            return;

        var effective = (AVPixelFormat)workFrame->format;
        if (effective == _srcPixelFormat)
            return;

        _srcPixelFormat = effective;
        var mapped = MapNativePixelFormat(_srcPixelFormat);
        _nativePixelFormat = mapped != PixelFormat.Unknown ? mapped : PixelFormat.Bgra32;
        _nativePixelFormats = mapped != PixelFormat.Unknown ? [mapped] : [];

        var desiredOutput = _outPixelFormat;
        _outPixelFormat = PixelFormat.Unknown;
        SelectOutputFormat(desiredOutput);
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

    private TimeSpan ResolvePts(AVFrame* frame)
    {
        var pts = frame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE) pts = frame->pts;
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
        AVPixelFormat.AV_PIX_FMT_DRM_PRIME      => PixelFormat.Nv12,
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
        AVPixelFormat.AV_PIX_FMT_YUV422P     => PixelFormat.Yuv422P,
        AVPixelFormat.AV_PIX_FMT_YUV444P     => PixelFormat.Yuv444P,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => PixelFormat.Yuv420P10Le,
        AVPixelFormat.AV_PIX_FMT_YUV420P12LE => PixelFormat.Yuv420P12Le,
        AVPixelFormat.AV_PIX_FMT_YUV444P10LE => PixelFormat.Yuv444P10Le,
        AVPixelFormat.AV_PIX_FMT_GRAY8       => PixelFormat.Gray8,
        AVPixelFormat.AV_PIX_FMT_GRAY16LE    => PixelFormat.Gray16,
        AVPixelFormat.AV_PIX_FMT_YUVA420P    => PixelFormat.Yuva420p,
        AVPixelFormat.AV_PIX_FMT_ARGB        => PixelFormat.Argb32,
        AVPixelFormat.AV_PIX_FMT_ABGR       => PixelFormat.Abgr32,
        AVPixelFormat.AV_PIX_FMT_P010LE      => PixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_P016LE      => PixelFormat.P016,
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
        // FFmpeg distinguishes I420 (YUV420P) from YV12 (YVU420P), but FFmpeg.AutoGen 8 omits AV_PIX_FMT_YVU420P.
        // Keep the historical I420 mapping so callers can still request sws output; note plane order is I420, not YV12.
        PixelFormat.Yv12        => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Yuv422P     => AVPixelFormat.AV_PIX_FMT_YUV422P,
        PixelFormat.Yuv444P     => AVPixelFormat.AV_PIX_FMT_YUV444P,
        PixelFormat.P010        => AVPixelFormat.AV_PIX_FMT_P010LE,
        PixelFormat.P016        => AVPixelFormat.AV_PIX_FMT_P016LE,
        PixelFormat.Yuv422P10Le => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        PixelFormat.Yuv420P10Le => AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
        PixelFormat.Yuv420P12Le => AVPixelFormat.AV_PIX_FMT_YUV420P12LE,
        PixelFormat.Yuv444P10Le => AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
        PixelFormat.Gray8       => AVPixelFormat.AV_PIX_FMT_GRAY8,
        PixelFormat.Gray16      => AVPixelFormat.AV_PIX_FMT_GRAY16LE,
        PixelFormat.Yuva420p    => AVPixelFormat.AV_PIX_FMT_YUVA420P,
        PixelFormat.Argb32      => AVPixelFormat.AV_PIX_FMT_ARGB,
        PixelFormat.Abgr32      => AVPixelFormat.AV_PIX_FMT_ABGR,
        _                       => null,
    };

    private static int BytesPerPackedPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Argb32 or PixelFormat.Abgr32 => 4,
        PixelFormat.Bgr24  or PixelFormat.Rgb24  => 3,
        PixelFormat.Uyvy   or PixelFormat.Yuyv   => 2,
        _ => 0,
    };

    /// <summary>Maps libav <see cref="AVColorTransferCharacteristic"/> to a Core hint consumed by HDR-aware sinks.</summary>
    internal static VideoTransferHint MapTransferHint(AVColorTransferCharacteristic trc) => trc switch
    {
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => VideoTransferHint.FromPq,
        AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => VideoTransferHint.FromHlg,
        AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => VideoTransferHint.FromSrgb,
        AVColorTransferCharacteristic.AVCOL_TRC_BT709
            or AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M => VideoTransferHint.Sdr,
        AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED => VideoTransferHint.Unspecified,
        _ => VideoTransferHint.Unspecified,
    };
}
