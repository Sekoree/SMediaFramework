using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class CpuVideoCompositorTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void SingleLayer_Source_ExactCopy()
    {
        // Source: 4x4 solid red (premul BGRA: B=0,G=0,R=255,A=255).
        var srcPlane = MakeSolid(4, 4, 0, 0, 255, 255);
        using var srcFrame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, srcPlane, 4 * 4, release: null);
        using var c = new CpuVideoCompositor(Bgra32_4x4);
        var layer = new CompositorLayer(srcFrame, LayerTransform2D.Identity, 1f, BlendMode.Source);
        var result = c.Composite([layer], TimeSpan.FromMilliseconds(10));
        try
        {
            Assert.Equal(TimeSpan.FromMilliseconds(10), result.PresentationTime);
            var span = result.Planes[0].Span;
            for (var p = 0; p < 4 * 4; p++)
            {
                Assert.Equal(0, span[p * 4 + 0]);
                Assert.Equal(0, span[p * 4 + 1]);
                Assert.Equal(255, span[p * 4 + 2]);
                Assert.Equal(255, span[p * 4 + 3]);
            }
        }
        finally { result.Dispose(); }
    }

    [Fact]
    public void TwoLayers_SourceOver_HalfOpacityTop_BlendsToward()
    {
        // Base layer: solid blue (255,0,0,255). Top: solid red at 50% opacity over it.
        var basePlane = MakeSolid(4, 4, 255, 0, 0, 255);
        var topPlane = MakeSolid(4, 4, 0, 0, 255, 255);
        using var baseFrame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, basePlane, 16, release: null);
        using var topFrame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, topPlane, 16, release: null);
        using var c = new CpuVideoCompositor(Bgra32_4x4);
        var bl = new CompositorLayer(baseFrame, LayerTransform2D.Identity, 1f, BlendMode.Source);
        var top = new CompositorLayer(topFrame, LayerTransform2D.Identity, 0.5f, BlendMode.SourceOver);
        var result = c.Composite([bl, top], TimeSpan.Zero);
        try
        {
            // SourceOver math (premul): out.rgb = src.rgb*opacity + dst.rgb*(1 - src.a*opacity/255).
            // src.rgb (red, premul) = (0,0,255). opacity = 0.5 → 0.5 * (0,0,255) = (0,0,127.5≈128).
            // (1 - 255*0.5/255) = 0.5. dst = (255,0,0). Add: B=0+128=128, G=0, R=128+0=128.
            var span = result.Planes[0].Span;
            Assert.InRange(span[0], 126, 130); // B near 128
            Assert.Equal(0, span[1]);
            Assert.InRange(span[2], 126, 130); // R near 128
        }
        finally { result.Dispose(); }
    }

    [Fact]
    public void Multiply_GrayOnWhite_ProducesGray()
    {
        // dst = solid white, src = solid mid-gray (128,128,128,255). Multiply at full opacity.
        // Expected: dst.rgb = white * gray / 255 = (128, 128, 128).
        var basePlane = MakeSolid(4, 4, 255, 255, 255, 255);
        var grayPlane = MakeSolid(4, 4, 128, 128, 128, 255);
        using var baseFrame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, basePlane, 16, release: null);
        using var grayFrame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, grayPlane, 16, release: null);
        using var c = new CpuVideoCompositor(Bgra32_4x4);
        var bl = new CompositorLayer(baseFrame, LayerTransform2D.Identity, 1f, BlendMode.Source);
        var mul = new CompositorLayer(grayFrame, LayerTransform2D.Identity, 1f, BlendMode.Multiply);
        var result = c.Composite([bl, mul], TimeSpan.Zero);
        try
        {
            var span = result.Planes[0].Span;
            Assert.InRange(span[0], 127, 129);
            Assert.InRange(span[1], 127, 129);
            Assert.InRange(span[2], 127, 129);
        }
        finally { result.Dispose(); }
    }

    [Fact]
    public void Translate_PlacesLayerAtKnownOffset()
    {
        // 8x8 canvas, 2x2 layer translated by (3, 2). After composite:
        //   layer occupies dst rows 2..3, columns 3..4 (inclusive); other pixels remain 0/transparent.
        var canvasFormat = new VideoFormat(8, 8, PixelFormat.Bgra32, new Rational(30, 1));
        var smallPlane = MakeSolid(2, 2, 0, 255, 0, 255); // green
        using var smallFrame = new VideoFrame(TimeSpan.Zero, new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)),
            smallPlane, 2 * 4, release: null);
        using var c = new CpuVideoCompositor(canvasFormat);
        var layer = new CompositorLayer(smallFrame, LayerTransform2D.Translate(3, 2), 1f, BlendMode.Source);
        var result = c.Composite([layer], TimeSpan.Zero);
        try
        {
            var span = result.Planes[0].Span;
            // Pixel at (3,2): green.
            var idx = (2 * 8 + 3) * 4;
            Assert.Equal(0, span[idx + 0]);
            Assert.Equal(255, span[idx + 1]);
            Assert.Equal(0, span[idx + 2]);
            Assert.Equal(255, span[idx + 3]);
            // Pixel at (0,0): unchanged transparent black.
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(0, span[2]);
            Assert.Equal(0, span[3]);
            // Pixel at (5,2) is just outside the layer (it covers x=3..4 inclusive).
            var outside = (2 * 8 + 5) * 4;
            Assert.Equal(0, span[outside + 3]);
        }
        finally { result.Dispose(); }
    }

    [Fact]
    public void Scale2x_ExpandsLayerByNearestNeighbor()
    {
        // 2x2 layer scaled 2x onto a 4x4 canvas — every dst pixel inside maps to a single src pixel.
        var srcPlane = new byte[2 * 2 * 4];
        // Color each of the 4 source pixels distinctly.
        // (0,0): B=10,G=20,R=30,A=255
        WritePixel(srcPlane, idx: 0, 10, 20, 30, 255);
        WritePixel(srcPlane, idx: 1, 11, 21, 31, 255);
        WritePixel(srcPlane, idx: 2, 12, 22, 32, 255);
        WritePixel(srcPlane, idx: 3, 13, 23, 33, 255);
        using var srcFrame = new VideoFrame(TimeSpan.Zero,
            new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)), srcPlane, 2 * 4, release: null);
        using var c = new CpuVideoCompositor(Bgra32_4x4);
        var layer = new CompositorLayer(srcFrame, LayerTransform2D.Scale(2f, 2f), 1f, BlendMode.Source);
        var result = c.Composite([layer], TimeSpan.Zero);
        try
        {
            var span = result.Planes[0].Span;
            // dst (0,0) → src (~0.25, 0.25) → src pixel (0,0).
            Assert.Equal(10, span[0]);
            Assert.Equal(20, span[1]);
            Assert.Equal(30, span[2]);
            // dst (3,3) → src (~1.75, 1.75) → src pixel (1,1) (index 3).
            var idx33 = (3 * 4 + 3) * 4;
            Assert.Equal(13, span[idx33 + 0]);
            Assert.Equal(23, span[idx33 + 1]);
            Assert.Equal(33, span[idx33 + 2]);
        }
        finally { result.Dispose(); }
    }

    [Fact]
    public void NonBgra32LayerThrows()
    {
        var nv12Format = new VideoFormat(2, 2, PixelFormat.Nv12, new Rational(30, 1));
        var planes = new ReadOnlyMemory<byte>[] { new byte[4], new byte[2] };
        using var srcFrame = new VideoFrame(TimeSpan.Zero, nv12Format, planes, new[] { 2, 2 }, release: null);
        using var c = new CpuVideoCompositor(Bgra32_4x4);
        var layer = new CompositorLayer(srcFrame, LayerTransform2D.Identity, 1f, BlendMode.Source);
        Assert.Throws<InvalidOperationException>(() => c.Composite([layer], TimeSpan.Zero));
    }

    private static byte[] MakeSolid(int w, int h, byte b, byte g, byte r, byte a)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static void WritePixel(byte[] buf, int idx, byte b, byte g, byte r, byte a)
    {
        buf[idx * 4 + 0] = b;
        buf[idx * 4 + 1] = g;
        buf[idx * 4 + 2] = r;
        buf[idx * 4 + 3] = a;
    }
}
