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

    private static void AssertMapsTo(ResolvedMappingSection r, float srcX, float srcY, float destX, float destY)
    {
        var (x, y) = r.Transform.Apply(srcX, srcY);
        Assert.Equal(destX, x, precision: 2);
        Assert.Equal(destY, y, precision: 2);
    }
}
