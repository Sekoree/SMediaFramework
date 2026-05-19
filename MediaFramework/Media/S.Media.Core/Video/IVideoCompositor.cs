namespace S.Media.Core.Video;

/// <summary>
/// Combines N <see cref="CompositorLayer"/>s back-to-front into a single output <see cref="VideoFrame"/>.
/// Used by <see cref="CompositorVideoSink"/> to back picture-in-picture, lower-thirds, text overlays,
/// and transition effects.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations ship with the framework:
/// <list type="bullet">
/// <item><see cref="CpuVideoCompositor"/> — BGRA32 software reference; runs anywhere, no GPU context required.</item>
/// <item><c>S.Media.OpenGL.GlVideoCompositor</c> — GL 3.3 implementation that uploads each layer to a
/// texture and blends via shader into an FBO. The caller provides the GL context and must keep it current
/// on the thread that drives <see cref="Composite"/>.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Frame ownership:</strong> the returned <see cref="VideoFrame"/> is owned by the caller —
/// it carries a <c>release</c> callback that returns any pooled buffers when the frame is disposed.
/// Layer frames passed in remain owned by their submitter (typically a <see cref="CompositorVideoSink"/>
/// slot) and are not disposed by the compositor.
/// </para>
/// <para>
/// <strong>Format negotiation:</strong> all layer frames must use one of the pixel formats listed in
/// <see cref="AcceptedLayerPixelFormats"/>. <see cref="CompositorVideoSink"/> exposes this list on each
/// slot's <see cref="IVideoSink.AcceptedPixelFormats"/> so the upstream router can pick a compatible
/// branch format automatically.
/// </para>
/// </remarks>
public interface IVideoCompositor : IDisposable
{
    /// <summary>Output frame layout — width, height, pixel format, frame rate.</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>Pixel formats this compositor accepts on input layers, ordered best-to-worst.</summary>
    IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats { get; }

    /// <summary>
    /// Reconfigure the compositor for a new output format. Allocated per-output resources
    /// (FBOs, scratch buffers, etc.) are rebuilt; call before the first <see cref="Composite"/> when
    /// output dimensions change.
    /// </summary>
    void Configure(VideoFormat output);

    /// <summary>
    /// Composite <paramref name="layersBackToFront"/> into a fresh <see cref="VideoFrame"/> at
    /// <paramref name="presentationTime"/>. Layers are drawn in list order — first is the backdrop,
    /// later layers blend on top.
    /// </summary>
    /// <remarks>
    /// Implementations must not dispose any layer frame. The returned frame's <c>release</c> callback
    /// returns any pooled output buffer.
    /// </remarks>
    VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime);
}
