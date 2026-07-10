using S.Media.Core.Video;
using S.Media.Gpu;
using Xunit;

namespace S.Media.Gpu.Tests;

public sealed class RgbGamutMatrixTests
{
    [Fact]
    public void Identity_LeavesRgbUnchanged()
    {
        var m = RgbGamutMatrix.Identity.Matrix;
        var (r, g, b) = Apply(m, 0.7f, 0.3f, 0.1f);
        Assert.Equal(0.7f, r, 5);
        Assert.Equal(0.3f, g, 5);
        Assert.Equal(0.1f, b, 5);
    }

    [Fact]
    public void Bt2020ToBt709_UnitWhiteRemainsUnitWhite()
    {
        // Both BT.2020 and BT.709 share D65 white; (1, 1, 1) must round-trip onto (1, 1, 1)
        // for the gamut remap to be physically reasonable.
        var m = RgbGamutMatrix.Bt2020ToBt709.Matrix;
        var (r, g, b) = Apply(m, 1f, 1f, 1f);
        Assert.Equal(1f, r, 3);
        Assert.Equal(1f, g, 3);
        Assert.Equal(1f, b, 3);
    }

    [Fact]
    public void Bt2020ToBt709_PureRedExpandsBeyondGamut()
    {
        // BT.2020 saturated red maps to a colour outside the BT.709 gamut - characteristic check
        // that we're going the right direction. R channel boosts > 1, G/B go slightly negative.
        var m = RgbGamutMatrix.Bt2020ToBt709.Matrix;
        var (r, g, b) = Apply(m, 1f, 0f, 0f);
        Assert.True(r > 1.5f, $"BT.709 R from BT.2020 R(1,0,0) should boost >1.5; got {r:F4}");
        Assert.True(g < 0f, $"BT.709 G should go slightly negative; got {g:F4}");
        Assert.True(b < 0f, $"BT.709 B should go slightly negative; got {b:F4}");
    }

    [Fact]
    public void FromHint_Bt2020Source_Bt709Display_PicksRemap()
    {
        var m = RgbGamutMatrix.FromHint(VideoColorSpace.Bt2020, VideoColorSpace.Bt709);
        Assert.Same(RgbGamutMatrix.Bt2020ToBt709.Matrix, m.Matrix);
    }

    [Fact]
    public void FromHint_Bt2020Cl_Bt709Display_PicksRemap()
    {
        var m = RgbGamutMatrix.FromHint(VideoColorSpace.Bt2020Cl, VideoColorSpace.Bt709);
        Assert.Same(RgbGamutMatrix.Bt2020ToBt709.Matrix, m.Matrix);
    }

    [Fact]
    public void FromHint_Bt709Source_StaysIdentity()
    {
        var m = RgbGamutMatrix.FromHint(VideoColorSpace.Bt709, VideoColorSpace.Bt709);
        Assert.Same(RgbGamutMatrix.Identity.Matrix, m.Matrix);
    }

    [Fact]
    public void FromHint_NonBt709Display_StaysIdentity()
    {
        // Only BT.709 SDR display preview is wired today.
        var m = RgbGamutMatrix.FromHint(VideoColorSpace.Bt2020, VideoColorSpace.Bt2020);
        Assert.Same(RgbGamutMatrix.Identity.Matrix, m.Matrix);
    }

    private static (float r, float g, float b) Apply(float[] m, float r, float g, float b)
    {
        // Row-major 3x3 layout (matches RgbGamutMatrix / YuvColorSpace convention).
        return (
            m[0] * r + m[1] * g + m[2] * b,
            m[3] * r + m[4] * g + m[5] * b,
            m[6] * r + m[7] * g + m[8] * b);
    }
}
