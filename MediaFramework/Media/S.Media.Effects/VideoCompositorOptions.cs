using Silk.NET.OpenGL;

namespace S.Media.Effects;

/// <summary>Optional knobs for <see cref="VideoCompositor.Create"/>.</summary>
public sealed class VideoCompositorOptions
{
    /// <summary>CPU compositor sampling when <see cref="VideoCompositorBackend"/> resolves to CPU.</summary>
    public CompositorSamplingMode CpuSampling { get; init; } = CompositorSamplingMode.Bilinear;

    /// <summary>
    /// Silk GL instance for the GL backend. If omitted, <see cref="VideoCompositorBackend.Gl"/> uses a registered
    /// host compositor backend when one is available.
    /// </summary>
    public GL? Gl { get; init; }

    /// <summary>GL compositor FBO / readback precision. Default <see cref="GlCompositorOutputPrecision.Rgba8"/>.</summary>
    public GlCompositorOutputPrecision GlOutputPrecision { get; init; } = GlCompositorOutputPrecision.Rgba8;

    /// <summary>
    /// Per-call compositor backend factories, tried in order for <see cref="VideoCompositorBackend.Auto"/>
    /// and <see cref="VideoCompositorBackend.Gl"/> <em>before</em> the process-wide ones registered via
    /// <see cref="VideoCompositor.RegisterAutoBackend"/>. Lets a session supply its own backend without
    /// mutating process-wide state (which can otherwise leak across sessions/tests).
    /// </summary>
    public IReadOnlyList<VideoCompositorBackendFactory>? AutoBackends { get; init; }
}
