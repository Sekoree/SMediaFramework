using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoCpuOpacityTests
{
    [Fact]
    public void Bgra32_HalfOpacity_ScalesRgbTowardBlack()
    {
        var buf = new byte[] { 200, 100, 50, 255 };
        var fmt = new VideoFormat(1, 1, PixelFormat.Bgra32, new Rational(30, 1));
        using var frame = new VideoFrame(TimeSpan.Zero, fmt, buf, stride: 4);

        VideoCpuOpacity.ApplyInPlace(frame, 0.5f, VideoColorRange.Full);

        Assert.Equal(100, buf[0]);
        Assert.Equal(50, buf[1]);
        Assert.Equal(25, buf[2]);
        Assert.InRange(buf[3], 126, 130);
    }

    [Fact]
    public void Nv12_HalfOpacity_LerpsYTowardLimitedBlackAndChromaTowardNeutral()
    {
        const int w = 4;
        const int h = 4;
        var y = new byte[w * h];
        Array.Fill(y, (byte)235);
        var uv = new byte[w * h / 2];
        Array.Fill(uv, (byte)140);
        var fmt = new VideoFormat(w, h, PixelFormat.Nv12, new Rational(30, 1));
        using var frame = new VideoFrame(TimeSpan.Zero, fmt, [y, uv], [w, w]);

        VideoCpuOpacity.ApplyInPlace(frame, 0.5f, VideoColorRange.Limited);

        Assert.InRange(y[0], 124, 128);
        Assert.InRange(uv[0], 133, 135);
    }

    [Fact]
    public void Uyvy_HalfOpacity_AdjustsYAndChromaSeparately()
    {
        var buf = new byte[] { 140, 200, 130, 210 };
        var fmt = new VideoFormat(2, 1, PixelFormat.Uyvy, new Rational(30, 1));
        using var frame = new VideoFrame(TimeSpan.Zero, fmt, buf, stride: 4);

        VideoCpuOpacity.ApplyInPlace(frame, 0.5f, VideoColorRange.Limited);

        Assert.InRange(buf[0], 133, 135);
        Assert.InRange(buf[1], 107, 109);
        Assert.InRange(buf[2], 128, 130);
        Assert.InRange(buf[3], 112, 114);
    }
}
