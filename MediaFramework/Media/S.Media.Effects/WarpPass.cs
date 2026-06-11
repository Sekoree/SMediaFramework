using System.Numerics;
using S.Media.Core.Video;

namespace S.Media.Effects;

/// <summary>One warp section: a crop of the composited canvas plus its affine placement (canvas
/// pixels → warp-output pixels) and opacity. With a non-null <paramref name="Mesh"/> the affine
/// <paramref name="Transform"/> is ignored for geometry — the mesh's control points already are
/// the section's destination shape. See <see cref="IWarpPassVideoCompositor"/>.</summary>
public readonly record struct WarpSection(
    RectNormalized SourceCrop, LayerTransform2D Transform, float Opacity, WarpMesh? Mesh = null);

/// <summary>
/// Mesh warp control grid for one section (Doc/HaPlay-Output-Mapping-Plan.md Phase 4): an
/// interpolating Catmull-Rom surface through <see cref="Points"/> — <c>Columns</c>×<c>Rows</c>
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

/// <summary>
/// Optional compositor capability: after compositing the layers, render the warp sections from the
/// composited canvas into a (possibly differently sized) output — entirely on the GPU, with a
/// single readback at the end. This is the integrated fast path for output mapping
/// (Doc/HaPlay-Output-Mapping-Plan.md Phase 2); chaining two compositors costs an extra readback +
/// re-upload per frame instead.
/// </summary>
public interface IWarpPassVideoCompositor : IVideoCompositor
{
    /// <summary>
    /// Configures the warp pass. With non-null <paramref name="sections"/>, subsequent
    /// <see cref="IVideoCompositor.Composite"/> calls return frames of <paramref name="warpOutput"/>
    /// size containing the warped sections; null disables the pass (raw canvas again).
    /// Thread-safe snapshot swap — callable while another thread composites.
    /// </summary>
    void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections);
}
