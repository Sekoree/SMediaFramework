using System.Numerics;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class WarpMeshTessellatorTests
{
    [Fact]
    public void Evaluate_PassesThroughEveryControlPoint()
    {
        // Interpolating surface: at grid-knot parameters the surface must hit the control point
        // exactly — that's the calibration contract (drag a point, the image lands on it).
        var points = new Vector2[]
        {
            new(0, 0), new(50, -10), new(100, 0),
            new(-5, 50), new(55, 60), new(105, 50),
            new(0, 100), new(50, 115), new(100, 100),
        };
        var mesh = new WarpMesh(3, 3, points);

        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var p = WarpMeshTessellator.Evaluate(mesh, c / 2f, r / 2f);
                Assert.Equal(points[r * 3 + c].X, p.X, precision: 3);
                Assert.Equal(points[r * 3 + c].Y, p.Y, precision: 3);
            }
        }
    }

    [Fact]
    public void TwoByTwoMesh_IsExactlyBilinear()
    {
        // Mirror-extrapolated borders cancel the cubic terms on a single-segment axis, so a 2×2
        // mesh is a corner pin with linear (not eased) interpolation.
        var tl = new Vector2(10, 20);
        var tr = new Vector2(110, 0);
        var bl = new Vector2(0, 120);
        var br = new Vector2(130, 140);
        var mesh = new WarpMesh(2, 2, [tl, tr, bl, br]);

        for (var i = 0; i <= 4; i++)
        {
            for (var j = 0; j <= 4; j++)
            {
                var s = i / 4f;
                var t = j / 4f;
                var expected = Vector2.Lerp(Vector2.Lerp(tl, tr, s), Vector2.Lerp(bl, br, s), t);
                var actual = WarpMeshTessellator.Evaluate(mesh, s, t);
                Assert.Equal(expected.X, actual.X, precision: 3);
                Assert.Equal(expected.Y, actual.Y, precision: 3);
            }
        }
    }

    [Fact]
    public void Evaluate_IdentityGrid_IsIdentity()
    {
        // Control points on a uniform grid → the surface is that grid everywhere, not just at knots.
        var points = new Vector2[12];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 4; c++)
                points[r * 4 + c] = new Vector2(c * 100, r * 100);
        }
        var mesh = new WarpMesh(4, 3, points);

        var p = WarpMeshTessellator.Evaluate(mesh, 0.37f, 0.81f);
        Assert.Equal(0.37f * 300f, p.X, precision: 2);
        Assert.Equal(0.81f * 200f, p.Y, precision: 2);
    }

    [Fact]
    public void Tessellate_ProducesConsistentGridAndIndices()
    {
        var mesh = new WarpMesh(2, 2, [new(0, 0), new(100, 0), new(0, 100), new(100, 100)]);
        WarpMeshTessellator.Tessellate(mesh, out var vertices, out var indices, subdivisionsPerCell: 4);

        // 1 cell per axis × 4 subdivisions = 4 segments → 5×5 vertices, 4×4×2 triangles.
        Assert.Equal(5 * 5 * 4, vertices.Length);
        Assert.Equal(4 * 4 * 6, indices.Length);

        foreach (var index in indices)
            Assert.InRange(index, 0u, 24u);

        // First vertex: (s,t) = (0,0) at the TL control point; last: (1,1) at BR.
        Assert.Equal(0f, vertices[0]);
        Assert.Equal(0f, vertices[1]);
        Assert.Equal(0f, vertices[2], precision: 3);
        Assert.Equal(0f, vertices[3], precision: 3);
        var last = vertices.Length - 4;
        Assert.Equal(1f, vertices[last]);
        Assert.Equal(1f, vertices[last + 1]);
        Assert.Equal(100f, vertices[last + 2], precision: 3);
        Assert.Equal(100f, vertices[last + 3], precision: 3);
    }

    [Fact]
    public void Tessellate_CapsSegmentsPerAxis()
    {
        var cols = 60;
        var rows = 2;
        var points = new Vector2[cols * rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                points[r * cols + c] = new Vector2(c, r);
        }

        // 59 cells × 8 subdivisions = 472 raw segments → capped at MaxSegmentsPerAxis.
        WarpMeshTessellator.Tessellate(new WarpMesh(cols, rows, points), out var vertices, out _);
        var vertCols = WarpMeshTessellator.MaxSegmentsPerAxis + 1;
        var vertRows = 8 + 1;
        Assert.Equal(vertCols * vertRows * 4, vertices.Length);
    }

    [Fact]
    public void WarpMesh_RejectsMalformedGrids()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WarpMesh(1, 2, new Vector2[2]));
        Assert.Throws<ArgumentException>(() => new WarpMesh(2, 2, new Vector2[3]));
    }
}
