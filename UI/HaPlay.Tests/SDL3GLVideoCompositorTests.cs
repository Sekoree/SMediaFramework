using S.Media.Core.Video;
using S.Media.SDL3;
using Xunit;

namespace HaPlay.Tests;

public sealed class SDL3GLVideoCompositorTests
{
    [Theory]
    [InlineData(PixelFormat.Yuv422P10Le)]
    [InlineData(PixelFormat.Yuva444P12Le)]
    [InlineData(PixelFormat.P216)]
    [InlineData(PixelFormat.Pa16)]
    public void AcceptedLayerPixelFormats_Includes_ProfessionalYuvFormats(PixelFormat format)
    {
        var compositor = new SDL3GLVideoCompositor(
            new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(60, 1)));

        Assert.Contains(format, compositor.AcceptedLayerPixelFormats);
    }

    [Fact]
    public void Configure_Rejects_NonReadbackOutput()
    {
        var compositor = new SDL3GLVideoCompositor(
            new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(60, 1)));

        Assert.Throws<ArgumentException>(() =>
            compositor.Configure(new VideoFormat(1920, 1080, PixelFormat.P216, new Rational(60, 1))));
    }
}
