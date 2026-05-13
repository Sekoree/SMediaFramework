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
/// fallback inside the shared video codec setup), including Linux DRM PRIME semi-planar **NV12/P010** when
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

    /// <summary>
    /// Re-syncs libav decoders and the demuxer to the current mux playhead without changing the
    /// logical timeline: <see cref="SeekPresentation"/> at <c>max(</c><see cref="Video.Position"/><c>, </c>
    /// <see cref="Audio.Position"/><c>)</c>.
    /// </summary>
    /// <remarks>
    /// Call only when no concurrent discrete-frame reads are in flight (same contract as <see cref="SeekPresentation"/>).
    /// Prefer this name at pause boundaries when the intent is “drain internal delay” rather than “jump to a new time”.
    /// </remarks>
    public void FlushCodecPipelines()
    {
        var vp = Video is ISeekableSource vs ? vs.Position : TimeSpan.Zero;
        var ap = Audio is ISeekableSource ais ? ais.Position : TimeSpan.Zero;
        var resume = vp >= ap ? vp : ap;
        if (resume < TimeSpan.Zero)
            resume = TimeSpan.Zero;
        SeekPresentation(resume);
    }

    /// <summary>When Windows D3D11VA NV12 shared-handle decode is active, libav's <c>ID3D11Device</c> COM pointer for GL upload.</summary>
    public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr) =>
        _shared.TryGetHardwareD3D11DeviceForWin32Gl(out deviceComPtr);

    /// <summary>When Windows D3D11VA NV12 shared-handle decode is active, DXGI adapter LUID (packed) for diagnostics.</summary>
    public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked) =>
        _shared.TryGetHardwareD3D11AdapterLuid(out adapterLuidPacked);

    public void Dispose() => _shared.Dispose();
}
