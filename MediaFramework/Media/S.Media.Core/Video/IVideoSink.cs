namespace S.Media.Core.Video;

/// <summary>
/// Push-based video consumer. Sinks include displays (<see cref="S.Media.SDL3.SDL3GLVideoSink"/>, <see cref="S.Media.Avalonia.VideoOpenGlControl"/>), file
/// muxers, and network senders (NDI). Multi-sink hosts (for example <c>S.Media.Playback.MediaPlayer</c> with <see cref="S.Media.FFmpeg.Video.VideoRouter"/>) route frames to any implementation the app references.
/// </summary>
/// <remarks>
/// <para>
/// Pixel-format negotiation: <see cref="AcceptedPixelFormats"/> lists every
/// layout the sink can consume <strong>without</strong> a CPU-side conversion
/// — for a GPU display that decodes YUV in a shader this means listing I420 /
/// NV12 / etc. directly. The list is ordered by preference; the
/// <see cref="VideoFormatNegotiator"/> picks the first entry the source can
/// also deliver natively.
/// </para>
/// <para>
/// <see cref="Configure"/> is called once after negotiation, before the first
/// <see cref="Submit"/>. Sinks allocate device-side resources (textures,
/// command queues, …) here.
/// </para>
/// </remarks>
public interface IVideoSink
{
    /// <summary>The format negotiated for incoming frames. Valid after <see cref="Configure"/>.</summary>
    VideoFormat Format { get; }

    /// <summary>
    /// Pixel formats this sink can render without CPU conversion, ordered
    /// best-to-worst. An empty list means the sink will accept whatever it's
    /// configured with and convert internally.
    /// </summary>
    IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; }

    /// <summary>
    /// Tell the sink which format frames will arrive in. Call once after
    /// negotiation; throw if the sink can't honor it.
    /// </summary>
    void Configure(VideoFormat format);

    /// <summary>
    /// Hand a frame to the sink. The frame's <see cref="VideoFrame.Format"/>
    /// must equal the configured <see cref="Format"/>. The sink takes ownership
    /// of the frame and is responsible for calling <see cref="VideoFrame.Dispose"/>
    /// once it's done with the underlying buffer.
    /// </summary>
    void Submit(VideoFrame frame);
}
