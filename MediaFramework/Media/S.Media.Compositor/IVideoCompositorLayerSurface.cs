using Silk.NET.OpenGL;

namespace S.Media.Compositor;

/// <summary>
/// A custom GL layer that renders directly into the compositor's canvas FBO - the "3D object layer" plugin
/// seam (Doc 05). It runs in the compositor's GL context, on the compositor's render thread (same-context
/// only - never a cross-process frame). Mirrors the C-ABI <c>MfpLayerSurfaceVTable</c>
/// (<c>configure_gl</c> / <c>render</c> / <c>destroy</c>) so a native plugin adapts onto this interface.
/// </summary>
public interface IVideoCompositorLayerSurface : IDisposable
{
    /// <summary>Configure (or reconfigure) for the canvas the surface will draw into. Called on the
    /// compositor thread with its context current, before the first <see cref="Render"/>.</summary>
    void ConfigureGl(GL gl, VideoFormat canvas);

    /// <summary>
    /// Render this layer into <paramref name="targetFbo"/> (the bound canvas framebuffer) at
    /// <paramref name="masterTime"/>, applying the layer <paramref name="transform"/> and
    /// <paramref name="opacity"/>. Runs on the compositor thread with its context current.
    /// </summary>
    void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity);
}

/// <summary>
/// Optional companion for a layer surface that owns objects tied to the compositor's GL context.
/// <see cref="IVideoCompositorLayerSurface.Dispose"/> may run on a control thread and should only stop
/// logical use; the GL compositor calls <see cref="ReleaseGl"/> on its owner thread with the context
/// current before destroying that context.
/// </summary>
public interface IVideoCompositorGlResource
{
    void ReleaseGl(GL gl);
}

/// <summary>
/// A layer-surface placed in a composite: the GL-rendering <see cref="IVideoCompositorLayerSurface"/> plus
/// its destination <see cref="Transform"/> and <see cref="Opacity"/>. Surface layers render on top of the
/// frame layers, in list order, directly into the compositor's canvas (no intermediate frame) - unless
/// <see cref="Effects"/> is non-empty, in which case the host renders the surface into an intermediate
/// canvas-sized texture and composites that through the per-layer effect chain (chroma key etc.), the
/// same shader path frame layers use.
/// </summary>
public readonly record struct CompositorSurfaceLayer(
    IVideoCompositorLayerSurface Surface,
    LayerTransform2D Transform,
    float Opacity,
    IReadOnlyList<VideoLayerEffect>? Effects = null,
    IReadOnlyList<WarpSection>? MappingSections = null);

/// <summary>
/// Capability interface for compositors that can host <see cref="CompositorSurfaceLayer"/>s (NXT-10 -
/// layer surfaces as a first-class compositor citizen). Callers discover support with a type test instead
/// of hard-coding a backend; the CPU compositor deliberately does NOT implement it (the surface contract
/// renders through a live GL context), so a surface-producing source falls back to its CPU frame path
/// there. The host is responsible for calling <see cref="IVideoCompositorLayerSurface.ConfigureGl"/> on
/// its render thread before a surface's first <see cref="IVideoCompositorLayerSurface.Render"/> and again
/// after every canvas reconfigure.
/// </summary>
public interface IVideoCompositorSurfaceHost : IVideoCompositor
{
    /// <summary>
    /// Composite <paramref name="frameLayers"/> (back-to-front), then render
    /// <paramref name="surfaceLayers"/> on top (list order) directly into the canvas, and return the
    /// finished frame at <paramref name="presentationTime"/>.
    /// </summary>
    VideoFrame CompositeWithSurfaces(
        IReadOnlyList<CompositorLayer> frameLayers,
        IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
        TimeSpan presentationTime);
}

/// <summary>
/// A video source that can ALSO render itself as a compositor layer surface (GPU-side, no CPU frame) -
/// e.g. a 3D renderer whose software raster is only a fallback. When the target composition's compositor
/// is an <see cref="IVideoCompositorSurfaceHost"/>, the session asks for a surface via
/// <see cref="CreateLayerSurface"/> and does NOT attach a frame output for the placement; the source may
/// then skip full-frame rasterization (its <c>TryReadNextFrame</c> should stay cheap - transport/priming
/// may still pull frames). On a CPU-only compositor the source is consumed through its normal frame path.
/// </summary>
public interface ILayerSurfaceVideoSource
{
    /// <summary>Creates the surface that will render this source's content. Called at most once per
    /// playback; the caller owns the surface's lifetime (disposed with the layer).</summary>
    IVideoCompositorLayerSurface CreateLayerSurface();
}
