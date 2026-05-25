using S.Media.Core.Video;
using S.Media.Effects;
using S.Media.Effects.OpenGL;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class GlVideoCompositorOutputPrecisionTests
{
    [Fact]
    public void OutputPixelFormatForPrecision_MapsRgba16AndRgba16F()
    {
        Assert.Equal(PixelFormat.Bgra32, InvokeOutputPixelFormat(GlCompositorOutputPrecision.Rgba8));
        Assert.Equal(PixelFormat.Rgba16, InvokeOutputPixelFormat(GlCompositorOutputPrecision.Rgba16));
        Assert.Equal(PixelFormat.Rgba16F, InvokeOutputPixelFormat(GlCompositorOutputPrecision.Rgba16F));
    }

    [Fact]
    public void VideoCompositorOptions_DefaultGlOutputPrecision_IsRgba8()
    {
        var options = new VideoCompositorOptions();
        Assert.Equal(GlCompositorOutputPrecision.Rgba8, options.GlOutputPrecision);
    }

    [Theory]
    [InlineData(PixelFormat.Rgba16, 1)]
    [InlineData(PixelFormat.Rgba16F, 1)]
    [InlineData(PixelFormat.P216, 2)]
    [InlineData(PixelFormat.Pa16, 3)]
    public void NewFormats_PixelFormatInfo_IsCoherent(PixelFormat fmt, int planeCount)
    {
        Assert.Equal(planeCount, PixelFormatInfo.PlaneCount(fmt));
        Assert.True(PixelFormatInfo.IsHighBitDepth(fmt));
    }

    private static PixelFormat InvokeOutputPixelFormat(GlCompositorOutputPrecision precision)
    {
        var method = typeof(GlVideoCompositor).GetMethod(
            "OutputPixelFormatForPrecision",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (PixelFormat)method!.Invoke(null, [precision])!;
    }
}
