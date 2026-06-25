using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class BobDeinterlacerTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Progressive_PassesThroughAsSingleOutput()
    {
        using var deinterlacer = new BobDeinterlacer(Bgra32_4x4);
        var plane = new byte[4 * 4 * 4];
        var frame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, plane, 16, release: null);
        Span<VideoFrame?> outs = new VideoFrame?[2];
        var n = deinterlacer.Process(frame, outs);
        Assert.Equal(1, n);
        Assert.Same(frame, outs[0]);
        frame.Dispose();
    }

    [Fact]
    public void Interlaced_TopFieldFirst_EmitsTwoProgressiveFrames()
    {
        using var deinterlacer = new BobDeinterlacer(Bgra32_4x4);
        // 4x4 BGRA: rows 0,2 = red, rows 1,3 = blue.
        var plane = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var idx = (y * 4 + x) * 4;
            var isRed = (y & 1) == 0;
            plane[idx + 0] = isRed ? (byte)0 : (byte)255;
            plane[idx + 1] = 0;
            plane[idx + 2] = isRed ? (byte)255 : (byte)0;
            plane[idx + 3] = 255;
        }
        var frame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, plane, 16, release: null,
            metadata: new VideoFrameMetadata(FieldOrder: VideoFieldOrder.TopFieldFirst));
        Span<VideoFrame?> outs = new VideoFrame?[2];
        var n = deinterlacer.Process(frame, outs);
        try
        {
            Assert.Equal(2, n);
            Assert.NotNull(outs[0]);
            Assert.NotNull(outs[1]);
            // First output should be from top field (red on rows 0,2). Sample pixel (0,0) — red.
            var top = outs[0]!.Planes[0].Span;
            Assert.Equal(0, top[0]);   // B
            Assert.Equal(0, top[1]);   // G
            Assert.Equal(255, top[2]); // R
            // Output PTS is monotonic.
            Assert.True(outs[1]!.PresentationTime > outs[0]!.PresentationTime);
            // Both outputs are progressive.
            Assert.Equal(VideoFieldOrder.Progressive, outs[0]!.FieldOrder);
            Assert.Equal(VideoFieldOrder.Progressive, outs[1]!.FieldOrder);
        }
        finally
        {
            outs[0]?.Dispose();
            outs[1]?.Dispose();
            frame.Dispose();
        }
    }

    [Fact]
    public void OutputFormat_FrameRateDoubled()
    {
        var input = new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(25, 1));
        using var deinterlacer = new BobDeinterlacer(input);
        Assert.Equal(50, deinterlacer.OutputFormat.FrameRate.Numerator);
        Assert.Equal(1, deinterlacer.OutputFormat.FrameRate.Denominator);
    }

    [Fact]
    public void Configure_RejectsOddHeight()
    {
        Assert.Throws<ArgumentException>(() => new BobDeinterlacer(
            new VideoFormat(4, 5, PixelFormat.Bgra32, new Rational(30, 1))));
    }

    [Fact]
    public void Configure_RejectsUnsupportedFormat()
    {
        Assert.Throws<ArgumentException>(() => new BobDeinterlacer(
            new VideoFormat(4, 4, PixelFormat.Yuyv, new Rational(30, 1))));
    }
}
