using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class OutputMappingResolverTests
{
    private static ClipOutputMappingSection Section(
        double srcX = 0, double srcY = 0, double srcW = 1, double srcH = 1,
        double destX = 0, double destY = 0, double destW = 0, double destH = 0,
        double rotation = 0, double opacity = 1, double brightness = 1, bool enabled = true) =>
        new("s", enabled, srcX, srcY, srcW, srcH, destX, destY, destW, destH, rotation, opacity, brightness);

    [Fact]
    public void FullCanvasSection_ResolvesToIdentityTransform()
    {
        var spec = new ClipOutputMappingSpec([Section()]);
        var resolved = OutputMappingResolver.Resolve(spec, 1920, 1080);

        var r = Assert.Single(resolved);
        Assert.True(r.SourceCrop.IsFull);
        AssertMapsTo(r, 0, 0, 0, 0);
        AssertMapsTo(r, 1920, 1080, 1920, 1080);
        Assert.Equal(1f, r.Opacity);
    }

    [Fact]
    public void MiddleThirdSlice_NaturalSize_MapsSliceOntoDestPosition()
    {
        // Middle 640px column of a 1920×1080 canvas, placed at x=660 (gap-skipping panel case).
        var spec = new ClipOutputMappingSpec(
            [Section(srcX: 1.0 / 3, srcW: 1.0 / 3, destX: 660, destY: 0)]);
        var r = Assert.Single(OutputMappingResolver.Resolve(spec, 1920, 1080));

        // Slice left edge (canvas x=640) lands at dest x=660; natural size keeps 640 wide.
        AssertMapsTo(r, 640, 0, 660, 0);
        AssertMapsTo(r, 1280, 1080, 1300, 1080);
    }

    [Fact]
    public void ExplicitDestSize_ScalesSlice()
    {
        // Left half scaled into a 480×270 thumbnail at the output origin.
        var spec = new ClipOutputMappingSpec([Section(srcW: 0.5, destW: 480, destH: 270)]);
        var r = Assert.Single(OutputMappingResolver.Resolve(spec, 1920, 1080));

        AssertMapsTo(r, 0, 0, 0, 0);
        AssertMapsTo(r, 960, 1080, 480, 270);
    }

    [Fact]
    public void Rotation_IsAroundDestCenter()
    {
        // 180° rotation: corners swap across the dest center, center stays put.
        var spec = new ClipOutputMappingSpec([Section(destW: 100, destH: 100, rotation: 180)]);
        var r = Assert.Single(OutputMappingResolver.Resolve(spec, 200, 200));

        AssertMapsTo(r, 100, 100, 50, 50); // source center → dest center
        AssertMapsTo(r, 0, 0, 100, 100);   // TL → BR
        AssertMapsTo(r, 200, 200, 0, 0);   // BR → TL
    }

    [Fact]
    public void DisabledAndDegenerateSections_AreFilteredOut()
    {
        var spec = new ClipOutputMappingSpec(
        [
            Section(enabled: false),
            Section(srcW: 0),              // degenerate source
            Section(opacity: 0),           // invisible
            Section(brightness: 0),        // invisible
            Section(destX: 5),             // the one survivor
        ]);

        Assert.Single(OutputMappingResolver.Resolve(spec, 1920, 1080));
    }

    [Fact]
    public void BrightnessFoldsIntoOpacity()
    {
        var spec = new ClipOutputMappingSpec([Section(opacity: 0.8, brightness: 0.5)]);
        var r = Assert.Single(OutputMappingResolver.Resolve(spec, 1920, 1080));
        Assert.Equal(0.4f, r.Opacity, precision: 5);
    }

    [Fact]
    public void ResolveOutputFormat_DefaultsToCanvasAndHonorsOverride()
    {
        var canvas = new S.Media.Core.Video.VideoFormat(
            1920, 1080, S.Media.Core.Video.PixelFormat.Bgra32, new S.Media.Core.Video.Rational(60, 1));

        var defaulted = OutputMappingResolver.ResolveOutputFormat(new ClipOutputMappingSpec([]), canvas);
        Assert.Equal((1920, 1080), (defaulted.Width, defaulted.Height));

        var sized = OutputMappingResolver.ResolveOutputFormat(
            new ClipOutputMappingSpec([], OutputWidth: 2560, OutputHeight: 800), canvas);
        Assert.Equal((2560, 800), (sized.Width, sized.Height));
        Assert.Equal(canvas.PixelFormat, sized.PixelFormat);
    }

    [Fact]
    public void Mesh_ResolvesToAbsoluteOutputPixels()
    {
        // 2×2 mesh on a 100×100 dest rect at (10, 20); BR control point pulled to 1.5 overshoot.
        var section = Section(destX: 10, destY: 20, destW: 100, destH: 100) with
        {
            MeshColumns = 2,
            MeshRows = 2,
            MeshPoints = [new(0, 0), new(1, 0), new(0, 1), new(1.5, 1.5)],
        };
        var r = Assert.Single(OutputMappingResolver.Resolve(new ClipOutputMappingSpec([section]), 200, 200));

        Assert.NotNull(r.Mesh);
        Assert.Equal((2, 2), (r.Mesh!.Columns, r.Mesh.Rows));
        Assert.Equal(10f, r.Mesh.Points[0].X, precision: 3);   // TL = rect origin
        Assert.Equal(20f, r.Mesh.Points[0].Y, precision: 3);
        Assert.Equal(110f, r.Mesh.Points[1].X, precision: 3);  // TR
        Assert.Equal(160f, r.Mesh.Points[3].X, precision: 3);  // BR overshoot: 10 + 1.5×100
        Assert.Equal(170f, r.Mesh.Points[3].Y, precision: 3);
    }

    [Fact]
    public void Mesh_RotationAppliesAroundDestCenter()
    {
        // 180° about the dest center (60, 70): the TL control point lands on the BR corner.
        var section = Section(destX: 10, destY: 20, destW: 100, destH: 100, rotation: 180) with
        {
            MeshColumns = 2,
            MeshRows = 2,
            MeshPoints = [new(0, 0), new(1, 0), new(0, 1), new(1.5, 1.5)],
        };
        var r = Assert.Single(OutputMappingResolver.Resolve(new ClipOutputMappingSpec([section]), 200, 200));

        Assert.NotNull(r.Mesh);
        Assert.Equal(110f, r.Mesh!.Points[0].X, precision: 3);
        Assert.Equal(120f, r.Mesh.Points[0].Y, precision: 3);
    }

    [Fact]
    public void Mesh_IdentityGrid_ResolvesToNull()
    {
        // Enabled-but-untouched mesh must keep the zero-cost affine path.
        var section = Section(destW: 100, destH: 100) with
        {
            MeshColumns = 3,
            MeshRows = 2,
            MeshPoints = [new(0, 0), new(0.5, 0), new(1, 0), new(0, 1), new(0.5, 1), new(1, 1)],
        };
        var r = Assert.Single(OutputMappingResolver.Resolve(new ClipOutputMappingSpec([section]), 200, 200));
        Assert.Null(r.Mesh);
    }

    [Fact]
    public void Mesh_MalformedGrids_ResolveToNull()
    {
        // Point-count mismatch and a sub-2 axis must fall back to affine, not throw.
        var wrongCount = Section(destW: 100, destH: 100) with
        {
            MeshColumns = 2, MeshRows = 2, MeshPoints = [new(0, 0), new(1, 1)],
        };
        var tooFew = Section(destW: 100, destH: 100) with
        {
            MeshColumns = 1, MeshRows = 2, MeshPoints = [new(0, 0), new(0, 1)],
        };

        foreach (var section in new[] { wrongCount, tooFew })
        {
            var r = Assert.Single(OutputMappingResolver.Resolve(new ClipOutputMappingSpec([section]), 200, 200));
            Assert.Null(r.Mesh);
        }
    }

    private static void AssertMapsTo(ResolvedMappingSection r, float srcX, float srcY, float destX, float destY)
    {
        var (x, y) = r.Transform.Apply(srcX, srcY);
        Assert.Equal(destX, x, precision: 2);
        Assert.Equal(destY, y, precision: 2);
    }
}
