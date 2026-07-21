using System.Numerics;

namespace S.Media.Core.Video;

/// <summary>One warp section: a crop of the composited canvas plus its affine placement (canvas
/// pixels → warp-output pixels) and opacity. With a non-null <paramref name="Mesh"/> the affine
/// <paramref name="Transform"/> is ignored for geometry - the mesh's control points already are
/// the section's destination shape. See <c>IWarpPassVideoCompositor</c>.</summary>
public readonly record struct WarpSection(
    RectNormalized SourceCrop, LayerTransform2D Transform, float Opacity, WarpMesh? Mesh = null);

/// <summary>
/// Mesh warp control grid for one section (Doc/HaPlay-Output-Mapping-Plan.md Phase 4): an
/// interpolating Catmull-Rom surface through <see cref="Points"/> - <c>Columns</c>×<c>Rows</c>
/// control points, row-major, in absolute warp-output pixels. The surface passes through every
/// control point (drag a point and the image under it lands exactly there); borders use mirror
/// extrapolation, which makes a 2×2 grid exactly bilinear (corner pin).
/// </summary>
public sealed record WarpMesh
{
    public WarpMesh(int columns, int rows, Vector2[] points)
    {
        if (columns < 2 || rows < 2)
            throw new ArgumentOutOfRangeException(nameof(columns), $"mesh grid must be at least 2x2 (got {columns}x{rows}).");
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length != columns * rows)
            throw new ArgumentException(
                $"mesh needs {columns * rows} control points for {columns}x{rows}; got {points.Length}.", nameof(points));
        Columns = columns;
        Rows = rows;
        Points = points;
    }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Row-major control points in warp-output pixels (TL of the grid first).</summary>
    public Vector2[] Points { get; }
}
