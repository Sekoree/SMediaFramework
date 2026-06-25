using Silk.NET.OpenGL;

namespace S.Media.Compositor;

/// <summary>
/// A custom GL layer that renders directly into the compositor's canvas FBO — the "3D object layer" plugin
/// seam (Doc 05). It runs in the compositor's GL context, on the compositor's render thread (same-context
/// only — never a cross-process frame). Mirrors the C-ABI <c>MfpLayerSurfaceVTable</c>
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
