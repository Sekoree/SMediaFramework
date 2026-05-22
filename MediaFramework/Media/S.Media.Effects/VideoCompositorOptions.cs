using Silk.NET.OpenGL;

namespace S.Media.Effects;

/// <summary>Optional knobs for <see cref="VideoCompositor.Create"/>.</summary>
public sealed class VideoCompositorOptions
{
    /// <summary>CPU compositor sampling when <see cref="VideoCompositorBackend"/> resolves to CPU.</summary>
    public CompositorSamplingMode CpuSampling { get; init; } = CompositorSamplingMode.Bilinear;

    /// <summary>Silk GL instance for the GL backend. Required when backend is <see cref="VideoCompositorBackend.Gl"/>.</summary>
    public GL? Gl { get; init; }

    /// <summary>GL compositor FBO / readback precision. Default <see cref="GlCompositorOutputPrecision.Rgba8"/>.</summary>
    public GlCompositorOutputPrecision GlOutputPrecision { get; init; } = GlCompositorOutputPrecision.Rgba8;
}
