namespace S.Media.Compositor;

/// <summary>Which <see cref="IVideoCompositor"/> implementation <see cref="VideoCompositor.Create"/> selects.</summary>
public enum VideoCompositorBackend
{
    /// <summary>Use the first registered host GPU backend when available; otherwise fall back to CPU.</summary>
    Auto = 0,

    /// <summary><see cref="CpuVideoCompositor"/> - BGRA32 software reference.</summary>
    Cpu = 1,

    /// <summary>
    /// Use <see cref="OpenGL.GlVideoCompositor"/> with a supplied GL instance, or a registered host GL backend.
    /// </summary>
    Gl = 2,
}
