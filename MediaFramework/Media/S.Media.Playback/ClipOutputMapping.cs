using S.Media.Core.Video;
using S.Media.Effects;

namespace S.Media.Playback;

/// <summary>
/// Output-mapping specification for one composition output lease: the composited canvas is cut
/// into <see cref="Sections"/> that are drawn back-to-front onto an output canvas of
/// <see cref="OutputWidth"/>×<see cref="OutputHeight"/> (defaults to the composition size).
/// Unmapped output area stays black — physical gaps (e.g. panel frames in a multi-panel rear
/// projection) fall out of section placement naturally.
/// </summary>
public sealed record ClipOutputMappingSpec(
    IReadOnlyList<ClipOutputMappingSection> Sections,
    int? OutputWidth = null,
    int? OutputHeight = null);

/// <summary>
/// One mapping section: a normalized source slice of the canvas plus an affine destination
/// placement in output pixels (position/size/rotation around the destination center).
/// </summary>
/// <param name="SrcX">Source slice left edge, normalized [0,1] canvas coordinates.</param>
/// <param name="DestWidth">Destination width in output pixels; ≤ 0 means the slice's natural pixel size.</param>
/// <param name="RotationDegrees">Rotation around the destination rect center, clockwise (Y-down).</param>
/// <param name="Opacity">Per-section alpha multiplier [0,1].</param>
/// <param name="Brightness">Per-section brightness [0,1] for panel matching. Folded into the layer
/// opacity — over the black output background that equals an RGB multiply; with overlapping
/// sections the lower section shows through instead (acceptable for v1).</param>
public sealed record ClipOutputMappingSection(
    string Id,
    bool Enabled,
    double SrcX, double SrcY, double SrcWidth, double SrcHeight,
    double DestX, double DestY, double DestWidth, double DestHeight,
    double RotationDegrees = 0.0,
    double Opacity = 1.0,
    double Brightness = 1.0);

/// <summary>A mapping section resolved against a concrete canvas: ready to become a
/// <see cref="CompositorLayer"/> whose source frame is the composited canvas.</summary>
public readonly record struct ResolvedMappingSection(
    RectNormalized SourceCrop,
    LayerTransform2D Transform,
    float Opacity);

/// <summary>
/// Pure math: turns <see cref="ClipOutputMappingSpec"/> sections into crop + affine transform pairs
/// for the mapping compositor. Mirrors <see cref="PlacementResolver"/>'s role for cue placements.
/// </summary>
/// <remarks>
/// Compositor semantics (see <c>CpuVideoCompositor.DrawLayer</c>): the transform maps FULL-FRAME
/// source pixel coordinates to destination pixels, and <see cref="CompositorLayer.SourceCrop"/>
/// gates which source pixels are drawn. The section transform therefore moves the slice's pixel
/// center onto the destination center: <c>Translate(destCenter) ∘ Rotate ∘ Scale ∘ Translate(−sliceCenter)</c>.
/// </remarks>
public static class OutputMappingResolver
{
    /// <summary>Output canvas format for <paramref name="spec"/> over <paramref name="canvas"/> —
    /// the spec's explicit size when set, else the canvas size (same pixel format / rate).</summary>
    public static VideoFormat ResolveOutputFormat(ClipOutputMappingSpec spec, VideoFormat canvas) =>
        new(
            Math.Max(16, spec.OutputWidth ?? canvas.Width),
            Math.Max(16, spec.OutputHeight ?? canvas.Height),
            canvas.PixelFormat,
            canvas.FrameRate);

    /// <summary>
    /// Resolves the spec's enabled, non-degenerate sections (back-to-front order preserved)
    /// against a canvas of <paramref name="canvasWidth"/>×<paramref name="canvasHeight"/> pixels.
    /// </summary>
    public static List<ResolvedMappingSection> Resolve(
        ClipOutputMappingSpec spec,
        int canvasWidth,
        int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var resolved = new List<ResolvedMappingSection>(spec.Sections.Count);
        foreach (var section in spec.Sections)
        {
            if (!section.Enabled)
                continue;
            if (TryResolveSection(section, canvasWidth, canvasHeight, out var r))
                resolved.Add(r);
        }

        return resolved;
    }

    private static bool TryResolveSection(
        ClipOutputMappingSection s,
        int canvasWidth,
        int canvasHeight,
        out ResolvedMappingSection resolved)
    {
        resolved = default;

        // Degeneracy on the raw model values — Clamped() below nudges zero-area rects to a sliver
        // (a slot-mailbox affordance), which would let an empty slice slip through.
        if (s.SrcWidth <= 0 || s.SrcHeight <= 0)
            return false;

        var crop = new RectNormalized(
            (float)s.SrcX,
            (float)s.SrcY,
            (float)(s.SrcX + s.SrcWidth),
            (float)(s.SrcY + s.SrcHeight)).Clamped();

        // Slice size in canvas pixels (post-clamp so an out-of-range model can't go degenerate).
        var sliceW = crop.Width * canvasWidth;
        var sliceH = crop.Height * canvasHeight;
        if (sliceW < 1e-3f || sliceH < 1e-3f)
            return false;

        var destW = (float)(s.DestWidth > 0 ? s.DestWidth : sliceW);
        var destH = (float)(s.DestHeight > 0 ? s.DestHeight : sliceH);
        if (destW < 1e-3f || destH < 1e-3f)
            return false;

        var srcCenterX = (crop.X0 + crop.X1) * 0.5f * canvasWidth;
        var srcCenterY = (crop.Y0 + crop.Y1) * 0.5f * canvasHeight;
        var destCenterX = (float)s.DestX + destW * 0.5f;
        var destCenterY = (float)s.DestY + destH * 0.5f;

        var transform = LayerTransform2D.Compose(
            LayerTransform2D.Translate(destCenterX, destCenterY),
            LayerTransform2D.Compose(
                LayerTransform2D.Rotate((float)(s.RotationDegrees * Math.PI / 180.0)),
                LayerTransform2D.Compose(
                    LayerTransform2D.Scale(destW / sliceW, destH / sliceH),
                    LayerTransform2D.Translate(-srcCenterX, -srcCenterY))));

        var opacity = Math.Clamp((float)(s.Opacity * s.Brightness), 0f, 1f);
        if (opacity <= 0f)
            return false;

        resolved = new ResolvedMappingSection(crop, transform, opacity);
        return true;
    }
}
