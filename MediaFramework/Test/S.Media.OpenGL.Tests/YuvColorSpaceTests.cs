using S.Media.Core.Video;
using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public class YuvColorSpaceTests
{
    [Fact]
    public void FromHint_Bt709Limited_DefaultRangeIsLimited()
    {
        var cs = YuvColorSpace.FromHint(VideoColorSpace.Bt709, VideoColorRange.Limited, height: 1080);
        Assert.Equal(YuvColorSpace.Bt709Limited, cs);
    }

    [Fact]
    public void FromHint_Bt709Full()
    {
        var cs = YuvColorSpace.FromHint(VideoColorSpace.Bt709, VideoColorRange.Full, height: 1080);
        Assert.Equal(YuvColorSpace.Bt709Full, cs);
    }

    [Fact]
    public void FromHint_Bt2020Limited()
    {
        var cs = YuvColorSpace.FromHint(VideoColorSpace.Bt2020, VideoColorRange.Limited, height: 2160);
        Assert.Equal(YuvColorSpace.Bt2020Limited, cs);
    }

    [Fact]
    public void FromHint_Bt2020Full()
    {
        var cs = YuvColorSpace.FromHint(VideoColorSpace.Bt2020, VideoColorRange.Full, height: 2160);
        Assert.Equal(YuvColorSpace.Bt2020Full, cs);
    }

    [Fact]
    public void FromHint_Bt2020Cl_AliasesToBt2020Matrix()
    {
        var cs = YuvColorSpace.FromHint(VideoColorSpace.Bt2020Cl, VideoColorRange.Limited, height: 2160);
        Assert.Equal(YuvColorSpace.Bt2020Limited, cs);
    }

    [Fact]
    public void FromHint_Unspecified_FallsBackToHeightDefault()
    {
        var cs1080 = YuvColorSpace.FromHint(VideoColorSpace.Unspecified, VideoColorRange.Unspecified, height: 1080);
        var cs480 = YuvColorSpace.FromHint(VideoColorSpace.Unspecified, VideoColorRange.Unspecified, height: 480);
        Assert.Equal(YuvColorSpace.Bt709Limited, cs1080);
        Assert.Equal(YuvColorSpace.Bt601Limited, cs480);
    }

    [Fact]
    public void Bt2020_Matrices_AreDifferentFromBt709()
    {
        // Sanity: BT.2020 limited matrix coefficients should differ from BT.709 limited at the
        // V→R row (index 2 in row-major form: M[0,2]).
        Assert.NotEqual(YuvColorSpace.Bt709Limited.Matrix[2], YuvColorSpace.Bt2020Limited.Matrix[2]);
    }
}
