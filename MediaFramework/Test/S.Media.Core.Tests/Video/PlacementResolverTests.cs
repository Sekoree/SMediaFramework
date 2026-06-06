using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class PlacementResolverTests
{
    private static readonly VideoFormat Hd = new(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void CenterFiftyPercent_SideBySide_TilesCanvasWithoutSpill()
    {
        // Left layer: left half of canvas, showing the centre 50% of a 16:9 source.
        var (lt, lc) = PlacementResolver.Resolve(
            new RectNormalized(0f, 0f, 0.5f, 1f), PlacementFit.Stretch,
            insetLeft: 0.25f, insetTop: 0f, insetRight: 0.25f, insetBottom: 0f, Hd, Hd);

        AssertClose(0.25f, lc.X0);
        AssertClose(0.75f, lc.X1);
        AssertClose(0f, lc.Y0);
        AssertClose(1f, lc.Y1);
        // The cropped region maps exactly onto the left half [0,960] x [0,1080].
        AssertMaps(lt, 480, 0, 0, 0);
        AssertMaps(lt, 1440, 1080, 960, 1080);

        // Right layer: same crop on the right half; the two tile the canvas, meeting at x=960.
        var (rt, _) = PlacementResolver.Resolve(
            new RectNormalized(0.5f, 0f, 1f, 1f), PlacementFit.Stretch,
            0.25f, 0f, 0.25f, 0f, Hd, Hd);
        AssertMaps(rt, 480, 0, 960, 0);
        AssertMaps(rt, 1440, 1080, 1920, 1080);
    }

    [Fact]
    public void Cover_IntoHalfRect_AutoCropsCenterWithoutSpill()
    {
        // No user crop, but Cover into a half-width rect must trim the overflow so it fills the half
        // exactly (centre crop) rather than spilling into the other half.
        var (t, c) = PlacementResolver.Resolve(
            new RectNormalized(0f, 0f, 0.5f, 1f), PlacementFit.Cover,
            0f, 0f, 0f, 0f, Hd, Hd);

        AssertClose(0.25f, c.X0);
        AssertClose(0.75f, c.X1);
        AssertMaps(t, 480, 0, 0, 0);
        AssertMaps(t, 1440, 1080, 960, 1080);
    }

    [Fact]
    public void Contain_FullCanvas_FourThreeSource_Pillarboxes()
    {
        var src = new VideoFormat(1440, 1080, PixelFormat.Bgra32, new Rational(30, 1)); // 4:3
        var (t, c) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Contain, 0f, 0f, 0f, 0f, src, Hd);

        Assert.True(c.IsFull); // full source shown, no crop
        // 1440x1080 fills height; the 1440-wide image is centred in 1920 -> 240px pillars.
        AssertMaps(t, 0, 0, 240, 0);
        AssertMaps(t, 1440, 1080, 1680, 1080);
    }

    [Fact]
    public void FullCanvas_NoCrop_IsBackwardCompatibleIdentityForMatchingSize()
    {
        var (t, c) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Cover, 0f, 0f, 0f, 0f, Hd, Hd);

        Assert.True(c.IsFull);
        AssertMaps(t, 0, 0, 0, 0);
        AssertMaps(t, 1920, 1080, 1920, 1080);
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.True(MathF.Abs(expected - actual) < 0.005f, $"expected {expected}, got {actual}");

    private static void AssertMaps(LayerTransform2D t, float sx, float sy, float ex, float ey)
    {
        var (dx, dy) = t.Apply(sx, sy);
        Assert.True(MathF.Abs(dx - ex) < 0.5f && MathF.Abs(dy - ey) < 0.5f,
            $"({sx},{sy}) -> expected ({ex},{ey}), got ({dx},{dy})");
    }
}
