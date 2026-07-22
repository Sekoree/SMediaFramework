using S.Media.Core.Video;

namespace S.Media.Core.Video.Effects;

/// <summary>
/// The GEOMETRY stage of the layer-effect system: turns one video layer into the section list it is
/// drawn as - splitting, per-section affine transforms, and optional Catmull-Rom warp meshes. Each
/// section becomes one compositor draw (<see cref="WarpSection"/> → <c>Slot.MappingSections</c> →
/// one <c>CompositorLayer</c>), so this stage runs in the vertex domain - deliberately
/// distinct from the fragment-domain color stage (<see cref="VideoLayerEffect"/>),
/// which cannot express multi-draw splitting or mesh warps.
/// </summary>
/// <remarks>
/// The built-in implementation is the session layer's output-mapping/warp ("VideoFx",
/// <c>S.Media.Session.OutputMappingGeometryEffect</c>). Sections are resolved in the effect's own
/// output space (<see cref="ResolveOutputFormat"/>); the host then places that space on the real
/// canvas via the placement's destination rect / fit mode, composing the placement transform onto
/// each section. Resolution happens on placement changes (control plane), never per frame.
/// Registry-based plugin registration for geometry effects needs these section types hoisted to
/// S.Media.Core first (the <c>BusRegistry</c> lives there) - until then, hosts wire implementations
/// directly.
/// </remarks>
public interface IVideoLayerGeometryEffect
{
    /// <summary>The output space the sections' destination coordinates target - the "virtual
    /// canvas" the placement then fits onto the real one. <paramref name="source"/> supplies the
    /// fallback size / pixel format / rate.</summary>
    VideoFormat ResolveOutputFormat(VideoFormat source);

    /// <summary>
    /// Resolves the sections (back-to-front) sampling the source video.
    /// <paramref name="sourceBounds"/> is the placement's crop sub-rectangle in [0,1] UV -
    /// sections must sample within it. Section transforms map full-frame source pixels into
    /// <see cref="ResolveOutputFormat"/> space; meshes (when present) carry absolute output-space
    /// control points.
    /// </summary>
    IReadOnlyList<WarpSection> ResolveSections(int sourceWidth, int sourceHeight, RectNormalized sourceBounds);
}
