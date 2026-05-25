using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoFormatTests
{
    [Fact]
    public void RecordEquality_HoldsForSameValues()
    {
        var a = new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));
        var b = new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DiffersOnFormat()
    {
        var a = new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));
        var b = new VideoFormat(1920, 1080, PixelFormat.Rgba32, new Rational(30, 1));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Rational_2997Fps_RoundsCorrectly()
    {
        var ntsc = new Rational(30000, 1001);
        Assert.InRange(ntsc.ToDouble(), 29.96, 29.98);
    }

    [Fact]
    public void Rational_DivByZero_ReturnsZero()
    {
        Assert.Equal(0.0, new Rational(30, 0).ToDouble());
    }
}
