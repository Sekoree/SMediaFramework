using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Opens the same media file for <strong>both</strong> audio and video decoding with a single
/// host-facing object. <see cref="SeekPresentation"/> seeks both streams to the same timeline position.
/// </summary>
/// <remarks>
/// <para>
/// This façade always uses <see cref="MediaContainerSharedDemux"/>: one libav
/// <c>AVFormatContext</c>, a background demux thread with bounded per-stream packet queues, and
/// independent audio/video decode locks so producers can run on different threads. Video follows
/// <see cref="VideoDecoderOpenOptions"/>: hardware acceleration is attempted first (with software
/// fallback inside the shared video codec setup), including Linux DRM PRIME NV12 when
/// <see cref="VideoDecoderOpenOptions.RetainDmabufForGl"/> is enabled and Windows D3D11 NV12 shared handles when
/// <see cref="VideoDecoderOpenOptions.RetainD3D11SharedHandleForGl"/> is enabled.
/// </para>
/// </remarks>
public sealed class MediaContainerDecoder : IDisposable
{
    private readonly MediaContainerSharedDemux _shared;

    private MediaContainerDecoder(MediaContainerSharedDemux shared) => _shared = shared;

    /// <summary>Audio side — <see cref="ISeekableSource"/> for coordinated seeks.</summary>
    public IAudioSource Audio => _shared.Audio;

    /// <summary>Video side — <see cref="ISeekableSource"/>.</summary>
    public IVideoSource Video => _shared.Video;

    /// <summary>Always true for this implementation.</summary>
    public bool UsesSharedDemux => true;

    /// <summary>Reserved for API stability; always <c>null</c> (no dual <c>AVFormatContext</c> path).</summary>
    public AudioFileDecoder? LegacyAudio => null;

    /// <summary>Reserved for API stability; always <c>null</c>.</summary>
    public VideoFileDecoder? LegacyVideo => null;

    public static MediaContainerDecoder Open(string path, VideoDecoderOpenOptions? videoOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("media file not found", path);
        FFmpegRuntime.EnsureInitialized();

        var opt = videoOptions ?? new VideoDecoderOpenOptions();
        var shared = MediaContainerSharedDemux.Open(path, opt);
        return new MediaContainerDecoder(shared);
    }

    /// <summary>Seeks both streams to the same presentation timestamp.</summary>
    public void SeekPresentation(TimeSpan position) => _shared.SeekPresentation(position);

    /// <summary>When Windows D3D11VA NV12 shared-handle decode is active, libav's <c>ID3D11Device</c> COM pointer for GL upload.</summary>
    public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr) =>
        _shared.TryGetHardwareD3D11DeviceForWin32Gl(out deviceComPtr);

    public void Dispose() => _shared.Dispose();
}
