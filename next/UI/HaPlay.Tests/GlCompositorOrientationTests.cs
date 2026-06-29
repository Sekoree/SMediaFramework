using System;
using System.Numerics;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Present.SDL3;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

// Verifies that a direct-uploaded BGRA32 layer (still image / rendered text path) composites
// right-side-up on the GL compositor, matching the YUV path. Regression for image/text cues
// appearing vertically flipped. Skips when no GL context is available (pure-CPU CI).
public class GlCompositorOrientationTests
{
    private readonly ITestOutputHelper _o;
    public GlCompositorOrientationTests(ITestOutputHelper o) => _o = o;

    private static VideoFrame TopMarkedBgra(int w, int h)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h / 4; y++)        // opaque white band across the TOP quarter
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                buf[o] = 255; buf[o + 1] = 255; buf[o + 2] = 255; buf[o + 3] = 255;
            }
        return new VideoFrame(TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)), buf, stride, release: null);
    }

    private static VideoFrame LeftBlueRightRedBgra(int w, int h)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                var rightHalf = x >= w / 2;
                buf[o] = rightHalf ? (byte)0 : (byte)255;
                buf[o + 1] = 0;
                buf[o + 2] = rightHalf ? (byte)255 : (byte)0;
                buf[o + 3] = 255;
            }
        }

        return new VideoFrame(TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)), buf, stride, release: null);
    }

    [Fact]
    public void Bgra32_layer_full_frame_is_not_vertically_flipped()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        int w = 64, h = 64;
        var canvas = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            new RectNormalized(0f, 0f, 1f, 1f), PlacementFit.Stretch, 0f, 0f, 0f, 0f, canvas, canvas);
        using var srcFrame = TopMarkedBgra(w, h);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };
        using var outFrame = comp.Composite(new[] { layer }, TimeSpan.Zero);

        var span = outFrame.Planes[0].Span;
        int stride = outFrame.Strides[0];
        long topA = 0, botA = 0;
        for (int x = 0; x < w; x++)
        {
            topA += span[1 * stride + x * 4 + 3];
            botA += span[(h - 2) * stride + x * 4 + 3];
        }
        _o.WriteLine($"GL composite: topAlpha={topA} bottomAlpha={botA}");
        Assert.True(topA > botA, $"Top-marked BGRA layer should stay at TOP after GL composite (topA={topA}, botA={botA})");
    }

    [Fact]
    public void Mesh_warp_pass_preserves_source_crop()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        int w = 64, h = 32;
        var canvas = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, canvas, canvas);
        using var srcFrame = LeftBlueRightRedBgra(w, h);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        var mesh = new WarpMesh(2, 2,
        [
            new Vector2(0, 0),
            new Vector2(w, 0),
            new Vector2(0, h),
            new Vector2(w, h - 2),
        ]);
        comp.SetWarpPass(canvas, [new WarpSection(new RectNormalized(0.5f, 0f, 1f, 1f), LayerTransform2D.Identity, 1f, mesh)]);

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);

        var span = outFrame.Planes[0].Span;
        int stride = outFrame.Strides[0];
        int leftCenter = (h / 2) * stride + (w / 4) * 4;
        var blue = span[leftCenter];
        var red = span[leftCenter + 2];
        _o.WriteLine($"GL mesh crop: leftCenter red={red} blue={blue}");
        Assert.True(red > 200 && blue < 50, $"Mesh warp should sample the right-half crop, not the full canvas (red={red}, blue={blue}).");
    }
}
