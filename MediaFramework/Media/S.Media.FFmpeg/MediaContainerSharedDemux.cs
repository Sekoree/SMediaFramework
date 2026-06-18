using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core;
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
/// <c>Rent</c>/<c>Return</c> run under the normal video decode serialization for this demux — there is no extra mutex around the arena itself.
/// On teardown, selected managed disposals (<see cref="PassThroughDescriptorArena"/>, hardware acceleration, primed-seek holder) are wrapped so <strong>Debug</strong> builds log via <see cref="S.Media.Core.Diagnostics.MediaDiagnostics.LogError"/> and <strong>Release</strong> builds continue best-effort (same policy as <see cref="VideoRouter.Dispose"/>).
/// </remarks>
internal sealed unsafe partial class MediaContainerSharedDemux : IDisposable
{
    private const long AvTimeBase = 1_000_000L;
    private const int DefaultAudioPacketsQueued = 192;
    private const int DefaultVideoPacketsQueued = 384;
    private static readonly AVIOInterruptCB_callback InterruptCallbackDelegate = InterruptBlockingIo;
    private static readonly AVIOInterruptCB_callback_func InterruptCallback = InterruptCallbackDelegate;
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.FFmpeg.MediaContainerSharedDemux");

    /// <summary>Configured from <see cref="VideoDecoderOpenOptions"/>; tight defaults for sane streams, raise for HEVC 4K B-frame reorder.</summary>
    private int _maxAudioPacketsQueued = DefaultAudioPacketsQueued;
    private int _maxVideoPacketsQueued = DefaultVideoPacketsQueued;

    /// <summary><c>AV_DISPOSITION_ATTACHED_PIC</c> — album art / cover; must not be chosen over a real video track.</summary>
    private const int AvDispositionAttachedPic = 1024;

    private readonly object _lifecycleLock = new();
    private readonly Lock _audioDecodeLock = new();
    private readonly Lock _videoDecodeLock = new();
    private readonly ReaderWriterLockSlim _readSeekGate = new(LockRecursionPolicy.NoRecursion);
    private readonly object _queueGate = new();

    private volatile bool _disposed;
    private volatile bool _demuxerStopRequest;
    private int _videoDecodeYieldRequested;
    private volatile bool _fileReadCompleted;
    private volatile Exception? _demuxFault;
    private volatile bool _demuxerThreadStuck;
    private int _readYieldRequested;
    private int _seekGeneration;
    private Thread? _demuxerThread;
    private GCHandle _interruptHandle;
    private AVFormatContext* _fmt;
    private StreamAvioBridge? _streamIo;
    // A FileStream this demux opened itself for the large-buffer AVIO file path (FileReadBufferBytes > 0).
    // Unlike the public OpenStream contract (caller owns the stream), we own and dispose this one.
    private Stream? _ownedInputStream;
    private bool _inputSeekable = true;
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
    // Input parameters _swr was built for. Audio streams can change format mid-file (HE-AAC SBR,
    // DVB parameter changes); EnsureSwrMatchesDecodedAudioFrameLocked compares the live frame against
    // these and rebuilds swr instead of feeding it misinterpreted buffers.
    private AVSampleFormat _swrInSampleFmt;
    private int _swrInSampleRate;
    private AVChannelLayout _swrInChLayout;
    private bool _swrInChLayoutValid;
    private AVFrame* _aFrame;
    private bool _aEof;
    private bool _aDrainSent;
    private long _aSamplesEmitted;
    private bool _aDrainedTail;
    // After a seek the container lands on a video keyframe that can be a whole GOP before the requested
    // position; PrimeBothAfterSeekLocked advances audio (and video) forward to the exact target so they
    // resume together. _aHasBufferedFrame pushes the straddling "keeper" frame back so the next read emits
    // it instead of re-receiving (and skipping) it.
    private bool _aHasBufferedFrame;

    private int _vStream = -1;
    private bool _hasVideo;
    private bool _videoIsAttachedPicture;
    // Set once the single attached-picture cover frame has been emitted. Latches for the lifetime of the
    // demux (the cover packet lives only at the file head, so it cannot be re-decoded after a seek): the
    // VideoTrack then reports exhausted so the decode loop stops and nothing waits on a video buffer that
    // will never refill. The cover is held by the output (HaPlay single-frame/logo mode).
    private bool _vAttachedPicEmitted;
    private AVRational _vTb;
    private AVCodecContext* _vCtx;
    private SwsContext* _swsCtx;
    private AVFrame* _vFrame;
    private AVPixelFormat _vSrcPixFmt;
    // Source geometry the sws context (and pass-through plane math) was configured for. Streams can
    // change dimensions mid-file (DVB captures, spliced MKVs); SyncVideoPixelFormatIfNeeded compares the
    // decoded frame against these and rebuilds the conversion path instead of feeding sws mismatched
    // geometry (which corrupts or over-reads — sws trusts its build-time source dims).
    private int _vSrcW;
    private int _vSrcH;
    private PixelFormat _vNativePixFmt;
    private PixelFormat _vOutPixFmt;
    private PixelFormat[] _vNativePixFormats = [];
    private bool _vPassThrough;
    private long _vFramesEmitted;
    private bool _vEof;
    private bool _vDrainSent;
    private VideoFrame? _vPrimedAfterSeek;
    // Position the demux is currently primed at. A coordinated A/V seek calls SeekPresentation twice on
    // the same shared demux (audio track + video track, same position); when the freshly primed state has
    // not been consumed, the second call is deduplicated instead of repeating the avformat_seek + decode.
    private TimeSpan? _seekPrimedPosition;
    // True while the demux is sitting exactly where the last SeekPresentation left it and no read has
    // pulled past it yet. This — not the presence of a primed keeper frame — gates the coordinated-seek
    // dedup: a prime that bailed early (4 s deadline or a cancellation) still leaves the demux positioned
    // at the target, so the paired call must dedup rather than redo a full (and equally doomed) seek.
    // Cleared by the first audio/video read after the seek (and re-armed by the next SeekPresentation).
    private bool _seekPrimePending;
    // Set by CancelInFlightSeek to abort an over-long PrimeBothAfterSeekLocked; reset at the start of each
    // real seek. Reads never inspect it, so a stray set can never wedge playback (unlike the read yield).
    private int _seekCancelRequested;
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

    private MediaStreamInfo[] _streamInfos = [];

    /// <summary>All container streams (including not-elected ones) from probe metadata.</summary>
    public IReadOnlyList<MediaStreamInfo> Streams => _streamInfos;

    /// <summary>Container stream index of the elected audio stream, or −1 (no audio / disabled / degraded).</summary>
    public int ActiveAudioStreamIndex => _aStream;

    /// <summary>Container stream index of the elected video stream, or −1 (no video / disabled / degraded).</summary>
    public int ActiveVideoStreamIndex => _vStream;

    /// <summary>True when the container exposed a decodable audio stream — false for video-only files.</summary>
    public bool HasAudio => _hasAudio;

    /// <summary>True when the container exposed a decodable video stream — false for audio-only files
    /// (the <see cref="VideoTrack"/> is a stub that reports <c>IsExhausted = true</c> immediately).</summary>
    public bool HasVideo => _hasVideo;

    /// <summary>True when the chosen video stream is <c>AV_DISPOSITION_ATTACHED_PIC</c> (album cover art).</summary>
    public bool VideoIsAttachedPicture => _videoIsAttachedPicture;

    private MediaContainerSharedDemux()
    {
        Audio = new AudioTrack(this);
        Video = new VideoTrack(this);
        _interruptHandle = GCHandle.Alloc(this);
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

    internal static MediaContainerSharedDemux Open(
        Stream stream,
        bool isSeekable,
        string? probeHintName,
        VideoDecoderOpenOptions? videoOptions)
    {
        var d = new MediaContainerSharedDemux();
        try
        {
            d.OpenInternalFromStream(stream, isSeekable, probeHintName, videoOptions);
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
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.OpenFile", slowWarningMs: 1000);
        ApplyQueueDepthOptions(videoOptions);

        var fileReadBuffer = videoOptions?.FileReadBufferBytes ?? 0;
        if (fileReadBuffer > 0)
        {
            OpenInternalLargeBufferFile(path, fileReadBuffer, videoOptions);
            timing?.SetOutcome($"path={Path.GetFileName(path)} buffered={fileReadBuffer} audio={_hasAudio} video={_hasVideo}");
            return;
        }

        _inputSeekable = true;

        AVFormatContext* fmt = null;
        fmt = avformat_alloc_context();
        if (fmt == null)
            throw new OutOfMemoryException("avformat_alloc_context returned NULL.");
        InstallInterruptCallback(fmt);

        var ret = avformat_open_input(&fmt, path, null, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
        _fmt = fmt;

        ret = avformat_find_stream_info(_fmt, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));

        OpenInternalAfterFormatOpen(videoOptions);
        timing?.SetOutcome($"path={Path.GetFileName(path)} audio={_hasAudio} video={_hasVideo} duration={Duration}");
    }

    /// <summary>
    /// Opens a local file through a large-buffer custom AVIO (over a minimally-buffered <see cref="FileStream"/>)
    /// instead of FFmpeg's native file protocol, so each read pulls a big sequential block. Helps sustained
    /// throughput on high-per-IOP-latency media (USB / external drives) where ~32 KB native reads can't keep
    /// the single demux thread fed. The <see cref="FileStream"/> is owned and disposed by this demux.
    /// </summary>
    private void OpenInternalLargeBufferFile(string path, int ioBufferSize, VideoDecoderOpenOptions? videoOptions)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.OpenLargeBufferFile", slowWarningMs: 1000);
        // bufferSize:1 disables FileStream's own buffering so the large AVIO read maps to a large OS read;
        // SequentialScan hints the kernel readahead. The bridge does not own the stream — we do.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        _ownedInputStream = fs;
        _inputSeekable = fs.CanSeek;
        _streamIo = StreamAvioBridge.Create(fs, _inputSeekable, ioBufferSize);
        _fmt = _streamIo.OpenFormatContext(Path.GetFileName(path));
        InstallInterruptCallback(_fmt);
        OpenInternalAfterFormatOpen(videoOptions);
        timing?.SetOutcome($"path={Path.GetFileName(path)} ioBuffer={ioBufferSize} seekable={_inputSeekable} audio={_hasAudio} video={_hasVideo}");
    }

    private void OpenInternalFromStream(
        Stream stream,
        bool isSeekable,
        string? probeHintName,
        VideoDecoderOpenOptions? videoOptions)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.OpenStream", slowWarningMs: 1000);
        ArgumentNullException.ThrowIfNull(stream);
        ApplyQueueDepthOptions(videoOptions);
        _inputSeekable = isSeekable && stream.CanSeek;
        _streamIo = StreamAvioBridge.Create(stream, _inputSeekable);
        _fmt = _streamIo.OpenFormatContext(probeHintName);
        InstallInterruptCallback(_fmt);
        OpenInternalAfterFormatOpen(videoOptions);
        timing?.SetOutcome($"hint={probeHintName ?? "(none)"} seekable={_inputSeekable} audio={_hasAudio} video={_hasVideo}");
    }

    private void ApplyQueueDepthOptions(VideoDecoderOpenOptions? videoOptions)
    {
        var aDepth = videoOptions?.AudioPacketQueueDepth ?? 0;
        var vDepth = videoOptions?.VideoPacketQueueDepth ?? 0;
        if (aDepth < 0) throw new ArgumentOutOfRangeException(nameof(videoOptions), "AudioPacketQueueDepth must be >= 0");
        if (vDepth < 0) throw new ArgumentOutOfRangeException(nameof(videoOptions), "VideoPacketQueueDepth must be >= 0");
        if (aDepth > 0) _maxAudioPacketsQueued = aDepth;
        if (vDepth > 0) _maxVideoPacketsQueued = vDepth;
    }

    private void OpenInternalAfterFormatOpen(VideoDecoderOpenOptions? videoOptions)
    {
        int ret;
        _streamInfos = MediaStreamProbe.ReadAll(_fmt);

        AVStream* aSt = null;
        AVCodec* aCodec = null;
        _aStream = ElectAudioStream(videoOptions?.AudioStreamIndex, &aCodec);
        _hasAudio = _aStream >= 0 && aCodec != null;
        if (!_hasAudio)
            _aStream = -1;

        AVCodec* vCodec = null;
        _vStream = ElectVideoStream(videoOptions?.VideoStreamIndex, _aStream, &vCodec);
        _hasVideo = _vStream >= 0 && vCodec != null;
        if (!_hasVideo)
        {
            _vStream = -1;
            if (!_hasAudio)
                throw new FFmpegException(_vStream, "no decodable audio or video stream found");
        }

        if (_hasAudio)
        {
            aSt = _fmt->streams[_aStream];
            // Wrap audio-stream setup so a file with a usable VIDEO stream still opens (video-only) when
            // the audio stream is unusable — unsupported codec build, corrupt codec parameters, a channel
            // layout swr can't negotiate, etc. Mirrors the video-side degrade below; the catch only engages
            // when there IS video to fall back to (an audio-only file still surfaces the error).
            try
            {
                _aTb = aSt->time_base;
                AudioCodecName = Marshal.PtrToStringAnsi((IntPtr)aCodec->name) ?? "unknown";

                _aCtx = avcodec_alloc_context3(aCodec);
                if (_aCtx == null) throw new OutOfMemoryException("audio avcodec_alloc_context3 returned NULL");
                ret = avcodec_parameters_to_context(_aCtx, aSt->codecpar);
                FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));
                ret = avcodec_open2(_aCtx, aCodec, null);
                FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

                if (_aCtx->sample_rate <= 0 || _aCtx->ch_layout.nb_channels <= 0)
                    throw new FFmpegException(0,
                        $"audio stream reports unusable format (rate={_aCtx->sample_rate}, channels={_aCtx->ch_layout.nb_channels})");

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

                CaptureSwrInputConfig(&_aCtx->ch_layout, _aCtx->sample_fmt, _aCtx->sample_rate);
            }
            catch (Exception ex) when (_hasVideo)
            {
                // Playable video + unusable audio ⇒ degrade to video-only (warns) instead of failing the open.
                ConfigureNoAudioStubAfterAudioSetupFailure(ex.Message);
            }
        }

        if (!_hasAudio)
        {
            // Sentinel format for video-only files: AudioFormat(0, 0) — both fields zero so any
            // consumer that forgets to guard with HasAudio fails fast at AudioFormat.Validate
            // rather than silently latching onto a bogus 48000 Hz rate.
            AudioCodecName = "";
            Audio.Format = new AudioFormat(0, 0);
        }

        AVStream* vSt = null;
        if (!_hasVideo)
        {
            // No decodable video stream — audio-only file (e.g. MP3 with no cover art).
            // Provide a stub VideoFormat so format negotiation between the stub IVideoSource and a
            // permissive output (DiscardingVideoOutput, or any output with at least one accepted format)
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
            Trace.LogDebug("Open: streams audio={AudioStream}({AudioCodec}) video=none duration={Duration} queues=({AudioQ},{VideoQ})",
                _aStream, AudioCodecName, Duration, _maxAudioPacketsQueued, _maxVideoPacketsQueued);
            return;
        }

        vSt = _fmt->streams[_vStream];
        // Wrap video-stream setup so a file with a playable AUDIO stream still opens (audio-only) when the
        // video stream is unusable — unsupported cover/video codec, bad dimensions, sws failure, etc. Without
        // this the whole open throws and a perfectly good audio track (e.g. an album with a broken embedded
        // cover) won't play. The catch only engages when there IS audio to fall back to; a video-only file
        // still surfaces the error. (Body left at this indent to keep the change a pure wrap — see catch below.)
        try
        {
        _videoIsAttachedPicture = (vSt->disposition & AvDispositionAttachedPic) != 0;
        _vTb = vSt->time_base;
        VideoCodecName = Marshal.PtrToStringAnsi((IntPtr)vCodec->name) ?? "unknown";

        _vCtx = avcodec_alloc_context3(vCodec);
        if (_vCtx == null) throw new OutOfMemoryException("video avcodec_alloc_context3 returned NULL");
        ret = avcodec_parameters_to_context(_vCtx, vSt->codecpar);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));

        // Never hardware-decode an attached-picture cover (album art): it is a single still, so a GPU decode
        // session buys nothing and some drivers (e.g. VAAPI MJPEG) are slow/flaky for one-shot JPEG/PNG —
        // which stalls the pre-audio video-buffer wait for seconds and can leave the cover blank. Software
        // decode of one frame is effectively free.
        var tryHw = (videoOptions?.TryHardwareAcceleration ?? true) && !_videoIsAttachedPicture;
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
                // Real layout comes from av_hwframe_transfer_data (first frame). Do not advertise NV12
                // here: codecs such as ProRes transfer to YUV422P10LE, and pretending NV12 lets downstream
                // negotiation pick a format before we know what swscale will actually receive.
                _vSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
                _vNativePixFmt = PixelFormat.Unknown;
                _vPassThrough = false;
                _vOutPixFmt = PixelFormat.Bgra32;
                _vNativePixFormats = [];
            }
        }
        else
        {
            _vSrcPixFmt = _vCtx->pix_fmt;
            var nativeSoft = VideoFileDecoder.MapNativePixelFormat(_vSrcPixFmt);
            _vNativePixFmt = nativeSoft != PixelFormat.Unknown ? nativeSoft : PixelFormat.Bgra32;
            _vPassThrough = nativeSoft != PixelFormat.Unknown;
            _vOutPixFmt = _vNativePixFmt;
            // When the decoder's pixel format has no native mapping we sws-convert to BGRA32, so advertise
            // BGRA32 (what we actually emit) rather than nothing. Otherwise an even-dimension source whose
            // pixfmt is unmapped (e.g. a yuvj420p album cover) declares no formats and, against a permissive
            // sink that also declares none (DiscardingVideoOutput on the no-video-output path), the negotiator
            // throws "neither source nor output declared any pixel formats" and the file won't play.
            _vNativePixFormats = nativeSoft != PixelFormat.Unknown ? [nativeSoft] : [PixelFormat.Bgra32];
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

        _vSrcW = _vCtx->width;
        _vSrcH = _vCtx->height;

        // Some streams (notably attached_pic / album cover art) report odd dimensions. Pixel formats with
        // chroma subsampling (I420 / NV12 over NDI) require even W and H, and BGRA / RGBA / UYVY require
        // even W. Round up to the next even multiple here and route through the sws-to-BGRA32 path so
        // downstream outputs always see even dimensions. The visible difference is at most one row/column
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
            // Bgra32 is what we actually emit after the forced sws conversion. Reporting it as native lets
            // VideoFormatNegotiator find common ground when the output declares no accepted formats (e.g.
            // DiscardingVideoOutput in HaPlay's audio-only routing path for FLAC + attached_pic cover art).
            // Without this, an attached_pic source with odd dimensions and a permissive output would crash
            // with "neither source nor output declared any pixel formats".
            _vNativePixFormats = [PixelFormat.Bgra32];
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
        }
        catch (Exception ex) when (_hasAudio)
        {
            // Playable audio + unusable video ⇒ degrade to audio-only (warns) instead of failing the open.
            ConfigureNoVideoStubAfterVideoSetupFailure(aSt, ex.Message);
        }

        _demuxPkt = av_packet_alloc();
        if (_demuxPkt == null) throw new OutOfMemoryException("demux av_packet_alloc returned NULL");
        if (_hasAudio)
        {
            _aFrame = av_frame_alloc();
            if (_aFrame == null) throw new OutOfMemoryException("audio av_frame_alloc returned NULL");
        }
        if (_hasVideo)
        {
            _vFrame = av_frame_alloc();
            if (_vFrame == null) throw new OutOfMemoryException("video av_frame_alloc returned NULL");
        }

        StartDemuxerThread();
        Trace.LogDebug(
            "Open: streams audio={AudioStream}({AudioCodec}) video={VideoStream}({VideoCodec}) attachedPic={AttachedPic} hw={Hardware} drm={Drm} d3d11={D3D11} duration={Duration} queues=({AudioQ},{VideoQ})",
            _aStream,
            AudioCodecName,
            _vStream,
            VideoCodecName,
            _videoIsAttachedPicture,
            _hwAccel is not null,
            _drmGpuNv12Path,
            _d3d11GpuNv12Path,
            Duration,
            _maxAudioPacketsQueued,
            _maxVideoPacketsQueued);
    }

    /// <summary>
    /// After a partial video-stream setup throws on a file that has a usable audio stream, release the
    /// half-initialised video native state and reconfigure as a no-video (audio-only) container — the same
    /// stub state the no-decodable-video path produces — so the audio still plays. The chosen video stream is
    /// disowned (<c>_vStream = -1</c>) so the demux reader stops routing its packets.
    /// </summary>
    private void ConfigureNoVideoStubAfterVideoSetupFailure(AVStream* aSt, string reason)
    {
        MediaDiagnostics.LogWarning(
            $"MediaContainerSharedDemux: video stream unusable ({reason}); playing audio only.");

        // Release whatever video native state was allocated before the failure.
        ReleaseSws();
        if (_vCtx != null)
        {
            _hwAccel?.DetachFromCodec(_vCtx);
            var c = _vCtx;
            avcodec_free_context(&c);
            _vCtx = null;
        }
        MediaDiagnostics.SwallowDisposeErrors(() => _hwAccel?.Dispose(), "MediaContainerSharedDemux.DegradeAudioOnly: _hwAccel");
        _hwAccel = null;
        _drmGpuNv12Path = false;
        _d3d11GpuNv12Path = false;

        // Reconfigure exactly like the no-decodable-video path (stub video source, never produces frames).
        _hasVideo = false;
        _vStream = -1;
        _videoIsAttachedPicture = false;
        VideoCodecName = "";
        _vSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        _vNativePixFmt = PixelFormat.Bgra32;
        _vPassThrough = false;
        _vOutPixFmt = PixelFormat.Bgra32;
        _vNativePixFormats = [PixelFormat.Bgra32];
        Video.Format = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

        if (Duration == default)
        {
            if (aSt is not null && aSt->duration > 0)
                Duration = PtsToTimeSpanAudio(aSt->duration);
            else if (_fmt->duration > 0)
                Duration = TimeSpan.FromMicroseconds(_fmt->duration);
        }
    }

    /// <summary>
    /// After a partial audio-stream setup throws on a file that has a usable video stream, release the
    /// half-initialised audio native state and reconfigure as a no-audio (video-only) container — the same
    /// stub state the no-decodable-audio path produces — so the video still plays. The chosen audio stream
    /// is disowned (<c>_aStream = -1</c>) so the demux reader stops routing its packets.
    /// </summary>
    private void ConfigureNoAudioStubAfterAudioSetupFailure(string reason)
    {
        MediaDiagnostics.LogWarning(
            $"MediaContainerSharedDemux: audio stream unusable ({reason}); playing video only.");

        if (_swr != null) { var s = _swr; swr_free(&s); _swr = null; }
        ReleaseSwrInputConfig();
        if (_aCtx != null) { var c = _aCtx; avcodec_free_context(&c); _aCtx = null; }

        _hasAudio = false;
        _aStream = -1;
        AudioCodecName = "";
        Audio.Format = new AudioFormat(0, 0);
    }

    /// <summary>Records the input parameters <see cref="_swr"/> was built for so mid-stream drift can be detected.</summary>
    private void CaptureSwrInputConfig(AVChannelLayout* layout, AVSampleFormat sampleFmt, int sampleRate)
    {
        ReleaseSwrInputConfig();
        fixed (AVChannelLayout* dst = &_swrInChLayout)
        {
            var ret = av_channel_layout_copy(dst, layout);
            FFmpegException.ThrowIfError(ret, nameof(av_channel_layout_copy));
        }
        _swrInChLayoutValid = true;
        _swrInSampleFmt = sampleFmt;
        _swrInSampleRate = sampleRate;
    }

    private void ReleaseSwrInputConfig()
    {
        if (!_swrInChLayoutValid) return;
        fixed (AVChannelLayout* dst = &_swrInChLayout)
            av_channel_layout_uninit(dst);
        _swrInChLayoutValid = false;
    }

    /// <summary>
    /// Audio streams can change parameters mid-file (HE-AAC SBR rate doubling on some decoder builds,
    /// DVB captures, spliced files). <see cref="_swr"/> is configured once at open; feeding it a frame
    /// with a different sample format / rate / channel layout misinterprets the input buffers. Detect the
    /// drift on the decoded frame and rebuild swr from the frame's parameters, keeping the OUTPUT fixed at
    /// <see cref="AudioTrack.Format"/> so the downstream router graph never has to renegotiate.
    /// Caller holds <c>_audioDecodeLock</c> and a decoded frame in <see cref="_aFrame"/>.
    /// </summary>
    private void EnsureSwrMatchesDecodedAudioFrameLocked()
    {
        var fmt = (AVSampleFormat)_aFrame->format;
        var rate = _aFrame->sample_rate;
        if (fmt == AVSampleFormat.AV_SAMPLE_FMT_NONE || rate <= 0 || _aFrame->ch_layout.nb_channels <= 0)
            return; // frame metadata incomplete — trust the existing configuration

        bool layoutMatches;
        fixed (AVChannelLayout* cfg = &_swrInChLayout)
            layoutMatches = _swrInChLayoutValid && av_channel_layout_compare(&_aFrame->ch_layout, cfg) == 0;
        if (layoutMatches && fmt == _swrInSampleFmt && rate == _swrInSampleRate)
            return;

        MediaDiagnostics.LogWarning(
            $"MediaContainerSharedDemux: audio stream parameters changed mid-stream " +
            $"(fmt {_swrInSampleFmt}→{fmt}, rate {_swrInSampleRate}→{rate}, channels →{_aFrame->ch_layout.nb_channels}); " +
            $"rebuilding resampler — output stays {Audio.Format}.");

        if (_swr != null) { var s = _swr; swr_free(&s); _swr = null; }

        AVChannelLayout outLayout;
        av_channel_layout_default(&outLayout, Audio.Format.Channels);
        SwrContext* swr = null;
        var ret = swr_alloc_set_opts2(&swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Audio.Format.SampleRate,
            &_aFrame->ch_layout, fmt, rate,
            0, null);
        av_channel_layout_uninit(&outLayout);
        FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));
        _swr = swr;
        ret = swr_init(_swr);
        FFmpegException.ThrowIfError(ret, nameof(swr_init));

        CaptureSwrInputConfig(&_aFrame->ch_layout, fmt, rate);
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

    /// <summary>
    /// Elects the audio stream honoring an explicit host selection. <c>null</c> = automatic
    /// (<c>av_find_best_stream</c>); <see cref="MediaStreamSelection.Disabled"/> = no audio at all; an
    /// invalid explicit index warns and falls back to automatic so a stale persisted track choice can
    /// never make a file unplayable.
    /// </summary>
    private int ElectAudioStream(int? requested, AVCodec** decoderRet)
    {
        *decoderRet = null;
        if (requested == MediaStreamSelection.Disabled)
            return -1;

        if (requested is { } idx && idx >= 0)
        {
            if (TryUseExplicitStream(idx, AVMediaType.AVMEDIA_TYPE_AUDIO, decoderRet))
                return idx;
            MediaDiagnostics.LogWarning(
                $"MediaContainerSharedDemux: requested audio stream #{idx} is not a decodable audio stream; falling back to automatic selection.");
        }

        return av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, decoderRet, 0);
    }

    /// <summary>Video-side counterpart of <see cref="ElectAudioStream"/>; explicit picks still go through
    /// the sane-dimension guard so a broken explicit choice degrades the same way a broken automatic one does.</summary>
    private int ElectVideoStream(int? requested, int relatedAudioStream, AVCodec** decoderRet)
    {
        *decoderRet = null;
        if (requested == MediaStreamSelection.Disabled)
            return -1;

        if (requested is { } idx && idx >= 0)
        {
            if (TryUseExplicitStream(idx, AVMediaType.AVMEDIA_TYPE_VIDEO, decoderRet)
                && VideoCodecparDimensionsLookSane(_fmt->streams[idx]->codecpar))
                return idx;
            *decoderRet = null;
            MediaDiagnostics.LogWarning(
                $"MediaContainerSharedDemux: requested video stream #{idx} is not a usable video stream; falling back to automatic selection.");
        }

        return PickVideoStreamIndex(_fmt, relatedAudioStream, decoderRet);
    }

    private bool TryUseExplicitStream(int index, AVMediaType type, AVCodec** decoderRet)
    {
        if (index < 0 || index >= _fmt->nb_streams)
            return false;
        var st = _fmt->streams[index];
        if (st->codecpar->codec_type != type)
            return false;
        var codec = avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
            return false;
        *decoderRet = codec;
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
            // outputs with SDK-paced video clocks (NDI's clockVideo:true paces NDIlib_send_send_video_async_v2
            // at the declared rate) would then block sender threads for one second per frame — making prime
            // / hold-toggle / cover-art appearance take 10+ seconds in practice. Declare 30 FPS so the SDK
            // paces at ~33 ms per send; we still only emit a single decoded frame, receivers just hold it.
            return new Rational(30, 1);

        if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0)
            return new Rational(30, 1);

        // Cap at 300 so genuine high-fps content (144/240) keeps its declared rate; rates beyond that are
        // container/timebase noise (e.g. an MKV r_frame_rate of 1000/1), not real frame cadence.
        if (d < 0.05 || d > 300.0)
            return new Rational(30, 1);

        if (den >= 5000 && d < 2.0)
            return new Rational(30, 1);

        return r;
    }

    /// <summary>Non-null after the background demux thread faulted (I/O / parse error). Consumers then
    /// observe EOF and exhaust gracefully instead of the process crashing; check this to distinguish a
    /// demux fault from a clean end of file.</summary>
    public Exception? DemuxFault => _demuxFault;

    private void StartDemuxerThread()
    {
        if (_demuxerThreadStuck)
            throw new InvalidOperationException(
                "Cannot start demuxer: a previous demux thread is still blocked in av_read_frame. Dispose this decoder and create a new one.");
        _demuxerStopRequest = false;
        _fileReadCompleted = false;
        _demuxFault = null;
        _demuxerThread = new Thread(DemuxerThreadProc)
        {
            IsBackground = true,
            Name = "MediaContainerSharedDemux.Read",
        };
        _demuxerThread.Start();
        Trace.LogTrace("StartDemuxerThread: started (audioStream={AudioStream}, videoStream={VideoStream})", _aStream, _vStream);
    }

    private bool StopDemuxerAndDrainQueues()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.StopDemuxer", slowWarningMs: 1000);
        _demuxerStopRequest = true;
        lock (_queueGate)
            Monitor.PulseAll(_queueGate);

        if (_demuxerThread is { } t)
        {
            if (!t.Join(TimeSpan.FromSeconds(4)))
            {
                _demuxerThreadStuck = true;
                _fileReadCompleted = true;
                lock (_queueGate)
                    Monitor.PulseAll(_queueGate);
                var ex = new TimeoutException("demux thread did not exit within the stop timeout");
                MediaDiagnostics.LogError(
                    ex,
                    "MediaContainerSharedDemux.StopDemuxerAndDrainQueues: demux thread is stuck; native demux state will not be restarted or freed");
                NativeResourceHealth.ReportStuck(
                    nameof(MediaContainerSharedDemux),
                    "FFmpeg demux thread",
                    $"audioQ={_audioPacketQ.Count} videoQ={_videoPacketQ.Count}",
                    TimeSpan.FromSeconds(4),
                    ex);
                timing?.SetOutcome($"stuck audioQ={_audioPacketQ.Count} videoQ={_videoPacketQ.Count}");
                return false;
            }
            _demuxerThread = null;
        }

        _demuxerStopRequest = false;
        FreeAllQueuedPacketsLocked();
        timing?.SetOutcome("stopped");
        return true;
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

    private void AdvanceSeekGeneration()
    {
        lock (_queueGate)
        {
            unchecked { _seekGeneration++; }
            Monitor.PulseAll(_queueGate);
        }
    }

    private void DemuxerThreadProc()
    {
        try
        {
            while (true)
            {
                if (_demuxerStopRequest)
                    return;

                av_packet_unref(_demuxPkt);
                var ret = av_read_frame(_fmt, _demuxPkt);
                if (ret == AVERROR_EXIT && _demuxerStopRequest)
                    return;
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
                    EnqueuePacketCopy(_audioPacketQ, _maxAudioPacketsQueued);
                else if (_demuxPkt->stream_index == _vStream)
                    EnqueuePacketCopy(_videoPacketQ, _maxVideoPacketsQueued);
            }
        }
        catch (Exception ex)
        {
            // The demux thread must never crash the host. Record a terminal fault and signal EOF so
            // blocked consumers (audio/video tracks waiting on _queueGate) wake, drain, and exhaust
            // gracefully. Hosts can distinguish a fault from clean EOF via DemuxFault.
            _demuxFault = ex;
            MediaDiagnostics.LogError(ex, "MediaContainerSharedDemux.DemuxerThreadProc faulted");
            lock (_queueGate)
            {
                _fileReadCompleted = true;
                Monitor.PulseAll(_queueGate);
            }
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
        if (!_inputSeekable)
            throw new NotSupportedException("cannot seek a non-seekable stream-backed container");

        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.SeekPresentation", slowWarningMs: 1000);
        RequestReadYield();
        _readSeekGate.EnterWriteLock();
        try
        {
            ClearReadYield();
            lock (_lifecycleLock)
            {
                ThrowIfDisposedUnsafe();

                // Coordinated A/V seek seeks this shared demux twice (audio then video) at the same target.
                // If no read has consumed the freshly primed state yet, the demux is already exactly where
                // the second call wants it — skip the repeat avformat_seek + decode-to-target. Keyed on a
                // "primed, not yet consumed" flag rather than the keeper frames so a prime that bailed early
                // (deadline / cancellation) still dedups instead of redoing an equally-doomed full seek.
                if (_seekPrimedPosition == position && _seekPrimePending)
                {
                    timing?.SetOutcome($"target={position} deduped");
                    return;
                }

                // Fresh seek: clear any leftover cancel request so a prior cancellation can't abort it.
                Volatile.Write(ref _seekCancelRequested, 0);

                if (!StopDemuxerAndDrainQueues())
                    throw new InvalidOperationException(
                        "Cannot seek: the demux thread did not stop, so the shared FFmpeg demux state cannot be safely reused.");
                AdvanceSeekGeneration();
                timing?.Checkpoint("demuxer stopped", LogLevel.Trace);

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
                timing?.Checkpoint("container seek completed", LogLevel.Trace);

                av_packet_unref(_demuxPkt);

                lock (_audioDecodeLock)
                {
                    if (_hasAudio)
                    {
                        avcodec_flush_buffers(_aCtx);
                        av_frame_unref(_aFrame);
                        swr_close(_swr);
                        ret = swr_init(_swr);
                        FFmpegException.ThrowIfError(ret, nameof(swr_init));
                    }

                    _aEof = false;
                    _aDrainSent = false;
                    _aDrainedTail = false;
                    _aHasBufferedFrame = false;
                    // Provisional; corrected to the real keeper-frame start by PrimeBothAfterSeekLocked, which
                    // advances audio forward to the exact target (the container landed on the video keyframe).
                    _aSamplesEmitted = _hasAudio ? (long)(position.TotalSeconds * Audio.Format.SampleRate) : 0;
                    Audio.Position = position;
                }

                lock (_videoDecodeLock)
                {
                    if (_hasVideo)
                    {
                        avcodec_flush_buffers(_vCtx);
                        av_frame_unref(_vFrame);
                    }

                    _vEof = false;
                    _vDrainSent = false;
                    _vPrimedAfterSeek?.Dispose();
                    _vPrimedAfterSeek = null;
                    // Re-anchor the no-PTS fallback video counter to the seek target so streams
                    // without container timestamps resume at ~position (matches ResolveVideoPts)
                    // instead of continuing from a stale pre-seek frame index.
                    var vFallbackFps = Video.Format.FrameRate.ToDouble();
                    _vFramesEmitted = vFallbackFps > 0 ? (long)Math.Round(position.TotalSeconds * vFallbackFps) : 0;
                    Video.Position = position;
                }

                _fileReadCompleted = false;
                StartDemuxerThread();
                timing?.Checkpoint("demuxer restarted", LogLevel.Trace);

                // Advance both decoders to the exact target in one interleaved, bounded pass so audio and
                // video resume together (not at the keyframe). Both decode locks are held: no reader can be
                // active under the write gate, so taking them together here is contention-free.
                lock (_videoDecodeLock)
                lock (_audioDecodeLock)
                    PrimeBothAfterSeekLocked(position);
                timing?.Checkpoint("prime completed", LogLevel.Trace);

                // Record where the demux is now primed so the paired (audio/video) call of a coordinated
                // seek to the same target can dedup. The first real read clears _seekPrimePending, so a
                // later genuine reseek to the same position still runs in full.
                _seekPrimedPosition = position;
                _seekPrimePending = true;
                timing?.SetOutcome($"target={position} audio={Audio.Position} video={ResolveVideoAlignmentPosition()}");
            }
        }
        finally
        {
            ClearReadYield();
            _readSeekGate.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        RequestReadYield();
        _readSeekGate.EnterWriteLock();
        try
        {
            ClearReadYield();
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;

                var canFreeNativeState = StopDemuxerAndDrainQueues();
                if (!canFreeNativeState)
                    return;

                MediaDiagnostics.SwallowDisposeErrors(() => _vPrimedAfterSeek?.Dispose(), "MediaContainerSharedDemux.Dispose: _vPrimedAfterSeek");
                _vPrimedAfterSeek = null;

                if (_demuxPkt != null) { var p = _demuxPkt; av_packet_free(&p); _demuxPkt = null; }
                if (_aFrame != null) { var f = _aFrame; av_frame_free(&f); _aFrame = null; }
                if (_vFrame != null) { var f = _vFrame; av_frame_free(&f); _vFrame = null; }

                if (_swr != null) { var s = _swr; swr_free(&s); _swr = null; }
                ReleaseSwrInputConfig();
                ReleaseSws();

                if (_aCtx != null) { var c = _aCtx; avcodec_free_context(&c); _aCtx = null; }
                if (_vCtx != null)
                {
                    _hwAccel?.DetachFromCodec(_vCtx);
                    var c = _vCtx;
                    avcodec_free_context(&c);
                    _vCtx = null;
                }

                MediaDiagnostics.SwallowDisposeErrors(() => _hwAccel?.Dispose(), "MediaContainerSharedDemux.Dispose: _hwAccel");
                _hwAccel = null;

                MediaDiagnostics.SwallowDisposeErrors(_passThroughArena.Dispose, "MediaContainerSharedDemux.Dispose: _passThroughArena");

                if (_fmt != null)
                {
                    var f = _fmt;
                    f->pb = null;
                    avformat_close_input(&f);
                    _fmt = null;
                }

                _streamIo?.Dispose();
                _streamIo = null;
                MediaDiagnostics.SwallowDisposeErrors(() => _ownedInputStream?.Dispose(), "MediaContainerSharedDemux.Dispose: _ownedInputStream");
                _ownedInputStream = null;
                FreeInterruptHandle();
            }
        }
        finally
        {
            ClearReadYield();
            _readSeekGate.ExitWriteLock();
        }
    }

    private void InstallInterruptCallback(AVFormatContext* fmt)
    {
        fmt->interrupt_callback.callback = InterruptCallback;
        fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_interruptHandle);
    }

    private static int InterruptBlockingIo(void* opaque)
    {
        if (opaque == null)
            return 0;

        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        if (handle.Target is not MediaContainerSharedDemux demux)
            return 1;

        return demux._disposed || demux._demuxerStopRequest ? 1 : 0;
    }

    private void FreeInterruptHandle()
    {
        if (_interruptHandle.IsAllocated)
            _interruptHandle.Free();
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
        // A post-seek skip may have decoded the straddling frame and pushed it back so the next emit uses
        // it instead of receiving (and discarding) past it.
        if (_aHasBufferedFrame)
        {
            _aHasBufferedFrame = false;
            return true;
        }

        while (true)
        {
            if (IsReadYieldRequested)
                return false;

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
                if (IsReadYieldRequested)
                    return false;
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
            if (IsReadYieldRequested)
                return;

            AVPacket* pkt = null;
            var packetGeneration = 0;
            lock (_queueGate)
            {
                if (_aPendingPacket != nint.Zero)
                {
                    pkt = (AVPacket*)_aPendingPacket;
                    _aPendingPacket = nint.Zero;
                    packetGeneration = _seekGeneration;
                }
                else
                {
                    while (_audioPacketQ.Count == 0 && !_fileReadCompleted && !_demuxerStopRequest && !IsReadYieldRequested)
                        Monitor.Wait(_queueGate, 50);

                    if (!IsReadYieldRequested && _audioPacketQ.Count > 0)
                    {
                        pkt = (AVPacket*)_audioPacketQ.Dequeue();
                        packetGeneration = _seekGeneration;
                    }
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
                    lock (_queueGate)
                    {
                        if (IsReadYieldRequested || packetGeneration != _seekGeneration)
                            av_packet_free(&pkt);
                        else
                            _aPendingPacket = (nint)pkt;
                        Monitor.PulseAll(_queueGate);
                    }
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

    /// <summary>
    /// After a seek lands on a keyframe, advances BOTH decoders forward to <paramref name="target"/> in one
    /// interleaved pass: video is primed (<see cref="_vPrimedAfterSeek"/>) at the first frame ≥ target and
    /// audio is discarded up to the target with the straddling frame pushed back (<see cref="_aHasBufferedFrame"/>),
    /// so both streams resume together at the requested position instead of the (possibly whole-GOP-earlier)
    /// keyframe. The caller holds both <c>_videoDecodeLock</c> and <c>_audioDecodeLock</c>.
    /// </summary>
    /// <remarks>
    /// Draining only one stream (the old video-only catch-up) let the other queue fill and block the single
    /// demux thread, starving the stream being drained — on a large-GOP file that hung the seek while it held
    /// the read/seek write gate, and because the seek is not cancellable the hang orphaned this thread and
    /// deadlocked everything after it. Interleaving drains whichever queue has data, so the demux never blocks.
    /// A wall-clock deadline bounds a stalled/corrupt demux to a best-effort position rather than an unbounded
    /// hang. Bails on a yield request (a superseding seek/stop).
    /// </remarks>
    private void PrimeBothAfterSeekLocked(TimeSpan target)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MediaContainerSharedDemux.PrimeAfterSeek", slowWarningMs: 1000);
        // Attached-picture cover art is a single still for the whole file — there is no video packet at a seek
        // target to advance to. Trying would drain the entire remaining file looking for one (or hit the 12 s
        // deadline) on every seek/realign. Treat video as already primed; the cover was decoded once and is
        // held downstream (see _vAttachedPicEmitted / VideoTrack.IsExhausted).
        var videoDone = !_hasVideo || _videoIsAttachedPicture;
        var audioDone = !_hasAudio;
        if (videoDone && audioDone)
        {
            timing?.SetOutcome($"target={target} nothing-to-prime");
            return;
        }

        var deadline = Environment.TickCount64 + 12000;
        var audioSteps = 0;
        var videoSteps = 0;
        var waits = 0;
        while (!videoDone || !audioDone)
        {
            if (IsReadYieldRequested || Volatile.Read(ref _videoDecodeYieldRequested) != 0
                || Volatile.Read(ref _seekCancelRequested) != 0)
            {
                timing?.SetOutcome($"target={target} cancelled videoDone={videoDone} audioDone={audioDone} vSteps={videoSteps} aSteps={audioSteps} waits={waits}");
                Trace.LogDebug("PrimeAfterSeek: cancelled/yielded target={Target} videoDone={VideoDone} audioDone={AudioDone} vSteps={VideoSteps} aSteps={AudioSteps}",
                    target, videoDone, audioDone, videoSteps, audioSteps);
                return; // superseded by a newer seek / stop, or cancelled by the caller — keep the
                        // best-effort position; a later op (or the dedup) re-primes from its target
            }

            var progressed = false;
            if (!videoDone)
            {
                progressed |= TryAdvanceVideoTowardTarget(target, ref videoDone);
                if (progressed) videoSteps++;
            }
            if (!audioDone)
            {
                var before = progressed;
                progressed |= TryAdvanceAudioTowardTarget(target, ref audioDone);
                if (progressed && !before) audioSteps++;
                else if (progressed) audioSteps++;
            }

            if (videoDone && audioDone)
            {
                timing?.SetOutcome($"target={target} video={Video.Position} audio={Audio.Position} vSteps={videoSteps} aSteps={audioSteps} waits={waits}");
                return;
            }

            if (!progressed)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    int audioQ;
                    int videoQ;
                    bool fileComplete;
                    lock (_queueGate)
                    {
                        audioQ = _audioPacketQ.Count;
                        videoQ = _videoPacketQ.Count;
                        fileComplete = _fileReadCompleted;
                    }
                    timing?.SetOutcome($"target={target} deadline videoDone={videoDone} audioDone={audioDone} vSteps={videoSteps} aSteps={audioSteps} waits={waits}");
                    Trace.LogWarning(
                        "PrimeAfterSeek: deadline reached for target={Target} (videoDone={VideoDone}, audioDone={AudioDone}, audioQ={AudioQ}, videoQ={VideoQ}, fileComplete={FileComplete}, vSteps={VideoSteps}, aSteps={AudioSteps})",
                        target,
                        videoDone,
                        audioDone,
                        audioQ,
                        videoQ,
                        fileComplete,
                        videoSteps,
                        audioSteps);
                    return; // demux stalled — keep the best-effort position rather than hang the (uncancellable) seek
                }

                lock (_queueGate)
                {
                    var aEmpty = _audioPacketQ.Count == 0 && _aPendingPacket == nint.Zero;
                    var vEmpty = _videoPacketQ.Count == 0 && _vPendingPacket == nint.Zero;
                    if (aEmpty && vEmpty && !_fileReadCompleted && !_demuxerStopRequest && !IsReadYieldRequested)
                    {
                        waits++;
                        Monitor.Wait(_queueGate, 10);
                    }
                }
            }
        }
        timing?.SetOutcome($"target={target} video={Video.Position} audio={Audio.Position} vSteps={videoSteps} aSteps={audioSteps} waits={waits}");
    }

    /// <summary>One non-blocking step of the video seek catch-up: decodes one already-available video frame,
    /// priming <see cref="_vPrimedAfterSeek"/> and setting <paramref name="done"/> once a frame ≥ target (or
    /// EOF) is reached. Returns whether it made progress (decoded a frame or fed a packet).</summary>
    private bool TryAdvanceVideoTowardTarget(TimeSpan target, ref bool done)
    {
        var ret = avcodec_receive_frame(_vCtx, _vFrame);
        if (ret == AVERROR_EOF)
        {
            _vEof = true;
            done = true;
            return false;
        }
        if (ret == AVERROR(EAGAIN))
            return TryPumpVideoPacketNonBlocking(); // fed a packet → progress; nothing available → no progress
        FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));

        var workFrame = ResolveWorkVideoFrame();
        SyncVideoPixelFormatIfNeeded(workFrame);
        var meta = ExtractVideoMetadata(_vFrame);
        var pts = ResolveVideoPts(workFrame);
        if (pts >= target)
        {
            _vPrimedAfterSeek = BuildVideoFrame(workFrame, meta);
            av_frame_unref(_vFrame);
            done = true;
            return true;
        }

        Video.Position = pts;
        _vFramesEmitted++;
        av_frame_unref(_vFrame);
        return true;
    }

    /// <summary>One non-blocking step of the audio seek catch-up: decodes one already-available audio frame,
    /// discarding it when wholly before the target or pushing it back as the keeper (<see cref="_aHasBufferedFrame"/>)
    /// and setting <paramref name="done"/> once the target (or EOF) is reached. Returns whether it made
    /// progress.</summary>
    private bool TryAdvanceAudioTowardTarget(TimeSpan target, ref bool done)
    {
        var ret = avcodec_receive_frame(_aCtx, _aFrame);
        if (ret == AVERROR_EOF)
        {
            _aEof = true;
            done = true;
            return false;
        }
        if (ret == AVERROR(EAGAIN))
            return TryPumpAudioPacketNonBlocking();
        FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));

        var pts = ResolveAudioPts();
        var srcRate = _aFrame->sample_rate > 0 ? _aFrame->sample_rate : Audio.Format.SampleRate;
        var frameEnd = pts + TimeSpan.FromSeconds((double)_aFrame->nb_samples / srcRate);
        if (frameEnd <= target)
        {
            av_frame_unref(_aFrame); // wholly before the target — drop it (swr is not fed, so no leftover)
            return true;
        }

        // This frame contains (or starts at/after) the target: emit it next. Anchor the emitted-sample
        // counter to its real start so Position is honest rather than the provisional seek estimate.
        _aHasBufferedFrame = true;
        _aSamplesEmitted = (long)Math.Round(pts.TotalSeconds * Audio.Format.SampleRate);
        done = true;
        return true;
    }

    /// <summary>
    /// Sends at most one already-queued/pending video packet (or an EOF drain) to the decoder without ever
    /// waiting. Returns <see langword="false"/> when nothing is available. Mirrors the packet-send half of
    /// <see cref="FeedVideoFromQueue"/> minus the blocking wait.
    /// </summary>
    private bool TryPumpVideoPacketNonBlocking()
    {
        AVPacket* pkt = null;
        var packetGeneration = 0;
        var fileComplete = false;
        lock (_queueGate)
        {
            if (_vPendingPacket != nint.Zero)
            {
                pkt = (AVPacket*)_vPendingPacket;
                _vPendingPacket = nint.Zero;
                packetGeneration = _seekGeneration;
            }
            else if (_videoPacketQ.Count > 0)
            {
                pkt = (AVPacket*)_videoPacketQ.Dequeue();
                packetGeneration = _seekGeneration;
            }
            else
            {
                fileComplete = _fileReadCompleted;
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
                return true;
            }
            if (ret == AVERROR(EAGAIN))
            {
                lock (_queueGate)
                {
                    if (IsReadYieldRequested || packetGeneration != _seekGeneration)
                        av_packet_free(&pkt);
                    else
                        _vPendingPacket = (nint)pkt;
                    Monitor.PulseAll(_queueGate);
                }
                return true;
            }
            av_packet_free(&pkt);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
            return false;
        }

        if (fileComplete && !_vDrainSent)
        {
            var ret = avcodec_send_packet(_vCtx, null);
            if (ret == 0 || ret == AVERROR(EAGAIN))
            {
                _vDrainSent = true;
                return true;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
        }

        return false;
    }

    /// <summary>
    /// Sends at most one already-queued/pending audio packet (or an EOF drain) to the decoder without ever
    /// waiting. Returns <see langword="false"/> when nothing is available. Mirrors the packet-send half of
    /// <see cref="FeedAudioFromQueue"/> minus the blocking wait.
    /// </summary>
    private bool TryPumpAudioPacketNonBlocking()
    {
        AVPacket* pkt = null;
        var packetGeneration = 0;
        var fileComplete = false;
        lock (_queueGate)
        {
            if (_aPendingPacket != nint.Zero)
            {
                pkt = (AVPacket*)_aPendingPacket;
                _aPendingPacket = nint.Zero;
                packetGeneration = _seekGeneration;
            }
            else if (_audioPacketQ.Count > 0)
            {
                pkt = (AVPacket*)_audioPacketQ.Dequeue();
                packetGeneration = _seekGeneration;
            }
            else
            {
                fileComplete = _fileReadCompleted;
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
                return true;
            }
            if (ret == AVERROR(EAGAIN))
            {
                lock (_queueGate)
                {
                    if (IsReadYieldRequested || packetGeneration != _seekGeneration)
                        av_packet_free(&pkt);
                    else
                        _aPendingPacket = (nint)pkt;
                    Monitor.PulseAll(_queueGate);
                }
                return true;
            }
            av_packet_free(&pkt);
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
            return false;
        }

        if (fileComplete && !_aDrainSent)
        {
            var ret = avcodec_send_packet(_aCtx, null);
            if (ret == 0 || ret == AVERROR(EAGAIN))
            {
                _aDrainSent = true;
                return true;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
        }

        return false;
    }

        private AudioFrame ConvertAudioFrame()
        {
        EnsureSwrMatchesDecodedAudioFrameLocked();
        var srcSamples = _aFrame->nb_samples;
        var outCapacity = (int)av_rescale_rnd(
            swr_get_delay(_swr, Audio.Format.SampleRate) + srcSamples,
            Audio.Format.SampleRate, Audio.Format.SampleRate, AVRounding.AV_ROUND_UP);

        var totalFloats = outCapacity * Audio.Format.Channels;
        var samples = ArrayPool<float>.Shared.Rent(totalFloats);
        int converted;
        fixed (float* outPtr = samples)
        {
            var outBuf = (byte*)outPtr;
            converted = swr_convert(_swr, &outBuf, outCapacity, _aFrame->extended_data, srcSamples);
        }
        if (converted < 0)
        {
            ArrayPool<float>.Shared.Return(samples);
            FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        }

        var pts = ResolveAudioPts();
        Audio.Position = pts + TimeSpan.FromSeconds((double)converted / Audio.Format.SampleRate);
        _aSamplesEmitted += converted;

        var owned = samples;
        // Idempotent single-shot Release: Interlocked guards double-Dispose from returning
        // the same buffer twice (AudioFrame's XML doc promises multi-call safety).
        var released = 0;
            return new AudioFrame(
                pts,
                Audio.Format,
                converted,
                samples.AsMemory(0, converted * Audio.Format.Channels),
                Release: DisposableRelease.Wrap(() =>
                {
                    if (Interlocked.Exchange(ref released, 1) == 0)
                        ArrayPool<float>.Shared.Return(owned, clearArray: false);
                }));
        }

        private bool TryDrainAudioTailFrame(out AudioFrame frame)
        {
            if (_aDrainedTail)
            {
                frame = default;
                return false;
            }

            var delay = swr_get_delay(_swr, Audio.Format.SampleRate);
            var outCapacity = (int)av_rescale_rnd(delay, Audio.Format.SampleRate, Audio.Format.SampleRate, AVRounding.AV_ROUND_UP);
            if (outCapacity <= 0)
            {
                _aDrainedTail = true;
                frame = default;
                return false;
            }

            var totalFloats = outCapacity * Audio.Format.Channels;
            var samples = ArrayPool<float>.Shared.Rent(totalFloats);
            int converted;
            fixed (float* outPtr = samples)
            {
                var outBuf = (byte*)outPtr;
                converted = swr_convert(_swr, &outBuf, outCapacity, null, 0);
            }
            if (converted < 0)
            {
                ArrayPool<float>.Shared.Return(samples);
                FFmpegException.ThrowIfError(converted, nameof(swr_convert));
            }

            if (converted == 0)
            {
                ArrayPool<float>.Shared.Return(samples);
                _aDrainedTail = true;
                frame = default;
                return false;
            }

            var pts = TimeSpan.FromSeconds((double)_aSamplesEmitted / Audio.Format.SampleRate);
            Audio.Position = pts + TimeSpan.FromSeconds((double)converted / Audio.Format.SampleRate);
            _aSamplesEmitted += converted;

            var owned = samples;
            var released = 0;
            frame = new AudioFrame(
                pts,
                Audio.Format,
                converted,
                samples.AsMemory(0, converted * Audio.Format.Channels),
                Release: DisposableRelease.Wrap(() =>
                {
                    if (Interlocked.Exchange(ref released, 1) == 0)
                        ArrayPool<float>.Shared.Return(owned, clearArray: false);
                }));
            return true;
        }

    internal void RequestVideoDecodeYield()
    {
        Volatile.Write(ref _videoDecodeYieldRequested, 1);
        lock (_queueGate)
            Monitor.PulseAll(_queueGate);
    }

    internal void ClearVideoDecodeYield() => Volatile.Write(ref _videoDecodeYieldRequested, 0);

    private bool IsReadYieldRequested =>
        Volatile.Read(ref _readYieldRequested) != 0;

    private void RequestReadYield()
    {
        Volatile.Write(ref _readYieldRequested, 1);
        lock (_queueGate)
            Monitor.PulseAll(_queueGate);
    }

    /// <summary>
    /// Cooperatively aborts an in-flight <see cref="SeekPresentation"/> whose decode-to-target prime is
    /// running long (e.g. a deep seek into a large GOP). The prime bails to its best-effort position and
    /// the seek returns. Safe to call at any time: the flag is reset at the start of each real seek and is
    /// never consulted by the read path, so a late call cannot wedge playback. Driven by the host's seek
    /// cancellation token via <see cref="MediaContainerDecoder.CancelInFlightSeek"/>.
    /// </summary>
    internal void CancelInFlightSeek()
    {
        Volatile.Write(ref _seekCancelRequested, 1);
        lock (_queueGate)
            Monitor.PulseAll(_queueGate); // wake a prime parked in Monitor.Wait(_queueGate, …)
    }

    /// <summary>
    /// After <see cref="SeekPresentation"/>, returns the timeline position both decoders have actually
    /// reached. When catch-up is incomplete, prefer <paramref name="fallback"/> (the requested seek
    /// target) if audio and video disagree by more than a quarter second — otherwise the clock would
    /// sit on a pre-target GOP keyframe while audio resumes at the keeper frame on the target.
    /// </summary>
    internal TimeSpan GetAlignedPresentationPosition(TimeSpan fallback)
    {
        if (_hasAudio && _hasVideo)
        {
            var audio = Audio.Position;
            var video = ResolveVideoAlignmentPosition();
            var spread = Math.Abs((audio - video).TotalSeconds);
            if (spread > 0.25)
                return fallback;
            return audio <= video ? audio : video;
        }

        if (_hasAudio)
            return Audio.Position;
        if (_hasVideo)
            return ResolveVideoAlignmentPosition();
        return fallback;
    }

    /// <summary>Video track position for clock alignment — uses the primed keeper frame when present.</summary>
    private TimeSpan ResolveVideoAlignmentPosition() =>
        _vPrimedAfterSeek?.PresentationTime ?? Video.Position;

    private void ClearReadYield() => Volatile.Write(ref _readYieldRequested, 0);

    private void FeedVideoFromQueue()
    {
        if (Volatile.Read(ref _videoDecodeYieldRequested) != 0 || IsReadYieldRequested)
            return;

        while (true)
        {
            if (Volatile.Read(ref _videoDecodeYieldRequested) != 0 || IsReadYieldRequested)
                return;

            AVPacket* pkt = null;
            var packetGeneration = 0;
            lock (_queueGate)
            {
                if (_vPendingPacket != nint.Zero)
                {
                    pkt = (AVPacket*)_vPendingPacket;
                    _vPendingPacket = nint.Zero;
                    packetGeneration = _seekGeneration;
                }
                else
                {
                    while (_videoPacketQ.Count == 0 && !_fileReadCompleted && !_demuxerStopRequest
                           && Volatile.Read(ref _videoDecodeYieldRequested) == 0 && !IsReadYieldRequested)
                        Monitor.Wait(_queueGate, 50);

                    if (!IsReadYieldRequested && _videoPacketQ.Count > 0)
                    {
                        pkt = (AVPacket*)_videoPacketQ.Dequeue();
                        packetGeneration = _seekGeneration;
                    }
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
                    lock (_queueGate)
                    {
                        if (IsReadYieldRequested || packetGeneration != _seekGeneration)
                            av_packet_free(&pkt);
                        else
                            _vPendingPacket = (nint)pkt;
                        Monitor.PulseAll(_queueGate);
                    }
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
        var w = workFrame->width;
        var h = workFrame->height;
        var dimsChanged = w > 0 && h > 0 && (w != _vSrcW || h != _vSrcH);

        if (effective == _vSrcPixFmt && !dimsChanged)
            return;

        if (dimsChanged)
        {
            // Mid-stream geometry change. The negotiated Video.Format canvas must stay stable for the
            // routing graph, so drop out of pass-through and sws-scale the new source geometry into the
            // existing canvas instead of emitting frames whose plane math no longer matches the format.
            MediaDiagnostics.LogWarning(
                $"MediaContainerSharedDemux: video source geometry changed mid-stream " +
                $"({_vSrcW}×{_vSrcH} {_vSrcPixFmt} → {w}×{h} {effective}); " +
                $"scaling into the negotiated {Video.Format.Width}×{Video.Format.Height} canvas.");
            _vSrcW = w;
            _vSrcH = h;
            _vSrcPixFmt = effective;
            var mappedNative = VideoFileDecoder.MapNativePixelFormat(effective);
            _vNativePixFmt = mappedNative != PixelFormat.Unknown ? mappedNative : PixelFormat.Bgra32;
            _vNativePixFormats = mappedNative != PixelFormat.Unknown ? [mappedNative] : [];
            var avTarget = FfmpegVideoPixelMaps.ToAvPixelFormat(_vOutPixFmt)
                ?? throw new NotSupportedException(
                    $"video geometry changed mid-stream but output format {_vOutPixFmt} has no FFmpeg mapping to scale into");
            ReleaseSws();
            _swsCtx = sws_getCachedContext(null,
                w, h, effective,
                Video.Format.Width, Video.Format.Height, avTarget,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);
            if (_swsCtx == null)
                throw new FFmpegException(0, "sws_getCachedContext for mid-stream geometry change returned NULL");
            _vPassThrough = false;
            return;
        }

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
        // (SelectOutputFormat) can change the output path afterward — drop the stale prime.
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
                _vSrcW, _vSrcH, _vSrcPixFmt,
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

    private VideoFrame BuildVideoFrame(AVFrame* work, VideoFrameMetadata meta)
    {
        var pts = ResolveVideoPts(work);
        Video.Position = pts;
        _vFramesEmitted++;
        if (_videoIsAttachedPicture)
            _vAttachedPicEmitted = true;

        if (_drmGpuNv12Path)
            return BuildNv12DrmDmabufGpuFrame(work, pts, meta);

        if (_d3d11GpuNv12Path)
            return BuildNv12D3D11SharedGpuFrame(work, pts, meta);

        return _vPassThrough
            ? BuildPassThroughVideoFrame(work, pts, meta)
            : BuildConvertedVideoFrame(work, pts, meta);
    }

    /// <summary>Reads transfer / color-space / range / field-order / S12M-timecode off <paramref name="source"/>.</summary>
    private VideoFrameMetadata ExtractVideoMetadata(AVFrame* source) => new(
        VideoFileDecoder.MapTransferHint(source->color_trc),
        VideoFileDecoder.MapColorSpace(source->colorspace),
        VideoFileDecoder.MapColorRange(source->color_range),
        VideoFileDecoder.MapFieldOrder(source),
        VideoFileDecoder.ReadS12mTimecode(source, Video.Format.FrameRate),
        VideoFileDecoder.MapAlphaMode((AVPixelFormat)source->format));

    private VideoFrame BuildNv12DrmDmabufGpuFrame(AVFrame* drmFrame, TimeSpan pts, VideoFrameMetadata meta) =>
        VideoFileDecoder.CreateVideoFrameFromLinuxDrmPrimeFrame(drmFrame, pts, Video.Format, meta);

    private VideoFrame BuildNv12D3D11SharedGpuFrame(AVFrame* d3dFrame, TimeSpan pts, VideoFrameMetadata meta)
    {
        var backing = D3D11VaNv12BackingFactory.TryCreateBacking(d3dFrame, _win32Nv12SharedHandleOnly);
        if (backing == null)
            throw new InvalidOperationException(
                "D3D11 frame could not be exported to NT shared handles. Disable decoder option RetainD3D11SharedHandleForGl or try a CPU upload path.");

        return VideoFrame.CreateNv12Win32Shared(pts, Video.Format, backing, meta);
    }

    private VideoFrame BuildPassThroughVideoFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta)
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
        return new VideoFrame(pts, Video.Format, planes, strides,
            release: DisposableRelease.Wrap(() =>
            {
                var f = (AVFrame*)clonePtr;
                av_frame_free(&f);
                _passThroughArena.Return(in passHandle);
            }),
            metadata: meta);
    }

    private VideoFrame BuildConvertedVideoFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta)
    {
        var width = Video.Format.Width;
        var height = Video.Format.Height;
        // sws_scale's srcSliceH is the SOURCE slice height — must match the decoder's actual frame
        // height even when we're upscaling to even output dimensions (attached_pic / odd-dim path).
        // _vSrcH tracks mid-stream geometry changes (SyncVideoPixelFormatIfNeeded runs before this).
        var srcHeight = _vSrcH;
        var bytesPerPixel = VideoFileDecoder.BytesPerPackedPixel(_vOutPixFmt);
        if (bytesPerPixel == 0)
        {
            // Multi-plane targets (Nv12 / Nv21 / I420). Mirror VideoFileDecoder.BuildConvertedPlanarFrame —
            // attached-picture cover-art branches need this path because their CPU-side conversion was
            // previously routed at the GPU layer; when the routing graph picks Nv12 (e.g. NDI branch with
            // pixel-format lock) and no GPU acceleration is in use, we must pack here too.
            if (_vOutPixFmt is PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.I420)
                return BuildConvertedPlanarVideoFrame(work, pts, meta, srcHeight);
            throw new NotSupportedException(
                $"sws conversion to {_vOutPixFmt} is not implemented — use BGRA32, NV12, I420, or a packed RGB layout.");
        }

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

        byte[] pooled = rented;
        return new VideoFrame(pts, Video.Format, dstMem, stride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(pooled)),
            metadata: meta);
    }

    /// <summary>
    /// Planar-target sws path (Nv12 / Nv21 / I420). Allocates one rented buffer per plane (sized via
    /// <see cref="PixelFormatInfo.PlanePitchBufferLength"/>), pins for the duration of sws_scale, then
    /// returns a <see cref="VideoFrame"/> whose release callback returns every plane to the pool.
    /// Mirrors <c>VideoFileDecoder.BuildConvertedPlanarFrame</c> — same allocation discipline so the
    /// attached-pic path doesn't diverge from the regular video file path.
    /// </summary>
    private VideoFrame BuildConvertedPlanarVideoFrame(AVFrame* work, TimeSpan pts, VideoFrameMetadata meta, int srcHeight)
    {
        var width = Video.Format.Width;
        var height = Video.Format.Height;
        var n = PixelFormatInfo.PlaneCount(_vOutPixFmt);

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

        var strides = new int[n];
        for (var i = 0; i < n; i++)
            strides[i] = PixelFormatInfo.PlaneByteWidth(_vOutPixFmt, width, i);

        var buffers = new byte[n][];
        // Transient pinning scratch (freed before return) — stack-allocate it; `allocated` tracks how
        // many were pinned so a mid-loop Rent failure frees exactly those.
        Span<GCHandle> handles = stackalloc GCHandle[n];
        var allocated = 0;
        try
        {
            for (var i = 0; i < n; i++)
            {
                var len = PixelFormatInfo.PlanePitchBufferLength(_vOutPixFmt, width, height, i, strides[i]);
                buffers[i] = ArrayPool<byte>.Shared.Rent(len);
                handles[i] = GCHandle.Alloc(buffers[i], GCHandleType.Pinned);
                allocated = i + 1;
            }

            for (var i = 0; i < n; i++)
                _swDstLines[i] = (byte*)handles[i].AddrOfPinnedObject();
            for (var i = n; i < 8; i++)
                _swDstLines[i] = null;

            Array.Clear(_swDstStride);
            for (var i = 0; i < n; i++)
                _swDstStride[i] = strides[i];

            var sret = sws_scale(_swsCtx, _swSrcLines, _swSrcStride, 0, srcHeight, _swDstLines, _swDstStride);
            if (sret < 0) FFmpegException.ThrowIfError(sret, nameof(sws_scale));
        }
        catch
        {
            for (var i = 0; i < allocated; i++)
                handles[i].Free();
            foreach (var b in buffers)
            {
                if (b is not null)
                    ArrayPool<byte>.Shared.Return(b);
            }
            throw;
        }

        for (var i = 0; i < n; i++)
            handles[i].Free();

        var memories = new ReadOnlyMemory<byte>[n];
        for (var i = 0; i < n; i++)
        {
            var len = PixelFormatInfo.PlanePitchBufferLength(_vOutPixFmt, width, height, i, strides[i]);
            memories[i] = buffers[i].AsMemory(0, len);
        }

        var captured = buffers;
        return new VideoFrame(pts, Video.Format, memories, strides,
            release: DisposableRelease.Wrap(() =>
            {
                foreach (var b in captured)
                    ArrayPool<byte>.Shared.Return(b);
            }),
            metadata: meta);
    }

}
