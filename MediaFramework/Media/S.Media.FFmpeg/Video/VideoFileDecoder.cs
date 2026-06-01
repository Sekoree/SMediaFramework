using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Pull-based decoder for the best video stream in a container. Implements
/// <see cref="IVideoSource"/> and <see cref="ISeekableSource"/> (so <see cref="VideoPlayer.Seek"/> works
/// without a wrapper). <see cref="NativePixelFormats"/> reports the
/// codec's native layout (zero-copy pass-through) and
/// <see cref="SelectOutputFormat"/> swaps in an internal <c>sws_scale</c>
/// context if a different format is requested.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe. <see cref="VideoFrame"/>s returned by <see cref="TryReadNextFrame"/>
/// must be <see cref="VideoFrame.Dispose"/>d — the release callback either
/// unrefs a cloned <c>AVFrame</c> (pass-through) or returns nothing
/// (conversion path owns a fresh byte array).
/// Hardware decode without Linux DRM dma-bufs or Windows D3D11 shared-handle export leaves <see cref="NativePixelFormats"/> empty until one
/// transferred frame is probed at open time, so negotiation does not assume NV12 prematurely.
/// Libav may use internal frame/slice threads on software decode when no hardware device context is active.
/// Pass-through frames borrow pooled plane and stride descriptor arrays from a
/// <see cref="PassThroughDescriptorArena"/> (per-plane-count Treiber free-list of fixed slots, bounded by <see cref="PassThroughDescriptorArena.PoolCap"/>).
/// <c>Array.Clear</c> runs on the release path before returning slots so pooled arrays never retain
/// stale plane views. Optional counters: set environment variable
/// <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c> and read <see cref="PassThroughArenaProfiling"/> (rent/return wall time and
/// <see cref="PassThroughArenaProfiling.TreiberCasRetries"/> — failed Treiber <c>CompareExchange</c> attempts on the free lists).
/// If profiling shows sustained retries, set <c>MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE=1</c> for a per-arena mutex around
/// rent/return/dispose (<see cref="PassThroughArenaSerialization"/>).
/// Further wait-free work is only for <strong>outer</strong> decode/release synchronization if profiling shows sustained retries,
/// wall-time outliers, or contention beyond the Treiber pools (profiling-gated; see environment variables above).
/// </para>
/// <para>
/// <see cref="Dispose"/> wraps managed teardown (<see cref="PassThroughDescriptorArena"/>, hardware acceleration, primed-seek holder)
/// so <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/> and <strong>Release</strong> builds continue best-effort
/// (same policy as <see cref="VideoRouter.Dispose"/>).
/// </remarks>
public sealed unsafe class VideoFileDecoder : IVideoSource, ISeekableSource, IHardwareD3D11GlInteropSource,
    ICooperativeVideoReadInterrupt, IDisposable
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
    private int _readYieldRequested;
    private VideoFrame? _primedAfterSeek;
    private VideoHardwareDecodeContext? _hwAccel;
    private bool _drmGpuNv12Path;
    private bool _d3d11GpuNv12Path;
    private bool _win32Nv12SharedHandleOnly;

    private const int SwsResizeFilter = (int)SwsFlags.SWS_BICUBIC;

    /// <summary>
    /// Pooled arrays for libav pass-through <see cref="VideoFrame"/> metadata; returned when the frame
    /// <c>release</c> callback runs (dispose may occur off-thread vs <see cref="TryReadNextFrame"/>).
    /// </summary>
    private readonly PassThroughDescriptorArena _passThroughArena = new();

    /// <remarks>Caches FFmpeg swscale pointer/strides — do not reorder across concurrent calls (<see cref="TryReadNextFrame"/> is single-threaded).</remarks>
    private readonly byte*[] _swScaleSrcLines = new byte*[8];

    private readonly int[] _swScaleSrcStride = new int[8];
    private readonly byte*[] _swScaleDstLines = new byte*[8];

    private readonly int[] _swScaleDstStride = new int[8];

    public VideoFormat Format { get; private set; }
    public string CodecName { get; private set; } = "";
    /// <summary>Libav <c>AVCodecContext.thread_count</c> after open (frame/slice threads on software decode; often 1 with hardware).</summary>
    public int CodecThreadCount => _disposed || _codecCtx == null ? 0 : _codecCtx->thread_count;
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    public bool IsAtEnd => _eofReached;

    /// <summary>Implements <see cref="IVideoSource.IsExhausted"/>.</summary>
    public bool IsExhausted => _eofReached;

    /// <summary>Pixel formats this decoder can deliver without a CPU conversion (just the codec's native layout).</summary>
    public IReadOnlyList<PixelFormat> NativePixelFormats => _nativePixelFormats;

    /// <summary>
    /// When Windows D3D11VA NV12 shared-handle decode is active, returns libav's <c>ID3D11Device</c> COM pointer
    /// (same device as decoded textures). Pass to <c>SDL3GLVideoOutput</c> so GL does not create a second device.
    /// </summary>
    public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr)
    {
        deviceComPtr = 0;
        if (_disposed || !_d3d11GpuNv12Path || _hwAccel == null)
            return false;
        deviceComPtr = _hwAccel.TryGetD3D11DeviceComPtr();
        return deviceComPtr != 0;
    }

    /// <inheritdoc cref="IHardwareD3D11GlInteropSource.TryGetHardwareD3D11AdapterLuid"/>
    public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
        if (_disposed || _hwAccel == null)
            return false;
        return _hwAccel.TryGetD3D11AdapterLuid(out adapterLuidPacked);
    }

    /// <summary>True when D3D11 NV12 backing omits libav D3D11 COM pointers (shared-handle-only export).</summary>
    public bool Win32Nv12SharedHandleOnlyActive => _win32Nv12SharedHandleOnly;

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
            if (Volatile.Read(ref _readYieldRequested) != 0)
            {
                frame = null!;
                return false;
            }

            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0)
            {
                var workFrame = ResolveWorkFrame();
                var meta = ExtractMetadata(_frame);
                SyncCodecPixelFormatIfNeeded(workFrame);
                frame = BuildFrame(workFrame, meta);
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
            if (format is not (PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.P016))
                throw new NotSupportedException(
                    "DRM PRIME zero-copy decoding only supports PixelFormat.Nv12, P010, or P016 output matching the GPU-exported semi-planar dma-bufs.");
            if (_outPixelFormat == format)
                return;
            _passThrough = true;
            ReleaseSws();
            _outPixelFormat = format;
            Format = Format with { PixelFormat = format };
            return;
        }

        if (_d3d11GpuNv12Path)
        {
            if (format != PixelFormat.Nv12)
                throw new NotSupportedException(
                    "D3D11 shared-handle zero-copy decoding only supports PixelFormat.Nv12 output matching the DXGI NV12 texture export.");
            if (_outPixelFormat == format)
                return;
            _passThrough = true;
            ReleaseSws();
            _outPixelFormat = format;
            Format = Format with { PixelFormat = format };
            return;
        }

        if (format == _outPixelFormat) return;

        if (format == _nativePixelFormat)
        {
            ReleaseSws();
            _passThrough = true;
        }
        else
        {
            var avTarget = FfmpegVideoPixelMaps.ToAvPixelFormat(format)
                ?? throw new NotSupportedException($"pixel format {format} has no FFmpeg mapping");
            ReleaseSws();
            _swsCtx = sws_getCachedContext(null,
                _codecCtx->width, _codecCtx->height, _srcPixelFormat,
                _codecCtx->width, _codecCtx->height, avTarget,
                SwsResizeFilter, null, null, null);
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

        ResetReadYield();

        var ts = (long)(position.TotalSeconds * _videoTimeBase.den / _videoTimeBase.num);
        var ret = av_seek_frame(_formatCtx, _videoStreamIndex, ts, AVSEEK_FLAG_BACKWARD);
        FFmpegException.ThrowIfError(ret, nameof(av_seek_frame));

        avcodec_flush_buffers(_codecCtx);
        _eofReached = false;
        _drainPacketSent = false;
        _primedAfterSeek = null;
        // Re-anchor the no-PTS fallback counter to the seek target so streams without
        // container timestamps resume at ~position instead of a stale future time.
        var fallbackFps = Format.FrameRate.ToDouble();
        _framesEmitted = fallbackFps > 0 ? (long)Math.Round(position.TotalSeconds * fallbackFps) : 0;
        Position = position;
        ConsumeDecoderUntilPts(position);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        MediaDiagnostics.SwallowDisposeErrors(() => _primedAfterSeek?.Dispose(), "VideoFileDecoder.Dispose: _primedAfterSeek");
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

        MediaDiagnostics.SwallowDisposeErrors(() => _hwAccel?.Dispose(), "VideoFileDecoder.Dispose: _hwAccel");
        _hwAccel = null;

        MediaDiagnostics.SwallowDisposeErrors(_passThroughArena.Dispose, "VideoFileDecoder.Dispose: _passThroughArena");

        if (_formatCtx != null) { var f = _formatCtx;  avformat_close_input(&f);    _formatCtx = null; }
    }

    /// <summary>Enables libav frame or slice threading for software video decode (not used with active HW device ctx).</summary>
    internal static void ApplyDecoderThreading(AVCodec* codec, AVCodecContext* ctx, VideoDecoderOpenOptions? options)
    {
        var requested = options?.DecoderThreadCount ?? 0;
        var cores = Math.Max(1, Environment.ProcessorCount);
        var count = requested <= 0 ? Math.Clamp(cores, 1, 16) : Math.Clamp(requested, 1, 16);

        if ((codec->capabilities & AV_CODEC_CAP_FRAME_THREADS) != 0)
        {
            ctx->thread_count = count;
            ctx->thread_type = (int)FF_THREAD_FRAME;
        }
        else if ((codec->capabilities & AV_CODEC_CAP_SLICE_THREADS) != 0)
        {
            ctx->thread_count = count;
            ctx->thread_type = (int)FF_THREAD_SLICE;
        }
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

        var tryHw = options?.TryHardwareAcceleration ?? true;
        var retainDmabuf = options?.RetainDmabufForGl ?? false;
        var retainD3D11 = options?.RetainD3D11SharedHandleForGl ?? false;
        _win32Nv12SharedHandleOnly = retainD3D11 && VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(options);
        var preferredDevices = options?.PreferredDeviceTypes ?? [];

        VideoHardwareDecodeContext? hw = null;
        if (tryHw)
            hw = VideoHardwareDecodeContext.TryCreate(codec, _codecCtx, preferredDevices, retainDmabuf, retainD3D11);

        // Frame/slice threading helps heavy software codecs; skip while a HW device is attached — libav
        // hardware decode paths are not reliably compatible with frame threading.
        if (hw == null)
            ApplyDecoderThreading(codec, _codecCtx, options);

        AVDictionary* openOpts = null;
        if (hw?.OutputsDrmPrimeGpuFrame == true)
            av_dict_set(&openOpts, "hwaccel_output_format", "drm_prime", 0);
        else if (hw?.OutputsD3D11GpuFrame == true)
            av_dict_set(&openOpts, "hwaccel_output_format", "d3d11", 0);

        ret = avcodec_open2(_codecCtx, codec, &openOpts);
        if (openOpts != null)
            av_dict_free(&openOpts);

        if (ret < 0 && hw != null)
        {
            hw.DetachFromCodec(_codecCtx);
            hw.Dispose();
            hw = null;
            ApplyDecoderThreading(codec, _codecCtx, options);
            ret = avcodec_open2(_codecCtx, codec, null);
        }
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _hwAccel = hw;
        _drmGpuNv12Path = hw?.OutputsDrmPrimeGpuFrame == true;
        _d3d11GpuNv12Path = hw?.OutputsD3D11GpuFrame == true;

        if (hw != null)
        {
            if (_drmGpuNv12Path)
            {
                var drmPass = InferDrmPrimeOutputPixelFormat(stream->codecpar);
                _srcPixelFormat = AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
                _nativePixelFormat = drmPass;
                _passThrough = true;
                _outPixelFormat = drmPass;
                _nativePixelFormats = [drmPass];
            }
            else if (_d3d11GpuNv12Path)
            {
                _srcPixelFormat = _hwAccel!.HwAccelPixFmt;
                _nativePixelFormat = PixelFormat.Nv12;
                _passThrough = true;
                _outPixelFormat = PixelFormat.Nv12;
                _nativePixelFormats = [PixelFormat.Nv12];
            }
            else
            {
                // Real layout comes from av_hwframe_transfer_data (first frame). Do not
                // advertise NV12 here — negotiation would pick it before we know the
                // transfer format (e.g. ProRes → YUV422P10LE) and break swscale.
                _srcPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
                _nativePixelFormat = PixelFormat.Unknown;
                _passThrough = false;
                _outPixelFormat = PixelFormat.Bgra32;
                _nativePixelFormats = [];
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
                SwsResizeFilter, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, "sws_getCachedContext returned NULL");
        }

        _packet = av_packet_alloc();
        if (_packet == null) throw new OutOfMemoryException("av_packet_alloc returned NULL");
        _frame = av_frame_alloc();
        if (_frame == null) throw new OutOfMemoryException("av_frame_alloc returned NULL");

        if (_hwAccel != null && !_drmGpuNv12Path && !_d3d11GpuNv12Path)
            PrimeHardwareTransferPixelFormat();
    }

    private void FeedDecoder()
    {
        while (true)
        {
            if (Volatile.Read(ref _readYieldRequested) != 0)
                return;

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

        if (_d3d11GpuNv12Path &&
            fmt is AVPixelFormat.AV_PIX_FMT_D3D11 or AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD)
            return _frame;

        if (_hwAccel != null && fmt == _hwAccel.HwAccelPixFmt)
            return _hwAccel.TransferToScratch(_frame);
        return _frame;
    }

    private VideoFrame BuildFrame(AVFrame* work, VideoFrameMetadata meta)
    {
        var pts = ResolvePts(work);
        Position = pts;
        _framesEmitted++;

        if (_drmGpuNv12Path)
            return BuildNv12DrmDmabufGpuFrame(work, pts, meta);

        if (_d3d11GpuNv12Path)
            return BuildNv12D3D11SharedGpuFrame(work, pts, meta);

        return _passThrough
            ? BuildPassThroughFrame(work, pts, meta)
            : BuildConvertedFrame(work, pts, meta);
    }

    /// <summary>
    /// Reads color-space / color-range / field-order / S12M-timecode plus the existing transfer
    /// hint off <paramref name="source"/>. Called once per decoded frame at the top of the read loop.
    /// </summary>
    internal VideoFrameMetadata ExtractMetadata(AVFrame* source) => new(
        MapTransferHint(source->color_trc),
        MapColorSpace(source->colorspace),
        MapColorRange(source->color_range),
        MapFieldOrder(source),
        ReadS12mTimecode(source, Format.FrameRate));

    private VideoFrame BuildNv12DrmDmabufGpuFrame(AVFrame* drmFrame, TimeSpan pts, VideoFrameMetadata meta) =>
        CreateVideoFrameFromLinuxDrmPrimeFrame(drmFrame, pts, Format, meta);

    internal static unsafe VideoFrame CreateVideoFrameFromLinuxDrmPrimeFrame(
        AVFrame* drmFrame, TimeSpan pts, VideoFormat format, VideoFrameMetadata meta)
    {
        var nv12 = DrmPrimeNv12BackingFactory.TryCreateBacking(drmFrame);
        var p010 = DrmPrimeP010BackingFactory.TryCreateBacking(drmFrame);
        var p016 = DrmPrimeP016BackingFactory.TryCreateBacking(drmFrame);

        if (format.PixelFormat == PixelFormat.P016)
        {
            if (p016 != null)
                return VideoFrame.CreateP016Dmabuf(pts, format, p016, meta);
            if (p010 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is P010 (DRM_FORMAT_P010) but the decoder negotiated P016 output. Re-open with a 12-bit profile or CPU decode.");
            if (nv12 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is NV12 (DRM_FORMAT_NV12) but the decoder negotiated P016 output. Re-open with a 12-bit profile or CPU decode.");
            throw new InvalidOperationException(
                "DRM PRIME frame could not be mapped to P016 dma-bufs (expected DRM_FORMAT_P016 two-plane descriptor). Disable decoder option RetainDmabufForGl or try NV12 / P010 / a CPU upload path.");
        }

        if (format.PixelFormat == PixelFormat.P010)
        {
            if (p010 != null)
                return VideoFrame.CreateP010Dmabuf(pts, format, p010, meta);
            if (p016 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is P016 (DRM_FORMAT_P016) but the decoder negotiated P010 output. Re-open with a 10-bit profile or CPU decode.");
            if (nv12 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is NV12 (DRM_FORMAT_NV12) but the decoder negotiated P010 output. Re-open with a 10-bit profile or CPU decode.");
            throw new InvalidOperationException(
                "DRM PRIME frame could not be mapped to P010 dma-bufs (expected DRM_FORMAT_P010 two-plane descriptor). Disable decoder option RetainDmabufForGl or try NV12 / a CPU upload path.");
        }

        if (format.PixelFormat == PixelFormat.Nv12)
        {
            if (nv12 != null)
                return VideoFrame.CreateNv12Dmabuf(pts, format, nv12, meta);
            if (p016 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is P016 (DRM_FORMAT_P016) but the decoder negotiated NV12 output. Re-open the source as 12-bit or select P016 for outputs.");
            if (p010 != null)
                throw new InvalidOperationException(
                    "DRM PRIME frame is P010 (DRM_FORMAT_P010) but the decoder negotiated NV12 output. Re-open the source as 10-bit or select P010 for outputs.");
            throw new InvalidOperationException(
                "DRM PRIME frame could not be mapped to NV12 dma-bufs. Disable decoder option RetainDmabufForGl or try a CPU upload path.");
        }

        throw new NotSupportedException(
            $"DRM PRIME zero-copy is not wired for negotiated pixel format {format.PixelFormat}.");
    }

    /// <summary>Chooses NV12 vs P010 vs P016 for Linux <c>drm_prime</c> zero-copy before the first decoded frame.</summary>
    internal static unsafe PixelFormat InferDrmPrimeOutputPixelFormat(AVCodecParameters* codecpar)
    {
        if (codecpar == null)
            return PixelFormat.Nv12;

        var mapped = MapNativePixelFormat((AVPixelFormat)codecpar->format);
        if (mapped == PixelFormat.P016)
            return PixelFormat.P016;
        if (mapped == PixelFormat.P010)
            return PixelFormat.P010;
        if (mapped is PixelFormat.Yuv420P10Le or PixelFormat.Yuv422P10Le or PixelFormat.Yuv444P10Le)
            return PixelFormat.P010;
        if (mapped == PixelFormat.Yuv420P12Le)
            return PixelFormat.P016;

        if (codecpar->bits_per_raw_sample == 12)
            return PixelFormat.P016;
        if (codecpar->bits_per_raw_sample == 10)
            return PixelFormat.P010;

        return PixelFormat.Nv12;
    }

    private VideoFrame BuildNv12D3D11SharedGpuFrame(AVFrame* d3dFrame, TimeSpan pts, VideoFrameMetadata meta)
    {
        var backing = D3D11VaNv12BackingFactory.TryCreateBacking(d3dFrame, _win32Nv12SharedHandleOnly);
        if (backing == null)
            throw new InvalidOperationException(
                "D3D11 frame could not be exported to NT shared handles. Disable decoder option RetainD3D11SharedHandleForGl or try a CPU upload path.");

        return VideoFrame.CreateNv12Win32Shared(pts, Format, backing, meta);
    }

    private VideoFrame BuildPassThroughFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta)
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
        if (planeCount < 1 || planeCount > PassThroughDescriptorArena.MaxPlaneCount)
        {
            var c = clone;
            av_frame_free(&c);
            throw new NotSupportedException(
                $"pass-through plane count {planeCount} is outside the pooled range 1..{PassThroughDescriptorArena.MaxPlaneCount}");
        }

        var passHandle = _passThroughArena.Rent(planeCount);
        var planes = passHandle.Planes;
        var strides = passHandle.Strides;
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
        return new VideoFrame(pts, Format, planes, strides,
            release: DisposableRelease.Wrap(() =>
            {
                FreeAVFrame(clonePtr);
                _passThroughArena.Return(in passHandle);
            }),
            metadata: meta);
    }

    private VideoFrame BuildConvertedFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta)
    {
        if (_swsCtx == null)
            throw new InvalidOperationException("swscale is not configured — SelectOutputFormat must run before decoding.");

        var width = Format.Width;
        var height = Format.Height;
        var bytesPerPixel = BytesPerPackedPixel(_outPixelFormat);
        if (bytesPerPixel == 0)
        {
            if (_outPixelFormat is PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.I420)
                return BuildConvertedPlanarFrame(work, pts, meta);
            throw new NotSupportedException(
                $"sws conversion to {_outPixelFormat} is not implemented — use BGRA32, NV12, I420, or a packed RGB layout.");
        }

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

        byte[] pooled = rented;
        return new VideoFrame(pts, Format, dstMem, stride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(pooled)),
            metadata: meta);
    }

    private VideoFrame BuildConvertedPlanarFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta)
    {
        var width = Format.Width;
        var height = Format.Height;
        var n = PixelFormatInfo.PlaneCount(_outPixelFormat);

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

        var strides = new int[n];
        for (var i = 0; i < n; i++)
            strides[i] = PixelFormatInfo.PlaneByteWidth(_outPixelFormat, width, i);

        var buffers = new byte[n][];
        var handles = new List<GCHandle>(n);
        try
        {
            for (var i = 0; i < n; i++)
            {
                var len = PixelFormatInfo.PlanePitchBufferLength(_outPixelFormat, width, height, i, strides[i]);
                buffers[i] = ArrayPool<byte>.Shared.Rent(len);
                handles.Add(GCHandle.Alloc(buffers[i], GCHandleType.Pinned));
            }

            for (var i = 0; i < n; i++)
                _swScaleDstLines[i] = (byte*)handles[i].AddrOfPinnedObject();
            for (var i = n; i < 8; i++)
                _swScaleDstLines[i] = null;

            Array.Clear(_swScaleDstStride);
            for (var i = 0; i < n; i++)
                _swScaleDstStride[i] = strides[i];

            var ret = sws_scale(_swsCtx, _swScaleSrcLines, _swScaleSrcStride, 0, height, _swScaleDstLines,
                _swScaleDstStride);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(sws_scale));
        }
        catch
        {
            foreach (var h in handles)
            {
                if (h.IsAllocated)
                    h.Free();
            }
            foreach (var b in buffers)
            {
                if (b is not null)
                    ArrayPool<byte>.Shared.Return(b);
            }
            throw;
        }

        foreach (var h in handles)
            h.Free();

        var memories = new ReadOnlyMemory<byte>[n];
        for (var i = 0; i < n; i++)
        {
            var len = PixelFormatInfo.PlanePitchBufferLength(_outPixelFormat, width, height, i, strides[i]);
            memories[i] = buffers[i].AsMemory(0, len);
        }

        var captured = buffers;
        return new VideoFrame(pts, Format, memories, strides,
            release: DisposableRelease.Wrap(() =>
            {
                foreach (var b in captured)
                    ArrayPool<byte>.Shared.Return(b);
            }),
            metadata: meta);
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
            var meta = ExtractMetadata(_frame);
            SyncCodecPixelFormatIfNeeded(workFrame);

            var pts = ResolvePts(workFrame);
            if (pts >= targetPresentationTime)
            {
                _primedAfterSeek = BuildFrame(workFrame, meta);
                av_frame_unref(_frame);
                return;
            }

            av_frame_unref(_frame);
        }
    }

    /// <summary>Rebuild native / scaler state when libav reports a different software pixel layout mid-stream.</summary>
    private void SyncCodecPixelFormatIfNeeded(AVFrame* workFrame)
    {
        if (_drmGpuNv12Path || _d3d11GpuNv12Path)
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

    /// <summary>
    /// Hardware decode: learn the CPU layout after <c>av_hwframe_transfer_data</c> so
    /// <see cref="NativePixelFormats"/> matches reality before <see cref="VideoFormatNegotiator.Connect"/>.
    /// </summary>
    private void PrimeHardwareTransferPixelFormat()
    {
        if (_hwAccel == null || _drmGpuNv12Path || _d3d11GpuNv12Path)
            return;

        while (true)
        {
            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0)
            {
                var workFrame = ResolveWorkFrame();
                SyncCodecPixelFormatIfNeeded(workFrame);
                av_frame_unref(_frame);
                return;
            }
            if (ret == AVERROR_EOF)
                return;
            if (ret == AVERROR(EAGAIN))
            {
                FeedDecoder();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
        }
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

    private void ResetReadYield() => Volatile.Write(ref _readYieldRequested, 0);

    void ICooperativeVideoReadInterrupt.RequestYieldBetweenReads() => Volatile.Write(ref _readYieldRequested, 1);

    void ICooperativeVideoReadInterrupt.ClearYieldRequest() => ResetReadYield();

    internal static PixelFormat MapNativePixelFormat(AVPixelFormat src) => src switch
    {
        AVPixelFormat.AV_PIX_FMT_DRM_PRIME      => PixelFormat.Nv12,
        AVPixelFormat.AV_PIX_FMT_D3D11          => PixelFormat.Nv12,
        AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD    => PixelFormat.Nv12,
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
        AVPixelFormat.AV_PIX_FMT_YUVA422P    => PixelFormat.Yuva422P,
        AVPixelFormat.AV_PIX_FMT_YUVA444P    => PixelFormat.Yuva444P,
        AVPixelFormat.AV_PIX_FMT_YUVA420P10LE => PixelFormat.Yuva420P10Le,
        AVPixelFormat.AV_PIX_FMT_YUVA422P10LE => PixelFormat.Yuva422P10Le,
        AVPixelFormat.AV_PIX_FMT_YUVA444P10LE => PixelFormat.Yuva444P10Le,
        AVPixelFormat.AV_PIX_FMT_YUVA422P12LE => PixelFormat.Yuva422P12Le,
        AVPixelFormat.AV_PIX_FMT_YUVA444P12LE => PixelFormat.Yuva444P12Le,
        AVPixelFormat.AV_PIX_FMT_YUVA420P16LE => PixelFormat.Yuva420P16Le,
        AVPixelFormat.AV_PIX_FMT_YUVA422P16LE => PixelFormat.Yuva422P16Le,
        AVPixelFormat.AV_PIX_FMT_YUVA444P16LE => PixelFormat.Yuva444P16Le,
        AVPixelFormat.AV_PIX_FMT_YUV422P12LE => PixelFormat.Yuv422P12Le,
        AVPixelFormat.AV_PIX_FMT_YUV444P12LE => PixelFormat.Yuv444P12Le,
        AVPixelFormat.AV_PIX_FMT_ARGB        => PixelFormat.Argb32,
        AVPixelFormat.AV_PIX_FMT_ABGR       => PixelFormat.Abgr32,
        AVPixelFormat.AV_PIX_FMT_P010LE      => PixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_P016LE      => PixelFormat.P016,
        _                                   => PixelFormat.Unknown,
    };

    internal static int BytesPerPackedPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Argb32 or PixelFormat.Abgr32 => 4,
        PixelFormat.Bgr24  or PixelFormat.Rgb24  => 3,
        PixelFormat.Uyvy   or PixelFormat.Yuyv   => 2,
        _ => 0,
    };

    /// <summary>Maps libav <see cref="AVColorTransferCharacteristic"/> to a Core hint consumed by HDR-aware outputs.</summary>
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

    /// <summary>Maps libav <see cref="AVColorSpace"/> to a Core <see cref="VideoColorSpace"/> hint.</summary>
    internal static VideoColorSpace MapColorSpace(AVColorSpace cs) => cs switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => VideoColorSpace.Bt709,
        AVColorSpace.AVCOL_SPC_SMPTE170M => VideoColorSpace.Bt601,
        AVColorSpace.AVCOL_SPC_BT470BG => VideoColorSpace.Bt601,
        AVColorSpace.AVCOL_SPC_BT2020_NCL => VideoColorSpace.Bt2020,
        AVColorSpace.AVCOL_SPC_BT2020_CL => VideoColorSpace.Bt2020Cl,
        _ => VideoColorSpace.Unspecified,
    };

    /// <summary>Maps libav <see cref="AVColorRange"/> to a Core <see cref="VideoColorRange"/> hint.</summary>
    internal static VideoColorRange MapColorRange(AVColorRange range) => range switch
    {
        AVColorRange.AVCOL_RANGE_MPEG => VideoColorRange.Limited,
        AVColorRange.AVCOL_RANGE_JPEG => VideoColorRange.Full,
        _ => VideoColorRange.Unspecified,
    };

    /// <summary>
    /// Maps libav frame interlace flags into a Core <see cref="VideoFieldOrder"/> hint. On
    /// FFmpeg ≥ 7 the legacy <c>interlaced_frame</c>/<c>top_field_first</c> bits live on
    /// <see cref="AVFrame.flags"/>; we read the spelled-out names from FFmpeg.AutoGen.
    /// </summary>
    internal static unsafe VideoFieldOrder MapFieldOrder(AVFrame* frame)
    {
        // FFmpeg.AutoGen exposes AV_FRAME_FLAG_INTERLACED + AV_FRAME_FLAG_TOP_FIELD_FIRST as int constants
        // on the ffmpeg static class. Fall back to legacy interlaced_frame/top_field_first when those
        // members aren't available (older FFmpeg.AutoGen surfaces).
        const int AV_FRAME_FLAG_INTERLACED = 1 << 3;
        const int AV_FRAME_FLAG_TOP_FIELD_FIRST = 1 << 4;
        var flags = frame->flags;
        var interlaced = (flags & AV_FRAME_FLAG_INTERLACED) != 0;
        if (!interlaced) return VideoFieldOrder.Progressive;
        var tff = (flags & AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0;
        return tff ? VideoFieldOrder.TopFieldFirst : VideoFieldOrder.BottomFieldFirst;
    }

    /// <summary>
    /// Reads the first SMPTE 12M timecode side-data on <paramref name="frame"/>, when present. Returns
    /// <see langword="null"/> when the source carries no S12M side data.
    /// </summary>
    internal static unsafe VideoTimecode? ReadS12mTimecode(AVFrame* frame, Rational frameRate)
    {
        var sd = av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_S12M_TIMECODE);
        if (sd is null || sd->size < sizeof(uint) * 2) return null;
        var words = (uint*)sd->data;
        var count = words[0];
        if (count == 0) return null;
        var tc = words[1];

        // SMPTE 12M packed bits (per libavutil/timecode.h):
        //   bits 0-3  : frames units
        //   bits 4-7  : frames tens
        //   bits 8    : drop frame
        //   bits 9    : color frame
        //   bits 10-13: seconds units
        //   bits 14-16: seconds tens
        //   bit  17   : flags
        //   bits 18-21: minutes units
        //   bits 22-24: minutes tens
        //   bit  25-27: flags
        //   bits 28-31: hours units / tens
        var ff = (int)(tc & 0x0F);
        var fT = (int)((tc >> 4) & 0x03);
        var dropFrame = ((tc >> 6) & 0x01) != 0;
        var ss = (int)((tc >> 8) & 0x0F);
        var sT = (int)((tc >> 12) & 0x07);
        var mm = (int)((tc >> 16) & 0x0F);
        var mT = (int)((tc >> 20) & 0x07);
        var hh = (int)((tc >> 24) & 0x0F);
        var hT = (int)((tc >> 28) & 0x03);

        var hours = hT * 10 + hh;
        var mins = mT * 10 + mm;
        var secs = sT * 10 + ss;
        var frames = fT * 10 + ff;

        try
        {
            return new VideoTimecode(hours, mins, secs, frames, dropFrame, frameRate);
        }
        catch (ArgumentException)
        {
            // Malformed timecode (out-of-range fields, drop-frame at a non-drop-rate). Drop it
            // rather than crashing the decode loop — the absence is signalling enough.
            return null;
        }
    }
}
