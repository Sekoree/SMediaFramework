using S.Media.Compositor;
using S.Media.Compositor.Effects;
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
}
