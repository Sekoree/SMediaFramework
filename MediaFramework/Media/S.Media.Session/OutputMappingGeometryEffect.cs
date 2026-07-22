using S.Media.Compositor;
using S.Media.Core.Video.Effects;
using S.Media.Core.Video;

namespace S.Media.Session;

/// <summary>
/// The built-in geometry-stage effect: adapts a persisted <see cref="ClipOutputMappingSpec"/>
/// (the GUI's mapping/warp/splitting "VideoFx") to <see cref="IVideoLayerGeometryEffect"/> by
/// delegating to <see cref="OutputMappingResolver"/>. Pure math, safe to construct per placement
/// update.
/// </summary>
public sealed class OutputMappingGeometryEffect(ClipOutputMappingSpec spec) : IVideoLayerGeometryEffect
{
    private readonly ClipOutputMappingSpec _spec = spec ?? throw new ArgumentNullException(nameof(spec));

    public VideoFormat ResolveOutputFormat(VideoFormat source) =>
        OutputMappingResolver.ResolveOutputFormat(_spec, source);

    public IReadOnlyList<WarpSection> ResolveSections(int sourceWidth, int sourceHeight, RectNormalized sourceBounds)
    {
        var resolved = OutputMappingResolver.Resolve(_spec, sourceWidth, sourceHeight, sourceBounds);
        var sections = new WarpSection[resolved.Count];
        for (var i = 0; i < resolved.Count; i++)
        {
            var r = resolved[i];
            sections[i] = new WarpSection(r.SourceCrop, r.Transform, r.Opacity, r.Mesh);
        }

        return sections;
    }

    /// <summary>
    /// Registry factory (<c>IBusRegistry.AddGeometryEffect</c>): builds the effect from a
    /// serialized <see cref="ClipOutputMappingSpec"/> JSON blob. Throws on missing/unusable
    /// config per the geometry-stage contract - there is no meaningful identity geometry, so
    /// <c>TryCreateGeometryEffect</c> reports false instead of silently splitting into nothing.
    /// </summary>
    public static OutputMappingGeometryEffect FromJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new FormatException("mapping geometry effect requires a ClipOutputMappingSpec JSON config.");
        var spec = System.Text.Json.JsonSerializer.Deserialize(
            configJson, ShowDocumentJsonContext.Default.ClipOutputMappingSpec)
            ?? throw new FormatException("mapping geometry effect config deserialized to null.");
        if (spec.Sections.Count == 0)
            throw new FormatException("mapping geometry effect config has no sections.");
        return new OutputMappingGeometryEffect(spec);
    }
}
