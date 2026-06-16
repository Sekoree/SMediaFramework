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
/// <param name="MeshColumns">Mesh warp control grid columns; 0 (with <paramref name="MeshRows"/>)
/// = no mesh (pure affine). Phase 4, see Doc/HaPlay-Output-Mapping-Plan.md.</param>
/// <param name="MeshPoints">Row-major <c>MeshColumns × MeshRows</c> control points in normalized
/// destination-rect space ((0,0) = dest rect TL, (1,1) = BR; values outside [0,1] overshoot the
/// rect). Stored relative so moving/scaling/rotating the section carries its warp along; the
/// resolver bakes them to absolute output pixels. An identity grid resolves to no mesh.</param>
public sealed record ClipOutputMappingSection(
    string Id,
    bool Enabled,
    double SrcX, double SrcY, double SrcWidth, double SrcHeight,
    double DestX, double DestY, double DestWidth, double DestHeight,
    double RotationDegrees = 0.0,
    double Opacity = 1.0,
    double Brightness = 1.0,
    int MeshColumns = 0,
    int MeshRows = 0,
    IReadOnlyList<ClipMeshPoint>? MeshPoints = null);

/// <summary>One mesh control point in normalized destination-rect space (see
/// <see cref="ClipOutputMappingSection.MeshPoints"/>).</summary>
public sealed record ClipMeshPoint(double X, double Y);

/// <summary>A mapping section resolved against a concrete canvas: ready to become a
/// <see cref="CompositorLayer"/> whose source frame is the composited canvas. <paramref name="Mesh"/>
/// (control points in absolute output pixels) is non-null only for warp-capable GL backends — the
/// CPU chained stage ignores it and falls back to the affine transform.</summary>
public readonly record struct ResolvedMappingSection(
    RectNormalized SourceCrop,
    LayerTransform2D Transform,
    float Opacity,
    WarpMesh? Mesh = null);

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
        int canvasHeight,
        RectNormalized? sourceBounds = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var resolved = new List<ResolvedMappingSection>(spec.Sections.Count);
        var bounds = (sourceBounds ?? RectNormalized.Full).Clamped();
        foreach (var section in spec.Sections)
        {
            if (!section.Enabled)
                continue;
            if (TryResolveSection(section, canvasWidth, canvasHeight, bounds, out var r))
                resolved.Add(r);
        }

        return resolved;
    }

    private static bool TryResolveSection(
        ClipOutputMappingSection s,
        int canvasWidth,
        int canvasHeight,
        RectNormalized sourceBounds,
        out ResolvedMappingSection resolved)
    {
        resolved = default;

        // Degeneracy on the raw model values — Clamped() below nudges zero-area rects to a sliver
        // (a slot-mailbox affordance), which would let an empty slice slip through.
        if (s.SrcWidth <= 0 || s.SrcHeight <= 0)
            return false;

        var crop = new RectNormalized(
            sourceBounds.X0 + (float)s.SrcX * sourceBounds.Width,
            sourceBounds.Y0 + (float)s.SrcY * sourceBounds.Height,
            sourceBounds.X0 + (float)(s.SrcX + s.SrcWidth) * sourceBounds.Width,
            sourceBounds.Y0 + (float)(s.SrcY + s.SrcHeight) * sourceBounds.Height).Clamped();

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

        resolved = new ResolvedMappingSection(crop, transform, opacity,
            TryResolveMesh(s, destW, destH, destCenterX, destCenterY));
        return true;
    }

    /// <summary>Max mesh control points per axis — matches the editor's cap; anything bigger is
    /// treated as malformed and ignored (affine fallback) rather than tessellated unbounded.</summary>
    private const int MaxMeshPointsPerAxis = 65;

    /// <summary>Identity tolerance in normalized dest-rect units (~1/20 px at 1080p) — a mesh whose
    /// points all sit on the identity grid resolves to null, keeping the zero-cost affine path.</summary>
    private const double MeshIdentityEpsilon = 5e-5;

    /// <summary>
    /// Bakes the section's normalized mesh control points to absolute output pixels: unnormalize
    /// over the dest rect, then rotate about the dest center — the same placement the affine
    /// transform applies. (Catmull-Rom interpolation commutes with affine maps, so transforming the
    /// control points equals transforming the evaluated surface.) Malformed grids resolve to null.
    /// </summary>
    private static WarpMesh? TryResolveMesh(
        ClipOutputMappingSection s, float destW, float destH, float destCenterX, float destCenterY)
    {
        if (s.MeshColumns < 2 || s.MeshRows < 2 || s.MeshPoints is null)
            return null;
        if (s.MeshColumns > MaxMeshPointsPerAxis || s.MeshRows > MaxMeshPointsPerAxis)
            return null;
        if (s.MeshPoints.Count != s.MeshColumns * s.MeshRows)
            return null;

        var identity = true;
        for (var r = 0; r < s.MeshRows && identity; r++)
        {
            for (var c = 0; c < s.MeshColumns; c++)
            {
                var p = s.MeshPoints[r * s.MeshColumns + c];
                if (Math.Abs(p.X - c / (double)(s.MeshColumns - 1)) > MeshIdentityEpsilon
                    || Math.Abs(p.Y - r / (double)(s.MeshRows - 1)) > MeshIdentityEpsilon)
                {
                    identity = false;
                    break;
                }
            }
        }

        if (identity)
            return null;

        var radians = s.RotationDegrees * Math.PI / 180.0;
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);
        var destX = (float)s.DestX;
        var destY = (float)s.DestY;

        var points = new System.Numerics.Vector2[s.MeshPoints.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var p = s.MeshPoints[i];
            var dx = destX + (float)p.X * destW - destCenterX;
            var dy = destY + (float)p.Y * destH - destCenterY;
            points[i] = new System.Numerics.Vector2(
                destCenterX + dx * cos - dy * sin,
                destCenterY + dx * sin + dy * cos);
        }

        return new WarpMesh(s.MeshColumns, s.MeshRows, points);
    }
}
