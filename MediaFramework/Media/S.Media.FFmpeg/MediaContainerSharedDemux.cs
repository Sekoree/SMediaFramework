using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg;

/// <summary>
/// Single <see cref="AVFormatContext"/> demux with a background reader thread, bounded packet queues
/// per A/V stream, and independent decode locks so audio and video consumers can run on different threads.
/// Video matches <see cref="VideoFileDecoder"/> hardware / software behaviour (including DRM PRIME semi-planar NV12/P010/P016 and D3D11 shared-handle NV12).
/// </summary>
/// <remarks>
/// Pass-through libav video uses <see cref="PassThroughDescriptorArena"/> the same way as <see cref="VideoFileDecoder"/> (Treiber-stack pooled descriptor arrays, <c>Array.Clear</c> on return).
/// <c>Rent</c>/<c>Return</c> run under the normal video decode serialization for this demux — there is no extra mutex around the arena itself (checklist **Tier E** **16**; **§Tier F** row **29** **`[x]`**).
/// On teardown, selected managed disposals (<see cref="PassThroughDescriptorArena"/>, hardware acceleration, primed-seek holder) are wrapped so <strong>Debug</strong> builds log via <see cref="S.Media.Core.Diagnostics.MediaDiagnostics.LogError"/> and <strong>Release</strong> builds continue best-effort (same policy as <see cref="VideoRouter.Dispose"/>).
/// </remarks>
internal sealed unsafe class MediaContainerSharedDemux : IDisposable
{
    private const long AvTimeBase = 1_000_000L;
    private const int MaxAudioPacketsQueued = 192;
    private const int MaxVideoPacketsQueued = 384;

    /// <summary><c>AV_DISPOSITION_ATTACHED_PIC</c> — album art / cover; must not be chosen over a real video track.</summary>
    private const int AvDispositionAttachedPic = 1024;

    private readonly object _lifecycleLock = new();
    private readonly Lock _audioDecodeLock = new();
    private readonly Lock _videoDecodeLock = new();
    private readonly object _queueGate = new();

    private volatile bool _disposed;
    private volatile bool _demuxerStopRequest;
    private int _videoDecodeYieldRequested;
    private volatile bool _fileReadCompleted;
    private Thread? _demuxerThread;
    private AVFormatContext* _fmt;
    private AVPacket* _demuxPkt;

    private readonly Queue<nint> _audioPacketQ = new();
    private readonly Queue<nint> _videoPacketQ = new();

    /// <summary>Packet dequeued but not yet accepted by <c>avcodec_send_packet</c> (EAGAIN) — must be retried.</summary>
    private nint _aPendingPacket;
    private nint _vPendingPacket;

    private int _aStream = -1;
    private bool _hasAudio;
    private AVRational _aTb;
    private AVCodecContext* _aCtx;
    private SwrContext* _swr;
    private AVFrame* _aFrame;
    private bool _aEof;
    private bool _aDrainSent;
    private long _aSamplesEmitted;
    private bool _aDrainedTail;

    private int _vStream = -1;
    private bool _hasVideo;
    private AVRational _vTb;
    private AVCodecContext* _vCtx;
    private SwsContext* _swsCtx;
    private AVFrame* _vFrame;
    private AVPixelFormat _vSrcPixFmt;
    private PixelFormat _vNativePixFmt;
    private PixelFormat _vOutPixFmt;
    private PixelFormat[] _vNativePixFormats = [];
    private bool _vPassThrough;
    private long _vFramesEmitted;
    private bool _vEof;
    private bool _vDrainSent;
    private VideoFrame? _vPrimedAfterSeek;
    private VideoHardwareDecodeContext? _hwAccel;
    private bool _drmGpuNv12Path;
    private bool _d3d11GpuNv12Path;
    private bool _win32Nv12SharedHandleOnly;

    private readonly PassThroughDescriptorArena _passThroughArena = new();

    private readonly byte*[] _swSrcLines = new byte*[8];
    private readonly int[] _swSrcStride = new int[8];
    private readonly byte*[] _swDstLines = new byte*[8];
    private readonly int[] _swDstStride = new int[8];

    public AudioTrack Audio { get; }
    public VideoTrack Video { get; }

    public string AudioCodecName { get; private set; } = "";
    public string VideoCodecName { get; private set; } = "";
    public TimeSpan Duration { get; private set; }

    /// <summary>True when the container exposed a decodable audio stream — false for video-only files.</summary>
    public bool HasAudio => _hasAudio;

    /// <summary>True when the container exposed a decodable video stream — false for audio-only files
    /// (the <see cref="VideoTrack"/> is a stub that reports <c>IsExhausted = true</c> immediately).</summary>
    public bool HasVideo => _hasVideo;

    private MediaContainerSharedDemux()
    {
        Audio = new AudioTrack(this);
        Video = new VideoTrack(this);
    }

    internal static MediaContainerSharedDemux Open(string path, VideoDecoderOpenOptions? videoOptions)
    {
        var d = new MediaContainerSharedDemux();
        try
        {
            d.OpenInternal(path, videoOptions);
            return d;
        }
        catch
        {
            d.Dispose();
            throw;
        }
    }

    internal bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr)
    {
        deviceComPtr = _hwAccel?.TryGetD3D11DeviceComPtr() ?? 0;
        return deviceComPtr != 0;
    }

    internal bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
        return _hwAccel?.TryGetD3D11AdapterLuid(out adapterLuidPacked) == true;
    }

    /// <summary>True when D3D11 NV12 backing is built without libav D3D11 COM pointers (DXGI shared handle path only).</summary>
    internal bool Win32Nv12SharedHandleOnlyActive => _win32Nv12SharedHandleOnly;

    private void OpenInternal(string path, VideoDecoderOpenOptions? videoOptions)
    {
        AVFormatContext* fmt = null;
        var ret = avformat_open_input(&fmt, path, null, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
        _fmt = fmt;

        ret = avformat_find_stream_info(_fmt, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));

        AVCodec* aCodec = null;
        _aStream = av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &aCodec, 0);
        _hasAudio = _aStream >= 0 && aCodec != null;
        if (!_hasAudio)
            _aStream = -1;

        AVCodec* vCodec = null;
        _vStream = PickVideoStreamIndex(_fmt, _aStream, &vCodec);
        _hasVideo = _vStream >= 0 && vCodec != null;
        if (!_hasVideo)
        {
            _vStream = -1;
            if (!_hasAudio)
                throw new FFmpegException(_vStream, "no decodable audio or video stream found");
        }

        AVStream* aSt = null;
        if (_hasAudio)
        {
            aSt = _fmt->streams[_aStream];
            _aTb = aSt->time_base;
            AudioCodecName = Marshal.PtrToStringAnsi((IntPtr)aCodec->name) ?? "unknown";

            _aCtx = avcodec_alloc_context3(aCodec);
            if (_aCtx == null) throw new OutOfMemoryException("audio avcodec_alloc_context3 returned NULL");
            ret = avcodec_parameters_to_context(_aCtx, aSt->codecpar);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));
            ret = avcodec_open2(_aCtx, aCodec, null);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

            Audio.Format = new AudioFormat(_aCtx->sample_rate, _aCtx->ch_layout.nb_channels);

            AVChannelLayout outLayout;
            av_channel_layout_default(&outLayout, Audio.Format.Channels);
            SwrContext* swr = null;
            ret = swr_alloc_set_opts2(&swr,
                &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Audio.Format.SampleRate,
                &_aCtx->ch_layout, _aCtx->sample_fmt, _aCtx->sample_rate,
                0, null);
            av_channel_layout_uninit(&outLayout);
            FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));
            _swr = swr;
            ret = swr_init(_swr);
            FFmpegException.ThrowIfError(ret, nameof(swr_init));
        }
        else
        {
            // Sentinel format for video-only files: 0 channels signals "no audio" to consumers.
            // The 48000 Hz rate is a placeholder for clock-math fallback paths that never run when
            // MediaContainerDecoder.HasAudio is false (MediaPlayer skips AudioPlayer creation).
            AudioCodecName = "";
            Audio.Format = new AudioFormat(48000, 0);
        }

        AVStream* vSt = null;
        if (!_hasVideo)
        {
            // No decodable video stream — audio-only file (e.g. MP3 with no cover art).
            // Provide a stub VideoFormat so format negotiation between the stub IVideoSource and a
            // permissive sink (DiscardingVideoSink, or any sink with at least one accepted format)
            // succeeds. The decode loop never produces frames because VideoTrack.IsExhausted is true.
            VideoCodecName = "";
            _vSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
            _vNativePixFmt = PixelFormat.Bgra32;
            _vPassThrough = false;
            _vOutPixFmt = PixelFormat.Bgra32;
            _vNativePixFormats = [PixelFormat.Bgra32];
            Video.Format = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

            // Duration falls back to audio (already populated above) or to the container duration.
            if (_hasAudio && aSt is not null && aSt->duration > 0)
                Duration = PtsToTimeSpanAudio(aSt->duration);
            else if (_fmt->duration > 0)
                Duration = TimeSpan.FromMicroseconds(_fmt->duration);

            _demuxPkt = av_packet_alloc();
            if (_demuxPkt == null) throw new OutOfMemoryException("demux av_packet_alloc returned NULL");
            if (_hasAudio)
            {
                _aFrame = av_frame_alloc();
                if (_aFrame == null) throw new OutOfMemoryException("audio av_frame_alloc returned NULL");
            }

            StartDemuxerThread();
            return;
        }

        vSt = _fmt->streams[_vStream];
        _vTb = vSt->time_base;
        VideoCodecName = Marshal.PtrToStringAnsi((IntPtr)vCodec->name) ?? "unknown";

        _vCtx = avcodec_alloc_context3(vCodec);
        if (_vCtx == null) throw new OutOfMemoryException("video avcodec_alloc_context3 returned NULL");
        ret = avcodec_parameters_to_context(_vCtx, vSt->codecpar);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));

        var tryHw = videoOptions?.TryHardwareAcceleration ?? true;
        var retainDmabuf = videoOptions?.RetainDmabufForGl ?? false;
        var retainD3D11 = videoOptions?.RetainD3D11SharedHandleForGl ?? false;
        _win32Nv12SharedHandleOnly = retainD3D11 && VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(videoOptions);
        var preferredDevices = videoOptions?.PreferredDeviceTypes ?? [];

        VideoHardwareDecodeContext? hw = null;
        if (tryHw)
            hw = VideoHardwareDecodeContext.TryCreate(vCodec, _vCtx, preferredDevices, retainDmabuf, retainD3D11);

        if (hw == null)
            VideoFileDecoder.ApplyDecoderThreading(vCodec, _vCtx, videoOptions);

        AVDictionary* openOpts = null;
        if (hw?.OutputsDrmPrimeGpuFrame == true)
            av_dict_set(&openOpts, "hwaccel_output_format", "drm_prime", 0);
        else if (hw?.OutputsD3D11GpuFrame == true)
            av_dict_set(&openOpts, "hwaccel_output_format", "d3d11", 0);

        ret = avcodec_open2(_vCtx, vCodec, &openOpts);
        if (openOpts != null)
            av_dict_free(&openOpts);

        if (ret < 0 && hw != null)
        {
            hw.DetachFromCodec(_vCtx);
            hw.Dispose();
            hw = null;
            VideoFileDecoder.ApplyDecoderThreading(vCodec, _vCtx, videoOptions);
            ret = avcodec_open2(_vCtx, vCodec, null);
        }
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _hwAccel = hw;
        _drmGpuNv12Path = hw?.OutputsDrmPrimeGpuFrame == true;
        _d3d11GpuNv12Path = hw?.OutputsD3D11GpuFrame == true;

        if (hw != null)
        {
            if (_drmGpuNv12Path)
            {
                var drmPass = VideoFileDecoder.InferDrmPrimeOutputPixelFormat(vSt->codecpar);
                _vSrcPixFmt = AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
                _vNativePixFmt = drmPass;
                _vPassThrough = true;
                _vOutPixFmt = drmPass;
                _vNativePixFormats = [drmPass];
            }
            else if (_d3d11GpuNv12Path)
            {
                _vSrcPixFmt = _hwAccel!.HwAccelPixFmt;
                _vNativePixFmt = PixelFormat.Nv12;
                _vPassThrough = true;
                _vOutPixFmt = PixelFormat.Nv12;
                _vNativePixFormats = [PixelFormat.Nv12];
            }
            else
            {
                _vSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
                _vNativePixFmt = PixelFormat.Nv12;
                _vPassThrough = false;
                _vOutPixFmt = PixelFormat.Bgra32;
                _vNativePixFormats = [PixelFormat.Nv12];
            }
        }
        else
        {
            _vSrcPixFmt = _vCtx->pix_fmt;
            var nativeSoft = VideoFileDecoder.MapNativePixelFormat(_vSrcPixFmt);
            _vNativePixFmt = nativeSoft != PixelFormat.Unknown ? nativeSoft : PixelFormat.Bgra32;
            _vPassThrough = nativeSoft != PixelFormat.Unknown;
            _vOutPixFmt = _vNativePixFmt;
            _vNativePixFormats = nativeSoft != PixelFormat.Unknown ? [nativeSoft] : [];
        }

        var fps = vSt->avg_frame_rate;
        if (fps.num <= 0 || fps.den <= 0) fps = vSt->r_frame_rate;
        var frameRate = NormalizeVideoFrameRate(fps, vSt->disposition);

        if (_vCtx->width <= 0 || _vCtx->height <= 0 || _vCtx->width > 7680 || _vCtx->height > 4320 ||
            (long)_vCtx->width * _vCtx->height > 32_000_000L)
        {
            throw new FFmpegException(0,
                $"video dimensions are unusable ({_vCtx->width}×{_vCtx->height}). " +
                "If this is audio with a broken embedded cover stream, re-encode or strip the cover.");
        }

        // Some streams (notably attached_pic / album cover art) report odd dimensions. Pixel formats with
        // chroma subsampling (I420 / NV12 over NDI) require even W and H, and BGRA / RGBA / UYVY require
        // even W. Round up to the next even multiple here and route through the sws-to-BGRA32 path so
        // downstream sinks always see even dimensions. The visible difference is at most one row/column
        // of sub-pixel rescaling — imperceptible for a static cover image.
        var outWidth = _vCtx->width + (_vCtx->width & 1);
        var outHeight = _vCtx->height + (_vCtx->height & 1);
        var dimensionsRounded = outWidth != _vCtx->width || outHeight != _vCtx->height;

        if (dimensionsRounded && hw is null)
        {
            // Force the sws-to-BGRA32 path so the destination buffer matches Video.Format dims.
            _vSrcPixFmt = _vCtx->pix_fmt;
            _vNativePixFmt = PixelFormat.Bgra32;
            _vPassThrough = false;
            _vOutPixFmt = PixelFormat.Bgra32;
            _vNativePixFormats = [];
        }

        Video.Format = new VideoFormat(outWidth, outHeight, _vOutPixFmt, frameRate);

        if (aSt is not null && aSt->duration > 0)
            Duration = PtsToTimeSpanAudio(aSt->duration);
        else if (vSt->duration > 0)
            Duration = PtsToTimeSpanVideo(vSt->duration);
        else if (_fmt->duration > 0)
            Duration = TimeSpan.FromMicroseconds(_fmt->duration);

        if (hw == null && !_vPassThrough)
        {
            _swsCtx = sws_getCachedContext(null,
                _vCtx->width, _vCtx->height, _vSrcPixFmt,
                outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, "sws_getCachedContext returned NULL");
        }

        _demuxPkt = av_packet_alloc();
        if (_demuxPkt == null) throw new OutOfMemoryException("demux av_packet_alloc returned NULL");
        if (_hasAudio)
        {
            _aFrame = av_frame_alloc();
            if (_aFrame == null) throw new OutOfMemoryException("audio av_frame_alloc returned NULL");
        }
        _vFrame = av_frame_alloc();
        if (_vFrame == null) throw new OutOfMemoryException("video av_frame_alloc returned NULL");

        StartDemuxerThread();
    }

    private static bool VideoCodecparDimensionsLookSane(AVCodecParameters* cp)
    {
        if (cp == null)
            return false;
        var w = cp->width;
        var h = cp->height;
        if (w <= 0 || h <= 0)
            return false;
        if (w > 7680 || h > 4320)
            return false;
        if ((long)w * h > 32_000_000L)
            return false;
        return true;
    }

    private static int PickVideoStreamIndex(AVFormatContext* fmt, int relatedAudioStream, AVCodec** decoderRet)
    {
        *decoderRet = null;
        for (var i = 0; i < fmt->nb_streams; i++)
        {
            var st = fmt->streams[i];
            if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                continue;
            if ((st->disposition & AvDispositionAttachedPic) != 0)
                continue;
            if (!VideoCodecparDimensionsLookSane(st->codecpar))
                continue;

            AVCodec* c = null;
            var idx = av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, i, relatedAudioStream, &c, 0);
            if (idx == i && c != null)
            {
                *decoderRet = c;
                return i;
            }
        }

        return av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, relatedAudioStream, decoderRet, 0);
    }

    private static Rational NormalizeVideoFrameRate(AVRational ffmpegRat, int streamDisposition)
    {
        var num = ffmpegRat.num;
        var den = Math.Max(ffmpegRat.den, 1);
        if (num <= 0)
            return new Rational(1, 1);

        var r = new Rational(num, den);
        var d = r.ToDouble();
        var attached = (streamDisposition & AvDispositionAttachedPic) != 0;

        if (attached)
            // attached_pic streams are conceptually 1 FPS (one still for the whole file), but downstream
            // sinks with SDK-paced video clocks (NDI's clockVideo:true paces NDIlib_send_send_video_async_v2
            // at the declared rate) would then block sender threads for one second per frame — making prime
            // / hold-toggle / cover-art appearance take 10+ seconds in practice. Declare 30 FPS so the SDK
            // paces at ~33 ms per send; we still only emit a single decoded frame, receivers just hold it.
            return new Rational(30, 1);

        if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0)
            return new Rational(30, 1);

        if (d < 0.05 || d > 120.0)
            return new Rational(30, 1);

        if (den >= 5000 && d < 2.0)
            return new Rational(30, 1);

        return r;
    }

    private void StartDemuxerThread()
    {
        _demuxerStopRequest = false;
        _fileReadCompleted = false;
        _demuxerThread = new Thread(DemuxerThreadProc)
        {
            IsBackground = true,
            Name = "MediaContainerSharedDemux.Read",
        };
        _demuxerThread.Start();
    }

    private void StopDemuxerAndDrainQueues()
    {
        _demuxerStopRequest = true;
        lock (_queueGate)
            Monitor.PulseAll(_queueGate);

        if (_demuxerThread is { } t)
        {
            if (!t.Join(TimeSpan.FromSeconds(4)))
            {
                // Best-effort: demuxer may be blocked in av_read_frame; continue teardown.
            }
            _demuxerThread = null;
        }

        _demuxerStopRequest = false;
        FreeAllQueuedPacketsLocked();
    }

    private void FreeAllQueuedPacketsLocked()
    {
        lock (_queueGate)
        {
            if (_vPendingPacket != nint.Zero)
            {
                var p = (AVPacket*)_vPendingPacket;
                av_packet_free(&p);
                _vPendingPacket = nint.Zero;
            }

            if (_aPendingPacket != nint.Zero)
            {
                var p = (AVPacket*)_aPendingPacket;
                av_packet_free(&p);
                _aPendingPacket = nint.Zero;
            }

            while (_audioPacketQ.Count > 0)
            {
                var pkt = (AVPacket*)_audioPacketQ.Dequeue();
                av_packet_free(&pkt);
            }
            while (_videoPacketQ.Count > 0)
            {
                var pkt = (AVPacket*)_videoPacketQ.Dequeue();
                av_packet_free(&pkt);
            }
        }
    }

    private void DemuxerThreadProc()
    {
        while (true)
        {
            if (_demuxerStopRequest)
                return;

            av_packet_unref(_demuxPkt);
            var ret = av_read_frame(_fmt, _demuxPkt);
            if (ret == AVERROR_EOF)
            {
                lock (_queueGate)
                {
                    _fileReadCompleted = true;
                    Monitor.PulseAll(_queueGate);
                }
                return;
            }
            if (ret < 0)
            {
                FFmpegException.ThrowIfError(ret, nameof(av_read_frame));
                return;
            }

            if (_demuxPkt->stream_index == _aStream)
                EnqueuePacketCopy(_audioPacketQ, MaxAudioPacketsQueued);
            else if (_demuxPkt->stream_index == _vStream)
                EnqueuePacketCopy(_videoPacketQ, MaxVideoPacketsQueued);
        }
    }

    private void EnqueuePacketCopy(Queue<nint> target, int maxCount)
    {
        var copy = av_packet_alloc();
        if (copy == null) throw new OutOfMemoryException("av_packet_alloc (queue) returned NULL");
        var refRet = av_packet_ref(copy, _demuxPkt);
        if (refRet < 0)
        {
            var c = copy;
            av_packet_free(&c);
            FFmpegException.ThrowIfError(refRet, nameof(av_packet_ref));
        }

        lock (_queueGate)
        {
            while (target.Count >= maxCount && !_demuxerStopRequest)
                Monitor.Wait(_queueGate, 5);

            if (_demuxerStopRequest)
            {
                var c = copy;
                av_packet_free(&c);
                return;
            }

            target.Enqueue((nint)copy);
            Monitor.PulseAll(_queueGate);
        }
    }

    internal void SeekPresentation(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));

        lock (_lifecycleLock)
        {
            ThrowIfDisposedUnsafe();
            ClearVideoDecodeYield();
            StopDemuxerAndDrainQueues();

            var timestampUs = (long)(position.TotalSeconds * AvTimeBase);
            var ret = avformat_seek_file(_fmt, -1, long.MinValue, timestampUs, long.MaxValue, AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                if (_hasVideo)
                {
                    var vTs = (long)(position.TotalSeconds * _vTb.den / _vTb.num);
                    ret = av_seek_frame(_fmt, _vStream, vTs, AVSEEK_FLAG_BACKWARD);
                    FFmpegException.ThrowIfError(ret, nameof(av_seek_frame));
                }
                else if (_hasAudio)
                {
                    var aTs = (long)(position.TotalSeconds * _aTb.den / _aTb.num);
                    ret = av_seek_frame(_fmt, _aStream, aTs, AVSEEK_FLAG_BACKWARD);
                    FFmpegException.ThrowIfError(ret, nameof(av_seek_frame));
                }
                else
                {
                    FFmpegException.ThrowIfError(ret, nameof(avformat_seek_file));
                }
            }

            if (_hasAudio)
                avcodec_flush_buffers(_aCtx);
            if (_hasVideo)
                avcodec_flush_buffers(_vCtx);

            av_packet_unref(_demuxPkt);
            if (_hasAudio)
            {
                av_frame_unref(_aFrame);
                swr_close(_swr);
                ret = swr_init(_swr);
                FFmpegException.ThrowIfError(ret, nameof(swr_init));
            }
            if (_hasVideo)
                av_frame_unref(_vFrame);

            _aEof = false;
            _aDrainSent = false;
            _aDrainedTail = false;
            _aSamplesEmitted = _hasAudio ? (long)(position.TotalSeconds * Audio.Format.SampleRate) : 0;
            Audio.Position = position;

            _vEof = false;
            _vDrainSent = false;
            _vPrimedAfterSeek?.Dispose();
            _vPrimedAfterSeek = null;
            Video.Position = position;

            _fileReadCompleted = false;
            StartDemuxerThread();

            if (_hasVideo)
            {
                lock (_videoDecodeLock)
                    ConsumeDecoderUntilPts(position);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;

            StopDemuxerAndDrainQueues();

            try
            {
                _vPrimedAfterSeek?.Dispose();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "MediaContainerSharedDemux.Dispose: _vPrimedAfterSeek");
            }
#else
            catch
            {
            }
#endif
            _vPrimedAfterSeek = null;

            if (_demuxPkt != null) { var p = _demuxPkt; av_packet_free(&p); _demuxPkt = null; }
            if (_aFrame != null) { var f = _aFrame; av_frame_free(&f); _aFrame = null; }
            if (_vFrame != null) { var f = _vFrame; av_frame_free(&f); _vFrame = null; }

            if (_swr != null) { var s = _swr; swr_free(&s); _swr = null; }
            ReleaseSws();

            if (_aCtx != null) { var c = _aCtx; avcodec_free_context(&c); _aCtx = null; }
            if (_vCtx != null)
            {
                _hwAccel?.DetachFromCodec(_vCtx);
                var c = _vCtx;
                avcodec_free_context(&c);
                _vCtx = null;
            }

            try
            {
                _hwAccel?.Dispose();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "MediaContainerSharedDemux.Dispose: _hwAccel");
            }
#else
            catch
            {
            }
#endif
            _hwAccel = null;

            try
            {
                _passThroughArena.Dispose();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "MediaContainerSharedDemux.Dispose: _passThroughArena");
            }
#else
            catch
            {
            }
#endif

            if (_fmt != null) { var f = _fmt; avformat_close_input(&f); _fmt = null; }
        }
    }

    private void ThrowIfDisposedUnsafe()
    {
        if (_fmt == null) throw new ObjectDisposedException(nameof(MediaContainerSharedDemux));
    }

    private TimeSpan PtsToTimeSpanAudio(long pts)
        => TimeSpan.FromSeconds((double)pts * _aTb.num / _aTb.den);

    private TimeSpan PtsToTimeSpanVideo(long pts)
        => TimeSpan.FromSeconds((double)pts * _vTb.num / _vTb.den);

    private void ReleaseSws()
    {
        if (_swsCtx == null) return;
        sws_freeContext(_swsCtx);
        _swsCtx = null;
    }

    private bool TryReceiveAudioFrame()
    {
        while (true)
        {
            var ret = avcodec_receive_frame(_aCtx, _aFrame);
            if (ret == 0) return true;
            if (ret == AVERROR_EOF)
            {
                _aEof = true;
                return false;
            }
            if (ret == AVERROR(EAGAIN))
            {
                FeedAudioFromQueue();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
            return false;
        }
    }

    private void FeedAudioFromQueue()
    {
        while (true)
        {
            AVPacket* pkt = null;
            lock (_queueGate)
            {
                if (_aPendingPacket != nint.Zero)
                {
                    pkt = (AVPacket*)_aPendingPacket;
                    _aPendingPacket = nint.Zero;
                }
                else
                {
                    while (_audioPacketQ.Count == 0 && !_fileReadCompleted && !_demuxerStopRequest)
                        Monitor.Wait(_queueGate, 50);

                    if (_audioPacketQ.Count > 0)
                        pkt = (AVPacket*)_audioPacketQ.Dequeue();
                }

                Monitor.PulseAll(_queueGate);
            }

            if (pkt != null)
            {
                var ret = avcodec_send_packet(_aCtx, pkt);
                if (ret == 0)
                {
                    av_packet_free(&pkt);
                    _aDrainSent = false;
                    return;
                }

                if (ret == AVERROR(EAGAIN))
                {
                    _aPendingPacket = (nint)pkt;
                    return;
                }

                av_packet_free(&pkt);
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }

            if (_fileReadCompleted)
            {
                if (_aDrainSent) return;
                var ret = avcodec_send_packet(_aCtx, null);
                if (ret == 0 || ret == AVERROR(EAGAIN))
                {
                    _aDrainSent = true;
                    return;
                }
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }
        }
    }

    private int SwrConvertInto(Span<float> dst, int capacityFrames, byte** inputData, int inputSamples)
    {
        if (capacityFrames <= 0) return 0;
        int converted;
        fixed (float* dstPtr = dst)
        {
            var outBuf = (byte*)dstPtr;
            converted = swr_convert(_swr, &outBuf, capacityFrames, inputData, inputSamples);
        }
        if (converted < 0) FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        return converted;
    }

    private TimeSpan ResolveAudioPts()
    {
        var pts = _aFrame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE) pts = _aFrame->pts;
        return pts == AV_NOPTS_VALUE
            ? TimeSpan.FromSeconds((double)_aSamplesEmitted / Audio.Format.SampleRate)
            : PtsToTimeSpanAudio(pts);
    }

    private AudioFrame ConvertAudioFrame()
    {
        var srcSamples = _aFrame->nb_samples;
        var outCapacity = (int)av_rescale_rnd(
            swr_get_delay(_swr, Audio.Format.SampleRate) + srcSamples,
            Audio.Format.SampleRate, Audio.Format.SampleRate, AVRounding.AV_ROUND_UP);

        var samples = new float[outCapacity * Audio.Format.Channels];
        int converted;
        fixed (float* outPtr = samples)
        {
            var outBuf = (byte*)outPtr;
            converted = swr_convert(_swr, &outBuf, outCapacity, _aFrame->extended_data, srcSamples);
        }
        if (converted < 0) FFmpegException.ThrowIfError(converted, nameof(swr_convert));

        var pts = ResolveAudioPts();
        Audio.Position = pts + TimeSpan.FromSeconds((double)converted / Audio.Format.SampleRate);
        _aSamplesEmitted += converted;

        return new AudioFrame(pts, Audio.Format, converted, samples.AsMemory(0, converted * Audio.Format.Channels));
    }

    internal void RequestVideoDecodeYield()
    {
        Volatile.Write(ref _videoDecodeYieldRequested, 1);
        lock (_queueGate)
            Monitor.PulseAll(_queueGate);
    }

    internal void ClearVideoDecodeYield() => Volatile.Write(ref _videoDecodeYieldRequested, 0);

    private void FeedVideoFromQueue()
    {
        if (Volatile.Read(ref _videoDecodeYieldRequested) != 0)
            return;

        while (true)
        {
            if (Volatile.Read(ref _videoDecodeYieldRequested) != 0)
                return;

            AVPacket* pkt = null;
            lock (_queueGate)
            {
                if (_vPendingPacket != nint.Zero)
                {
                    pkt = (AVPacket*)_vPendingPacket;
                    _vPendingPacket = nint.Zero;
                }
                else
                {
                    while (_videoPacketQ.Count == 0 && !_fileReadCompleted && !_demuxerStopRequest)
                        Monitor.Wait(_queueGate, 50);

                    if (_videoPacketQ.Count > 0)
                        pkt = (AVPacket*)_videoPacketQ.Dequeue();
                }

                Monitor.PulseAll(_queueGate);
            }

            if (pkt != null)
            {
                var ret = avcodec_send_packet(_vCtx, pkt);
                if (ret == 0)
                {
                    av_packet_free(&pkt);
                    _vDrainSent = false;
                    return;
                }

                if (ret == AVERROR(EAGAIN))
                {
                    _vPendingPacket = (nint)pkt;
                    return;
                }

                av_packet_free(&pkt);
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }

            if (_fileReadCompleted)
            {
                if (_vDrainSent) return;
                var ret = avcodec_send_packet(_vCtx, null);
                if (ret == 0 || ret == AVERROR(EAGAIN))
                {
                    _vDrainSent = true;
                    return;
                }
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }
        }
    }

    private AVFrame* ResolveWorkVideoFrame()
    {
        var fmt = (AVPixelFormat)_vFrame->format;

        if (_drmGpuNv12Path && fmt == AVPixelFormat.AV_PIX_FMT_DRM_PRIME)
            return _vFrame;

        if (_d3d11GpuNv12Path &&
            fmt is AVPixelFormat.AV_PIX_FMT_D3D11 or AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD)
            return _vFrame;

        if (_hwAccel != null && fmt == _hwAccel.HwAccelPixFmt)
            return _hwAccel.TransferToScratch(_vFrame);
        return _vFrame;
    }

    private void SyncVideoPixelFormatIfNeeded(AVFrame* workFrame)
    {
        if (_drmGpuNv12Path || _d3d11GpuNv12Path)
            return;

        var effective = (AVPixelFormat)workFrame->format;
        if (effective == _vSrcPixFmt)
            return;

        _vSrcPixFmt = effective;
        var mapped = VideoFileDecoder.MapNativePixelFormat(_vSrcPixFmt);
        _vNativePixFmt = mapped != PixelFormat.Unknown ? mapped : PixelFormat.Bgra32;
        _vNativePixFormats = mapped != PixelFormat.Unknown ? [mapped] : [];

        var desiredOutput = _vOutPixFmt;
        _vOutPixFmt = PixelFormat.Unknown;
        SelectVideoOutputFormatLocked(desiredOutput);
    }

    private void SelectVideoOutputFormatLocked(PixelFormat format)
    {
        if (format == PixelFormat.Unknown)
            throw new ArgumentException("cannot select Unknown pixel format", nameof(format));

        // SeekPresentation primes one frame with the previous output pixel format; negotiation
        // (SelectOutputFormat) can change the sink path afterward — drop the stale prime.
        if (format != Video.Format.PixelFormat)
        {
            _vPrimedAfterSeek?.Dispose();
            _vPrimedAfterSeek = null;
        }

        if (_drmGpuNv12Path)
        {
            if (format is not (PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.P016))
                throw new NotSupportedException(
                    "DRM PRIME zero-copy decoding only supports PixelFormat.Nv12, P010, or P016 output matching the GPU-exported semi-planar dma-bufs.");
            if (_vOutPixFmt == format)
                return;
            _vPassThrough = true;
            ReleaseSws();
            _vOutPixFmt = format;
            Video.Format = Video.Format with { PixelFormat = format };
            return;
        }

        if (_d3d11GpuNv12Path)
        {
            if (format != PixelFormat.Nv12)
                throw new NotSupportedException(
                    "D3D11 shared-handle zero-copy decoding only supports PixelFormat.Nv12 output matching the DXGI NV12 texture export.");
            if (_vOutPixFmt == format)
                return;
            _vPassThrough = true;
            ReleaseSws();
            _vOutPixFmt = format;
            Video.Format = Video.Format with { PixelFormat = format };
            return;
        }

        if (format == _vOutPixFmt) return;

        if (_hwAccel != null && _vSrcPixFmt == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            _vOutPixFmt = format;
            Video.Format = Video.Format with { PixelFormat = format };
            return;
        }

        if (format == _vNativePixFmt)
        {
            ReleaseSws();
            _vPassThrough = true;
        }
        else
        {
            var avTarget = FfmpegVideoPixelMaps.ToAvPixelFormat(format)
                ?? throw new NotSupportedException($"pixel format {format} has no FFmpeg mapping");
            ReleaseSws();
            _swsCtx = sws_getCachedContext(null,
                _vCtx->width, _vCtx->height, _vSrcPixFmt,
                Video.Format.Width, Video.Format.Height, avTarget,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, $"sws_getCachedContext for {format} returned NULL");
            _vPassThrough = false;
        }

        _vOutPixFmt = format;
        Video.Format = Video.Format with { PixelFormat = format };
    }

    private TimeSpan ResolveVideoPts(AVFrame* frame)
    {
        var pts = frame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE) pts = frame->pts;
        if (pts == AV_NOPTS_VALUE)
        {
            var fps = Video.Format.FrameRate.ToDouble();
            return fps > 0
                ? TimeSpan.FromSeconds(_vFramesEmitted / fps)
                : TimeSpan.Zero;
        }
        return PtsToTimeSpanVideo(pts);
    }

    private VideoFrame BuildVideoFrame(AVFrame* work, AVColorTransferCharacteristic trc)
    {
        var pts = ResolveVideoPts(work);
        Video.Position = pts;
        _vFramesEmitted++;

        if (_drmGpuNv12Path)
            return BuildNv12DrmDmabufGpuFrame(work, pts, trc);

        if (_d3d11GpuNv12Path)
            return BuildNv12D3D11SharedGpuFrame(work, pts, trc);

        return _vPassThrough
            ? BuildPassThroughVideoFrame(work, pts, trc)
            : BuildConvertedVideoFrame(work, pts, trc);
    }

    private VideoFrame BuildNv12DrmDmabufGpuFrame(AVFrame* drmFrame, TimeSpan pts, AVColorTransferCharacteristic trc) =>
        VideoFileDecoder.CreateVideoFrameFromLinuxDrmPrimeFrame(drmFrame, pts, Video.Format, trc);

    private VideoFrame BuildNv12D3D11SharedGpuFrame(AVFrame* d3dFrame, TimeSpan pts, AVColorTransferCharacteristic trc)
    {
        var backing = D3D11VaNv12BackingFactory.TryCreateBacking(d3dFrame, _win32Nv12SharedHandleOnly);
        if (backing == null)
            throw new InvalidOperationException(
                "D3D11 frame could not be exported to NT shared handles. Disable decoder option RetainD3D11SharedHandleForGl or try a CPU upload path.");

        return VideoFrame.CreateNv12Win32Shared(pts, Video.Format, backing, VideoFileDecoder.MapTransferHint(trc));
    }

    private VideoFrame BuildPassThroughVideoFrame(AVFrame* work, TimeSpan pts, AVColorTransferCharacteristic trc)
    {
        var clone = av_frame_alloc();
        if (clone == null) throw new OutOfMemoryException("av_frame_alloc (clone) returned NULL");
        var ret = av_frame_ref(clone, work);
        if (ret < 0)
        {
            var c = clone;
            av_frame_free(&c);
            FFmpegException.ThrowIfError(ret, nameof(av_frame_ref));
        }

        var planeCount = PixelFormatInfo.PlaneCount(_vOutPixFmt);
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
        var hint = VideoFileDecoder.MapTransferHint(trc);
        for (var i = 0; i < planeCount; i++)
        {
            var rawStride = clone->linesize[(uint)i];
            var absStride = Math.Abs(rawStride);
            var planeH = PixelFormatInfo.PlaneHeight(_vOutPixFmt, Video.Format.Height, i);
            var ptr = clone->data[(uint)i];
            if (rawStride < 0)
                ptr += rawStride * (planeH - 1);
            planes[i] = new UnmanagedMemoryManager<byte>(ptr, absStride * planeH).Memory;
            strides[i] = absStride;
        }

        var clonePtr = (nint)clone;
        return new VideoFrame(pts, Video.Format, planes, strides, hint, release: () =>
        {
            var f = (AVFrame*)clonePtr;
            av_frame_free(&f);
            _passThroughArena.Return(in passHandle);
        });
    }

    private VideoFrame BuildConvertedVideoFrame(AVFrame* work, TimeSpan pts, AVColorTransferCharacteristic trc)
    {
        var width = Video.Format.Width;
        var height = Video.Format.Height;
        // sws_scale's srcSliceH is the SOURCE slice height — must match the decoder's actual frame
        // height even when we're upscaling to even output dimensions (attached_pic / odd-dim path).
        var srcHeight = _vCtx->height;
        var bytesPerPixel = VideoFileDecoder.BytesPerPackedPixel(_vOutPixFmt);
        if (bytesPerPixel == 0)
            throw new NotSupportedException(
                $"sws conversion to {_vOutPixFmt} (multi-plane / non-packed) is not implemented");

        var stride = width * bytesPerPixel;
        var contiguous = stride * height;
        var rented = ArrayPool<byte>.Shared.Rent(contiguous);
        var dstMem = rented.AsMemory(0, contiguous);

        _swSrcLines[0] = work->data[0];
        _swSrcLines[1] = work->data[1];
        _swSrcLines[2] = work->data[2];
        _swSrcLines[3] = work->data[3];
        _swSrcLines[4] = work->data[4];
        _swSrcLines[5] = work->data[5];
        _swSrcLines[6] = work->data[6];
        _swSrcLines[7] = work->data[7];
        _swSrcStride[0] = work->linesize[0];
        _swSrcStride[1] = work->linesize[1];
        _swSrcStride[2] = work->linesize[2];
        _swSrcStride[3] = work->linesize[3];
        _swSrcStride[4] = work->linesize[4];
        _swSrcStride[5] = work->linesize[5];
        _swSrcStride[6] = work->linesize[6];
        _swSrcStride[7] = work->linesize[7];

        fixed (byte* dstPtr = dstMem.Span)
        {
            _swDstLines[0] = dstPtr;
            for (var i = 1; i < 8; i++) _swDstLines[i] = null;
            Array.Clear(_swDstStride);
            _swDstStride[0] = stride;

            var sret = sws_scale(_swsCtx, _swSrcLines, _swSrcStride, 0, srcHeight, _swDstLines, _swDstStride);
            if (sret < 0) FFmpegException.ThrowIfError(sret, nameof(sws_scale));
        }

        var hint = VideoFileDecoder.MapTransferHint(trc);
        byte[] pooled = rented;
        return new VideoFrame(pts, Video.Format, dstMem, stride, hint,
            release: () => ArrayPool<byte>.Shared.Return(pooled));
    }

    private void ConsumeDecoderUntilPts(TimeSpan targetPresentationTime)
    {
        // Guard against pathological timestamp / demux states that would spin forever.
        const int maxIterations = 2_000_000;
        var iterations = 0;
        while (true)
        {
            if (++iterations > maxIterations)
            {
                throw new InvalidOperationException(
                    "MediaContainerSharedDemux.SeekPresentation: ConsumeDecoderUntilPts exceeded iteration guard — " +
                    "aborting to avoid an unbounded hang (check mux timestamps vs target PTS).");
            }

            var ret = avcodec_receive_frame(_vCtx, _vFrame);
            if (ret == AVERROR_EOF)
            {
                _vEof = true;
                return;
            }
            if (ret == AVERROR(EAGAIN))
            {
                FeedVideoFromQueue();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));

            var workFrame = ResolveWorkVideoFrame();
            SyncVideoPixelFormatIfNeeded(workFrame);
            var trc = _vFrame->color_trc;
            var pts = ResolveVideoPts(workFrame);
            if (pts >= targetPresentationTime)
            {
                _vPrimedAfterSeek = BuildVideoFrame(workFrame, trc);
                av_frame_unref(_vFrame);
                return;
            }

            av_frame_unref(_vFrame);
        }
    }

    internal sealed class AudioTrack : IAudioSource, ISeekableSource, IDisposable
    {
        private readonly MediaContainerSharedDemux _o;

        internal AudioTrack(MediaContainerSharedDemux owner) => _o = owner;

        public AudioFormat Format { get; internal set; } = default!;
        public string CodecName => _o.AudioCodecName;
        public TimeSpan Duration => _o.Duration;
        public TimeSpan Position { get; internal set; }
        public bool IsAtEnd => !_o._hasAudio || _o._aEof;
        public bool IsExhausted => !_o._hasAudio || (_o._aEof && _o._aDrainedTail);

        public int ReadInto(Span<float> dst)
        {
            ObjectDisposedException.ThrowIf(_o._disposed, this);
            if (!_o._hasAudio) return 0;
            if (dst.Length % Format.Channels != 0)
                throw new ArgumentException(
                    $"destination length {dst.Length} is not a multiple of channel count {Format.Channels}", nameof(dst));

            lock (_o._audioDecodeLock)
            {
                _o.ThrowIfDisposedUnsafe();
                if (IsExhausted) return 0;

                var written = 0;
                while (written < dst.Length)
                {
                    var remainingFrames = (dst.Length - written) / Format.Channels;
                    if (remainingFrames == 0) break;

                    var drained = _o.SwrConvertInto(dst[written..], remainingFrames, null, 0);
                    if (drained > 0)
                    {
                        written += drained * Format.Channels;
                        _o._aSamplesEmitted += drained;
                    }
                    if (drained == remainingFrames) continue;

                    if (_o._aEof)
                    {
                        if (drained == 0)
                        {
                            _o._aDrainedTail = true;
                            break;
                        }
                        continue;
                    }

                    if (!_o.TryReceiveAudioFrame()) continue;

                    var capacity = (dst.Length - written) / Format.Channels;
                    var produced = _o.SwrConvertInto(dst[written..], capacity, _o._aFrame->extended_data, _o._aFrame->nb_samples);
                    if (produced > 0)
                    {
                        written += produced * Format.Channels;
                        _o._aSamplesEmitted += produced;
                    }
                    av_frame_unref(_o._aFrame);
                }

                if (written > 0)
                    Position = TimeSpan.FromSeconds((double)_o._aSamplesEmitted / Format.SampleRate);
                return written;
            }
        }

        public bool TryReadNextFrame(out AudioFrame frame)
        {
            ObjectDisposedException.ThrowIf(_o._disposed, this);
            if (!_o._hasAudio)
            {
                frame = default;
                return false;
            }
            lock (_o._audioDecodeLock)
            {
                _o.ThrowIfDisposedUnsafe();
                if (!_o.TryReceiveAudioFrame())
                {
                    frame = default;
                    return false;
                }
                frame = _o.ConvertAudioFrame();
                av_frame_unref(_o._aFrame);
                return true;
            }
        }

        public void Seek(TimeSpan position) => _o.SeekPresentation(position);

        public void Dispose() { }
    }

    internal sealed class VideoTrack : IVideoSource, ISeekableSource, IHardwareD3D11GlInteropSource,
        ICooperativeVideoReadInterrupt, IDisposable
    {
        private readonly MediaContainerSharedDemux _o;

        internal VideoTrack(MediaContainerSharedDemux owner) => _o = owner;

        void ICooperativeVideoReadInterrupt.RequestYieldBetweenReads() => _o.RequestVideoDecodeYield();

        void ICooperativeVideoReadInterrupt.ClearYieldRequest() => _o.ClearVideoDecodeYield();

        public VideoFormat Format { get; internal set; } = default!;
        public string CodecName => _o.VideoCodecName;
        public TimeSpan Duration => _o.Duration;
        public TimeSpan Position { get; internal set; }
        public bool IsAtEnd => !_o._hasVideo || _o._vEof;
        public bool IsExhausted => !_o._hasVideo || _o._vEof;
        public IReadOnlyList<PixelFormat> NativePixelFormats => _o._vNativePixFormats;

        public void SelectOutputFormat(PixelFormat format)
        {
            ObjectDisposedException.ThrowIf(_o._disposed, this);
            if (format == PixelFormat.Unknown)
                throw new ArgumentException("cannot select Unknown pixel format", nameof(format));

            if (!_o._hasVideo)
            {
                // No real video stream — just record the negotiated output format on the stub so the
                // sink's Configure(Format) call sees a coherent VideoFormat. The decode path is inert.
                Format = Format with { PixelFormat = format };
                _o._vOutPixFmt = format;
                return;
            }

            lock (_o._videoDecodeLock)
            {
                _o.ThrowIfDisposedUnsafe();
                _o.SelectVideoOutputFormatLocked(format);
            }
        }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            ObjectDisposedException.ThrowIf(_o._disposed, this);
            if (!_o._hasVideo)
            {
                frame = null!;
                return false;
            }
            lock (_o._videoDecodeLock)
            {
                _o.ThrowIfDisposedUnsafe();

                if (Volatile.Read(ref _o._videoDecodeYieldRequested) != 0)
                {
                    frame = null!;
                    return false;
                }

                if (_o._vPrimedAfterSeek is { } primed)
                {
                    frame = primed;
                    _o._vPrimedAfterSeek = null;
                    return true;
                }

                while (true)
                {
                    if (Volatile.Read(ref _o._videoDecodeYieldRequested) != 0)
                    {
                        frame = null!;
                        return false;
                    }

                    var ret = avcodec_receive_frame(_o._vCtx, _o._vFrame);
                    if (ret == 0)
                    {
                        var workFrame = _o.ResolveWorkVideoFrame();
                        _o.SyncVideoPixelFormatIfNeeded(workFrame);
                        var trc = _o._vFrame->color_trc;
                        frame = _o.BuildVideoFrame(workFrame, trc);
                        av_frame_unref(_o._vFrame);
                        return true;
                    }
                    if (ret == AVERROR_EOF)
                    {
                        _o._vEof = true;
                        frame = null!;
                        return false;
                    }
                    if (ret == AVERROR(EAGAIN))
                    {
                        _o.FeedVideoFromQueue();
                        continue;
                    }
                    FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
                }
            }
        }

        public void Seek(TimeSpan position) => _o.SeekPresentation(position);

        public void Dispose() { }

        public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr) =>
            _o.TryGetHardwareD3D11DeviceForWin32Gl(out deviceComPtr);

        public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked) =>
            _o.TryGetHardwareD3D11AdapterLuid(out adapterLuidPacked);
    }
}
