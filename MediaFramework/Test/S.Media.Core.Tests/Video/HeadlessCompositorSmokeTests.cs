using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class HeadlessCompositorSmokeTests
{
    [Fact]
    public void CpuCompositor_DeterministicProgramFrame_HasExpectedChecksum()
    {
        var format = new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
        using var background = new VideoFrame(TimeSpan.Zero, format, MakeSolid(4, 4, 10, 20, 30, 255), 16);
        using var foreground = new VideoFrame(TimeSpan.Zero,
            new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)),
            MakeSolid(2, 2, 0, 100, 200, 255),
            8);

        using var compositor = new CpuVideoCompositor(format);
        using var result = compositor.Composite(
            [
                new CompositorLayer(background, LayerTransform2D.Identity, 1f, BlendMode.Source),
                new CompositorLayer(foreground, LayerTransform2D.Translate(1, 1), 0.5f, BlendMode.SourceOver),
            ],
            TimeSpan.FromMilliseconds(33));

        Assert.Equal(TimeSpan.FromMilliseconds(33), result.PresentationTime);
        Assert.Equal(4 * 4 * 4, result.Planes[0].Length);
        Assert.Equal(5_520u, Checksum(result.Planes[0].Span));
    }

    private static byte[] MakeSolid(int w, int h, byte b, byte g, byte r, byte a)
    {
        var buffer = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buffer[i * 4 + 0] = b;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = r;
            buffer[i * 4 + 3] = a;
        }

        return buffer;
    }

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        foreach (var b in bytes)
            sum += b;
        return sum;
    }
}
