using System.Text;
using S.Media.Core.Video.Effects;

namespace S.Media.Compositor.OpenGL;

/// <summary>
/// Splices a layer-effect chain into the composite fragment shader. The base shader carries two
/// marker comments; for a non-empty chain the composer replaces them with per-instance uniform
/// declarations + <c>vec4 ApplyFx{i}(vec4 src)</c> functions, and the chained calls at the sample
/// site. The empty chain compiles the base source untouched (markers are plain comments).
/// </summary>
internal static class VideoLayerEffectShaderComposer
{
    public const string DeclarationsMarker = "//__LAYER_FX_DECLARATIONS__";
    public const string ApplyMarker = "//__LAYER_FX_APPLY__";

    /// <summary>Cache key for one descriptor chain - program variants are keyed on this.</summary>
    public static string ChainKey(IReadOnlyList<VideoLayerEffect> effects)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < effects.Count; i++)
        {
            if (i > 0) sb.Append('+');
            sb.Append(effects[i].Descriptor.Id);
        }
        return sb.ToString();
    }

    public static string UniformName(int effectIndex, string parameterName) =>
        $"uFx{effectIndex}_{parameterName}";

    public static string Compose(string baseFragSrc, IReadOnlyList<VideoLayerEffect> effects)
    {
        if (!baseFragSrc.Contains(DeclarationsMarker, StringComparison.Ordinal)
            || !baseFragSrc.Contains(ApplyMarker, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "composite_layer.frag.glsl is missing the layer-effect markers; effects cannot be composed.");

        var decls = new StringBuilder();
        var apply = new StringBuilder();
        for (var i = 0; i < effects.Count; i++)
        {
            var d = effects[i].Descriptor;
            var body = d.GlslBody;
            foreach (var p in d.Parameters)
            {
                var uniform = UniformName(i, p.Name);
                decls.Append("uniform ").Append(GlslType(p.Components)).Append(' ').Append(uniform).Append(";\n");
                body = body.Replace($"$P({p.Name})", uniform, StringComparison.Ordinal);
            }

            if (body.Contains("$P(", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Effect '{d.Id}' GLSL body references an undeclared parameter: " +
                    $"'{body[body.IndexOf("$P(", StringComparison.Ordinal)..Math.Min(body.Length, body.IndexOf("$P(", StringComparison.Ordinal) + 24)]}...'.");

            decls.Append("vec4 ApplyFx").Append(i).Append("(vec4 src)\n{\n").Append(body).Append("\n}\n");
            apply.Append("src = ApplyFx").Append(i).Append("(src);\n");
        }

        return baseFragSrc
            .Replace(DeclarationsMarker, decls.ToString(), StringComparison.Ordinal)
            .Replace(ApplyMarker, apply.ToString(), StringComparison.Ordinal);
    }

    private static string GlslType(int components) => components switch
    {
        1 => "float",
        2 => "vec2",
        3 => "vec3",
        _ => "vec4",
    };
}
