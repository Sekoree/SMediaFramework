using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;

namespace S.Media.Playback;

/// <summary>Decoder and audio-router flags for <see cref="MediaPlayer.Open(string)"/> builders — no output libraries.</summary>
public readonly record struct MediaPlayerOpenOptions(
    bool TryHardwareAcceleration = true,
    bool RetainDmabufForGl = false,
    bool RetainD3D11SharedHandleForGl = false,
    bool Win32Nv12SharedHandleOnlyExport = false,
    /// <summary>When true with <see cref="Win32Nv12SharedHandleOnlyExport"/>, decode open fails (Win32 handle-only needs a GL host device).</summary>
    bool WindowsD3d11ZeroHostGl = false,
    int AudioChunkSamples = 480,
    bool IncludeAudioRouter = true,
    /// <summary>Max demuxed audio packets buffered ahead of the audio decoder. <c>0</c> = use the demuxer default (192). Raise for HEVC 4K B-frame reorder.</summary>
    int AudioPacketQueueDepth = 0,
    /// <summary>Max demuxed video packets buffered ahead of the video decoder. <c>0</c> = use the demuxer default (384). Raise for HEVC 4K B-frame reorder.</summary>
    int VideoPacketQueueDepth = 0,
    /// <summary>AVIO read buffer for local-file opens, in bytes. <c>0</c> = FFmpeg's native ~32 KB file reads. Set 1–4 MB for big sequential reads on high-latency media (USB/external drives).</summary>
    int FileReadBufferBytes = 0,
    /// <summary><see cref="S.Media.Core.Video.VideoPlayer"/> decode queue depth for live opens. <c>0</c> = default (4).</summary>
    int LiveVideoDecodeQueueCapacity = 0,
    /// <summary>
    /// <see cref="S.Media.Core.Video.VideoPlayer"/> decode queue depth for file/decoder opens. <c>0</c> = default (16).
    /// This is the post-decode jitter buffer: file content has high per-frame decode-time variance
    /// (complex scenes, post-seek warmup), and the bare 4-frame player default drops ~25–40% of frames as
    /// "late" right after a seek. ~16 frames (~0.7 s @ 24 fps) lets decode pre-buffer during easy stretches
    /// and spend it during heavy ones. Raise for very high-variance 4K content (at a memory cost).
    /// </summary>
    int FileVideoDecodeQueueCapacity = 0,
    VideoPresentationMode LiveVideoPresentation = VideoPresentationMode.Scheduled,
    /// <summary>When true, <see cref="MediaPlayer.TryOpenStream"/> spools to disk instead of AVIO.</summary>
    bool SpoolStreamToDisk = false,
    /// <summary>When true (and not spooling), allows libav seek on the input stream.</summary>
    bool StreamIsSeekable = false,
    /// <summary>
    /// Per-session deinterlacer override. When null, consumers fall back to
    /// <see cref="S.Media.Core.Diagnostics.MediaFrameworkPlugins.VideoDeinterlacerFactory"/>,
    /// then the built-in Core fallback.
    /// </summary>
    Func<VideoFormat, IDeinterlacer>? VideoDeinterlacerFactory = null,
    /// <summary>Explicit audio stream index (<see cref="MediaStreamInfo.Index"/>). <c>null</c> = automatic;
    /// <see cref="MediaStreamSelection.Disabled"/> disables audio decode entirely. Invalid indices warn and
    /// fall back to automatic.</summary>
    int? AudioStreamIndex = null,
    /// <summary>Explicit video stream index, same semantics as <see cref="AudioStreamIndex"/>. Pass
    /// <see cref="MediaStreamSelection.Disabled"/> for audio-only playback of video files at zero
    /// video-decode cost (the player runs its stub video source).</summary>
    int? VideoStreamIndex = null)
{
    public MediaPlayerOpenOptions()
        : this(
            TryHardwareAcceleration: true,
            RetainDmabufForGl: false,
            RetainD3D11SharedHandleForGl: false,
            Win32Nv12SharedHandleOnlyExport: false,
            WindowsD3d11ZeroHostGl: false,
            AudioChunkSamples: 480,
            IncludeAudioRouter: true,
            AudioPacketQueueDepth: 0,
            VideoPacketQueueDepth: 0,
            FileReadBufferBytes: 0,
            LiveVideoDecodeQueueCapacity: 0,
            FileVideoDecodeQueueCapacity: 0,
            LiveVideoPresentation: VideoPresentationMode.Scheduled,
            SpoolStreamToDisk: false,
            StreamIsSeekable: false,
            VideoDeinterlacerFactory: null,
            AudioStreamIndex: null,
            VideoStreamIndex: null)
    {
    }

    /// <summary>Baseline: hardware decode on, GL decode flags off, standard audio chunking.</summary>
    public static MediaPlayerOpenOptions Default => new();

    /// <summary>Rejects the Win32 NV12 shared-handle-only + zero-host GL combination (smoke tool parity).</summary>
    public bool ValidateWin32Nv12Flags(out string? errorMessage)
    {
        errorMessage = null;
        if (Win32Nv12SharedHandleOnlyExport && WindowsD3d11ZeroHostGl)
        {
            errorMessage =
                "Win32 NV12 shared-handle-only export conflicts with zero-host GL (handle-only needs SDL D3D11GlInteropDeviceHost or a pre-bound negotiator device).";
            return false;
        }

        return true;
    }

    /// <summary>Maps to <see cref="VideoDecoderOpenOptions"/> for <see cref="MediaContainerDecoder.Open"/>.</summary>
    public VideoDecoderOpenOptions ToVideoDecoderOpenOptions() =>
        new()
        {
            TryHardwareAcceleration = TryHardwareAcceleration,
            RetainDmabufForGl = RetainDmabufForGl,
            RetainD3D11SharedHandleForGl = RetainD3D11SharedHandleForGl,
            Win32Nv12SharedHandleOnlyExport = Win32Nv12SharedHandleOnlyExport,
            AudioPacketQueueDepth = AudioPacketQueueDepth,
            VideoPacketQueueDepth = VideoPacketQueueDepth,
            FileReadBufferBytes = FileReadBufferBytes,
            AudioStreamIndex = AudioStreamIndex,
            VideoStreamIndex = VideoStreamIndex,
        };
}
