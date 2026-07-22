using System.Text.RegularExpressions;

namespace S.Media.Core.Video.Effects;

/// <summary>One shader parameter of a layer effect: a float vector uploaded as a uniform.</summary>
/// <param name="Name">Identifier referenced from the GLSL body as <c>$P(name)</c>. Must be a valid
/// GLSL identifier fragment (<c>[A-Za-z_][A-Za-z0-9_]*</c>).</param>
/// <param name="Components">Vector width 1-4 (float / vec2 / vec3 / vec4).</param>
public sealed record VideoLayerEffectParameter(string Name, int Components);

/// <summary>
/// Immutable definition of a per-layer video effect - the extension point for pixel effects that
/// run inside the compositor's layer pass (chroma key is the built-in first effect; plugins define
/// their own). An effect transforms the layer's sampled straight-alpha color before opacity /
/// blending, expressed twice:
/// <list type="bullet">
/// <item><see cref="GlslBody"/> - the body of a <c>vec4 apply(vec4 src)</c> GLSL function. GPU
/// compositors splice it into the composite fragment shader and compile one cached program variant
/// per distinct effect chain, so the effect costs nothing beyond its own math. Parameters are
/// referenced as <c>$P(name)</c>; the composer rewrites them to per-instance uniforms.</item>
/// <item><see cref="CpuKernelFactory"/> - optional scalar fallback for <c>CpuVideoCompositor</c>.
/// Effects without one are skipped on the CPU backend (GPU-only effects degrade to pass-through
/// rather than failing the composite).</item>
/// </list>
/// Descriptors are compared by <see cref="Id"/> when building shader-variant cache keys, so an Id
/// must uniquely identify the GLSL body: changing the body means changing the Id (append a version).
/// </summary>
public sealed class VideoLayerEffectDescriptor
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex ParamNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public VideoLayerEffectDescriptor(
        string id,
        string glslBody,
        IReadOnlyList<VideoLayerEffectParameter> parameters,
        Func<float[], IVideoLayerCpuEffect>? cpuKernelFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(glslBody);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!IdPattern.IsMatch(id))
            throw new ArgumentException(
                $"Effect id '{id}' must match [a-z0-9][a-z0-9._-]* (it becomes part of shader-cache keys).",
                nameof(id));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var p in parameters)
        {
            if (!ParamNamePattern.IsMatch(p.Name))
                throw new ArgumentException($"Effect '{id}' parameter '{p.Name}' is not a valid GLSL identifier.");
            if (p.Components is < 1 or > 4)
                throw new ArgumentException($"Effect '{id}' parameter '{p.Name}' has {p.Components} components; 1-4 supported.");
            if (!seen.Add(p.Name))
                throw new ArgumentException($"Effect '{id}' declares parameter '{p.Name}' twice.");
            total += p.Components;
        }

        Id = id;
        GlslBody = glslBody;
        Parameters = parameters;
        CpuKernelFactory = cpuKernelFactory;
        TotalComponents = total;
    }

    /// <summary>Stable unique id; part of the GPU program-variant cache key.</summary>
    public string Id { get; }

    /// <summary>Body of <c>vec4 apply(vec4 src)</c> (must <c>return</c> a vec4). <c>src</c> is the
    /// layer's straight-alpha color; parameters are referenced as <c>$P(name)</c>.</summary>
    public string GlslBody { get; }

    public IReadOnlyList<VideoLayerEffectParameter> Parameters { get; }

    /// <summary>Builds the CPU fallback kernel from the packed parameter values (declared order,
    /// <see cref="TotalComponents"/> floats). Null = GPU-only effect.</summary>
    public Func<float[], IVideoLayerCpuEffect>? CpuKernelFactory { get; }

    /// <summary>Total packed float count across <see cref="Parameters"/>.</summary>
    public int TotalComponents { get; }
}

/// <summary>
/// Scalar fallback kernel for one effect instance, used by <c>CpuVideoCompositor</c>.
/// Values are straight-alpha RGBA in [0, 1]; implementations must clamp their own outputs.
/// Called per pixel on the composite thread - keep it allocation-free.
/// </summary>
public interface IVideoLayerCpuEffect
{
    void Apply(ref float r, ref float g, ref float b, ref float a);
}
