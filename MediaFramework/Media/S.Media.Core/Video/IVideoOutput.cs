namespace S.Media.Core.Video;

/// <summary>
/// Push-based video consumer. Outputs include displays (<see cref="S.Media.SDL3.SDL3GLVideoOutput"/>, <see cref="S.Media.Avalonia.VideoOpenGlControl"/>), file
/// muxers, and network senders (NDI). Multi-output hosts (for example <c>S.Media.Playback.MediaPlayer</c> with <see cref="S.Media.Core.Video.VideoRouter"/>) route frames to any implementation the app references.
/// </summary>
/// <remarks>
/// <para>
/// Pixel-format negotiation: <see cref="AcceptedPixelFormats"/> lists every
/// layout the output can consume <strong>without</strong> a CPU-side conversion
/// - for a GPU display that decodes YUV in a shader this means listing I420 /
/// NV12 / etc. directly. The list is ordered by preference; the
/// <see cref="VideoFormatNegotiator"/> picks the first entry the source can
/// also deliver natively.
/// </para>
/// <para>
/// <see cref="Configure"/> is called once after negotiation, before the first
/// <see cref="Submit"/>. Outputs allocate device-side resources (textures,
/// command queues, …) here.
/// </para>
/// </remarks>
public interface IVideoOutput
{
    /// <summary>The format negotiated for incoming frames. Valid after <see cref="Configure"/>.</summary>
    VideoFormat Format { get; }

    /// <summary>
    /// Pixel formats this output can render without CPU conversion, ordered
    /// best-to-worst. An empty list means the output will accept whatever it's
    /// configured with and convert internally - the convention holds both when
    /// the output is a router input's negotiation primary and when it is a
    /// fan-out branch (the branch then receives the negotiated format
    /// pass-through; see <see cref="VideoOutputFanoutFormats.PickBranchPixelFormat(VideoFormat, IReadOnlyList{PixelFormat}, Func{PixelFormat, PixelFormat, int, int, bool})"/>).
    /// </summary>
    IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; }

    /// <summary>
    /// Tell the output which format frames will arrive in. Call once after
    /// negotiation; throw if the output can't honor it.
    /// </summary>
    void Configure(VideoFormat format);

    /// <summary>
    /// Hand a frame to the output. The frame's <see cref="VideoFrame.Format"/>
    /// must equal the configured <see cref="Format"/>. The output takes ownership
    /// of the frame and is responsible for calling <see cref="VideoFrame.Dispose"/>
    /// once it's done with the underlying buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Thread contract:</strong> <c>Submit</c> is invoked on the clock-driver thread that
    /// fires <see cref="S.Media.Core.Clock.IMediaClock.VideoTick"/> (typically the audio callback
    /// or wall-clock pacer). It must return promptly - implementations that do real work (uploads,
    /// network encodes, file writes) should hand the frame off to a worker thread of their own.
    /// A slow <c>Submit</c> delays every other subscriber on the same clock.
    /// </para>
    /// <para>
    /// For slow outputs that cannot guarantee promptness, wrap with
    /// <see cref="S.Media.Core.Video.VideoOutputPump"/> (or register via
    /// <see cref="S.Media.Core.Video.VideoRouter.AddOutput"/> with
    /// <see cref="S.Media.Core.Video.VideoOutputPumpAttachOptions"/>) so submissions are
    /// queued and drained on a background thread.
    /// </para>
    /// </remarks>
    void Submit(VideoFrame frame);
}
