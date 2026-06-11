using System.Numerics;

namespace S.Media.Effects;

/// <summary>
/// Pure math for <see cref="WarpMesh"/>: evaluates the interpolating Catmull-Rom surface and
/// tessellates it into an indexed triangle grid for the GL warp pass. CPU-side and allocation-only
/// on (re)build — per frame the GPU just draws the uploaded buffers.
/// </summary>
/// <remarks>
/// Border segments use mirror-extrapolated virtual control points (<c>P(-1) = 2·P(0) − P(1)</c>),
/// which cancels the cubic terms on a single-segment axis — a 2×2 mesh is therefore exactly
/// bilinear, so corner-pin behaves linearly instead of eased. Inner segments are standard uniform
/// Catmull-Rom and pass through every control point.
/// </remarks>
public static class WarpMeshTessellator
{
    /// <summary>Sub-segments per control-point cell per axis — fine enough that the piecewise-linear
    /// triangles are visually indistinguishable from the smooth surface at projection scales.</summary>
    public const int DefaultSubdivisionsPerCell = 8;

    /// <summary>Hard cap on tessellation segments per axis (keeps a hostile/buggy grid bounded).</summary>
    public const int MaxSegmentsPerAxis = 256;

    /// <summary>Evaluates the surface at normalized grid parameters <paramref name="s"/>,
    /// <paramref name="t"/> ∈ [0,1] ((0,0) = first control point, (1,1) = last).</summary>
    public static Vector2 Evaluate(WarpMesh mesh, float s, float t)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var cols = mesh.Columns;
        var rows = mesh.Rows;

        var u = Math.Clamp(s, 0f, 1f) * (cols - 1);
        var v = Math.Clamp(t, 0f, 1f) * (rows - 1);
        var ci = Math.Min((int)u, cols - 2);
        var ri = Math.Min((int)v, rows - 2);
        var lu = u - ci;
        var lv = v - ri;

        // Tensor product: spline along columns for the four bracketing rows, then along the results.
        Span<Vector2> rowPoints = stackalloc Vector2[4];
        for (var k = -1; k <= 2; k++)
        {
            rowPoints[k + 1] = CatmullRom(
                Point(mesh, ci - 1, ri + k),
                Point(mesh, ci, ri + k),
                Point(mesh, ci + 1, ri + k),
                Point(mesh, ci + 2, ri + k),
                lu);
        }

        return CatmullRom(rowPoints[0], rowPoints[1], rowPoints[2], rowPoints[3], lv);
    }

    /// <summary>
    /// Tessellates the surface into an indexed triangle grid. <paramref name="vertices"/> is
    /// interleaved <c>(s, t, x, y)</c> per vertex — <c>(s,t)</c> the section-normalized parameter
    /// (drives source UV via the crop), <c>(x,y)</c> the warped position in output pixels.
    /// </summary>
    public static void Tessellate(
        WarpMesh mesh,
        out float[] vertices,
        out uint[] indices,
        int subdivisionsPerCell = DefaultSubdivisionsPerCell)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfLessThan(subdivisionsPerCell, 1);

        var segsX = Math.Min((mesh.Columns - 1) * subdivisionsPerCell, MaxSegmentsPerAxis);
        var segsY = Math.Min((mesh.Rows - 1) * subdivisionsPerCell, MaxSegmentsPerAxis);
        var vertCols = segsX + 1;
        var vertRows = segsY + 1;

        vertices = new float[vertCols * vertRows * 4];
        var w = 0;
        for (var y = 0; y < vertRows; y++)
        {
            var t = (float)y / segsY;
            for (var x = 0; x < vertCols; x++)
            {
                var s = (float)x / segsX;
                var p = Evaluate(mesh, s, t);
                vertices[w++] = s;
                vertices[w++] = t;
                vertices[w++] = p.X;
                vertices[w++] = p.Y;
            }
        }

        indices = new uint[segsX * segsY * 6];
        var n = 0;
        for (var y = 0; y < segsY; y++)
        {
            for (var x = 0; x < segsX; x++)
            {
                var tl = (uint)(y * vertCols + x);
                var tr = tl + 1;
                var bl = tl + (uint)vertCols;
                var br = bl + 1;
                indices[n++] = tl;
                indices[n++] = tr;
                indices[n++] = br;
                indices[n++] = tl;
                indices[n++] = br;
                indices[n++] = bl;
            }
        }
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (
            2f * p1
            + (p2 - p0) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    /// <summary>Grid accessor with mirror extrapolation one point past each border
    /// (<c>P(-1) = 2·P(0) − P(1)</c>); recursion depth ≤ 2 (corner needs both axes).</summary>
    private static Vector2 Point(WarpMesh mesh, int c, int r)
    {
        if (c < 0) return 2f * Point(mesh, 0, r) - Point(mesh, 1, r);
        if (c >= mesh.Columns) return 2f * Point(mesh, mesh.Columns - 1, r) - Point(mesh, mesh.Columns - 2, r);
        if (r < 0) return 2f * Point(mesh, c, 0) - Point(mesh, c, 1);
        if (r >= mesh.Rows) return 2f * Point(mesh, c, mesh.Rows - 1) - Point(mesh, c, mesh.Rows - 2);
        return mesh.Points[r * mesh.Columns + c];
    }
}
