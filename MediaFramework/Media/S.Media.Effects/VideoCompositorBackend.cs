namespace S.Media.Effects;

/// <summary>Which <see cref="IVideoCompositor"/> implementation <see cref="VideoCompositor.Create"/> selects.</summary>
public enum VideoCompositorBackend
{
    /// <summary>CPU when no GL context is available; otherwise GL.</summary>
    Auto = 0,

    /// <summary><see cref="CpuVideoCompositor"/> — BGRA32 software reference.</summary>
    Cpu = 1,

    /// <summary><see cref="OpenGL.GlVideoCompositor"/> — requires a current GL context on the consumer thread.</summary>
    Gl = 2,
}
