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
    /// <summary><see cref="S.Media.Core.Video.VideoPlayer"/> decode queue depth for live opens. <c>0</c> = default (4).</summary>
    int LiveVideoDecodeQueueCapacity = 0,
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
    Func<VideoFormat, IDeinterlacer>? VideoDeinterlacerFactory = null)
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
            LiveVideoDecodeQueueCapacity: 0,
            LiveVideoPresentation: VideoPresentationMode.Scheduled,
            SpoolStreamToDisk: false,
            StreamIsSeekable: false,
            VideoDeinterlacerFactory: null)
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
        };
}
