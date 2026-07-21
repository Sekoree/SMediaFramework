using System.Numerics;
using BenchmarkDotNet.Attributes;
using S.Media.Compositor;

namespace S.Media.Compositor.Benchmarks;

/// <summary>
/// Measures <see cref="WarpMeshTessellator.Tessellate"/>. A <see cref="CompositorLayer.Mesh"/> layer
/// currently re-tessellates (and re-allocates the vertex/index arrays) every frame per layer in
/// <c>GlVideoCompositor.DrawLayerMesh</c>; this quantifies what caching per-mesh buffers would save.
/// </summary>
[MemoryDiagnoser]
public class TessellateBenchmarks
{
    private WarpMesh _mesh = null!;

    [Params(2, 5, 17)]
    public int Grid { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var points = new Vector2[Grid * Grid];
        for (var r = 0; r < Grid; r++)
        {
            for (var c = 0; c < Grid; c++)
            {
                // Slightly perturbed grid over a 1920x1080 canvas so the Catmull-Rom surface is non-trivial.
                points[r * Grid + c] = new Vector2(
                    c * (1920f / (Grid - 1)) + ((c + r) % 3 - 1) * 6f,
                    r * (1080f / (Grid - 1)) + ((c * r) % 3 - 1) * 6f);
            }
        }

        _mesh = new WarpMesh(Grid, Grid, points);
    }

    [Benchmark]
    public (float[], uint[]) Tessellate()
    {
        WarpMeshTessellator.Tessellate(_mesh, out var vertices, out var indices);
        return (vertices, indices);
    }
}
