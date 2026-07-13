using System;
using System.Numerics;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Present.SDL3;
using S.Media.Session;
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

    private static VideoFrame QuadrantsBgra(int w, int h)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = y * stride + x * 4;
            var right = x >= w / 2;
            var bottom = y >= h / 2;
            (buf[o], buf[o + 1], buf[o + 2]) = (right, bottom) switch
            {
                (false, false) => ((byte)0, (byte)0, (byte)255),     // top-left red
                (true, false) => ((byte)0, (byte)255, (byte)0),     // top-right green
                (false, true) => ((byte)255, (byte)0, (byte)0),     // bottom-left blue
                _ => ((byte)255, (byte)255, (byte)255),             // bottom-right white
            };
            buf[o + 3] = 255;
        }
        return new VideoFrame(TimeSpan.Zero,
            new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(60, 1)), buf, stride, release: null);
    }

    private static VideoFrame CoordinateGradientBgra(int w, int h)
    {
        var stride = w * 4;
        var buf = new byte[stride * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = y * stride + x * 4;
            buf[o] = (byte)(255 * y / Math.Max(1, h - 1));
            buf[o + 1] = 0;
            buf[o + 2] = (byte)(255 * x / Math.Max(1, w - 1));
            buf[o + 3] = 255;
        }

        return new VideoFrame(TimeSpan.Zero,
            new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(60, 1)), buf, stride, release: null);
    }

    private static (byte B, byte G, byte R) Sample(VideoFrame frame, double nx, double ny)
    {
        var x = Math.Clamp((int)(nx * frame.Format.Width), 0, frame.Format.Width - 1);
        var y = Math.Clamp((int)(ny * frame.Format.Height), 0, frame.Format.Height - 1);
        var o = y * frame.Strides[0] + x * 4;
        var pixels = frame.Planes[0].Span;
        return (pixels[o], pixels[o + 1], pixels[o + 2]);
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
    public void Placement_destination_y_zero_is_bottom_of_gl_canvas()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var source = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(24, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            new RectNormalized(0, 0, 2f / 3, 2f / 3),
            PlacementFit.Stretch, 0, 0, 0, 0, source, canvas);
        using var srcFrame = QuadrantsBgra(source.Width, source.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);
        var top = Sample(outFrame, 0.1, 0.1);
        var bottom = Sample(outFrame, 0.1, 0.9);
        _o.WriteLine($"placement samples top={top} bottom={bottom}");
        Assert.Equal(((byte)0, (byte)0, (byte)0), top);
        Assert.True(bottom.B > 200, $"expected content at bottom, got {bottom}");
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

    [Fact]
    public void Affine_warp_scales_full_composition_to_smaller_output_without_offset_or_crop()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var output = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(60, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, canvas, canvas);
        using var srcFrame = QuadrantsBgra(canvas.Width, canvas.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        var spec = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("full", true, 0, 0, 1, 1, 0, 0, output.Width, output.Height)],
            output.Width, output.Height);
        var sections = OutputMappingResolver.Resolve(spec, canvas.Width, canvas.Height)
            .Select(s => new WarpSection(s.SourceCrop, s.Transform, s.Opacity, s.Mesh))
            .ToArray();
        comp.SetWarpPass(output, sections);

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Sample(outFrame, 0.25, 0.25));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Sample(outFrame, 0.75, 0.25));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Sample(outFrame, 0.25, 0.75));
        Assert.Equal(((byte)255, (byte)255, (byte)255), Sample(outFrame, 0.75, 0.75));
    }

    [Fact]
    public void Affine_warp_scales_top_left_composition_slice_to_full_output()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var output = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(60, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, canvas, canvas);
        using var srcFrame = QuadrantsBgra(canvas.Width, canvas.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        var spec = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("slice", true, 0, 0, 2d / 3, 2d / 3, 0, 0, output.Width, output.Height)],
            output.Width, output.Height);
        var sections = OutputMappingResolver.Resolve(spec, canvas.Width, canvas.Height)
            .Select(s => new WarpSection(s.SourceCrop, s.Transform, s.Opacity, s.Mesh))
            .ToArray();
        comp.SetWarpPass(output, sections);

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Sample(outFrame, 0.2, 0.2));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Sample(outFrame, 0.9, 0.2));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Sample(outFrame, 0.2, 0.9));
        Assert.Equal(((byte)255, (byte)255, (byte)255), Sample(outFrame, 0.9, 0.9));

        // Quadrants alone cannot distinguish a 2/3 crop from a full-frame scale because both cross the
        // quadrant boundaries. A coordinate gradient proves the far edge samples around 2/3 of the canvas.
        using var gradient = CoordinateGradientBgra(canvas.Width, canvas.Height);
        var gradientLayer = new CompositorLayer(gradient, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };
        using var gradientOutput = comp.Composite([gradientLayer], TimeSpan.Zero);
        var farEdge = Sample(gradientOutput, 0.95, 0.95);
        Assert.InRange(farEdge.R, (byte)145, (byte)180);
        Assert.InRange(farEdge.B, (byte)145, (byte)180);
    }

    [Fact]
    public void Affine_warp_scales_bottom_right_composition_slice_to_full_output()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var output = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(60, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, canvas, canvas);
        using var srcFrame = QuadrantsBgra(canvas.Width, canvas.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        // Exact reduced-ratio analogue of the reported 1920x1080 composition with a 1280x720
        // output moved (without resizing) to the bottom-right at normalized origin (1/3, 1/3).
        var spec = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("slice", true, 1d / 3, 1d / 3, 2d / 3, 2d / 3,
                0, 0, output.Width, output.Height)],
            output.Width, output.Height);
        var sections = OutputMappingResolver.Resolve(spec, canvas.Width, canvas.Height)
            .Select(s => new WarpSection(s.SourceCrop, s.Transform, s.Opacity, s.Mesh))
            .ToArray();
        comp.SetWarpPass(output, sections);

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Sample(outFrame, 0.1, 0.1));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Sample(outFrame, 0.9, 0.1));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Sample(outFrame, 0.1, 0.9));
        Assert.Equal(((byte)255, (byte)255, (byte)255), Sample(outFrame, 0.9, 0.9));
    }

    [Fact]
    public void Top_left_native_size_layer_and_top_left_output_slice_fill_output_without_black_or_crop()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        // Exact reduced-ratio analogue of a 1280x720 layer at UI (0,0) on a 1920x1080 composition,
        // with a 1280x720 output covering that top-left composition slice. The UI placement's Y=0 is
        // converted to GL's bottom-up destination coordinate: 1 - 0 - 2/3 = 1/3.
        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var source = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(24, 1));
        var output = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(60, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            new RectNormalized(0, 1f / 3, 2f / 3, 1),
            PlacementFit.Stretch, 0, 0, 0, 0, source, canvas);
        using var srcFrame = QuadrantsBgra(source.Width, source.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        var spec = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("slice", true, 0, 0, 2d / 3, 2d / 3, 0, 0, output.Width, output.Height)],
            output.Width, output.Height);
        var sections = OutputMappingResolver.Resolve(spec, canvas.Width, canvas.Height)
            .Select(s => new WarpSection(s.SourceCrop, s.Transform, s.Opacity, s.Mesh))
            .ToArray();
        comp.SetWarpPass(output, sections);

        using var outFrame = comp.Composite([layer], TimeSpan.Zero);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Sample(outFrame, 0.2, 0.2));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Sample(outFrame, 0.8, 0.2));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Sample(outFrame, 0.2, 0.8));
        Assert.Equal(((byte)255, (byte)255, (byte)255), Sample(outFrame, 0.8, 0.8));
    }

    [Fact]
    public void Live_output_layout_change_replaces_crop_and_scale_after_compositor_initialized()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        var canvas = new VideoFormat(96, 54, PixelFormat.Bgra32, new Rational(60, 1));
        var output = new VideoFormat(64, 36, PixelFormat.Bgra32, new Rational(60, 1));
        using var comp = new SDL3GLVideoCompositor(canvas);
        var (transform, crop) = PlacementResolver.Resolve(
            RectNormalized.Full, PlacementFit.Stretch, 0, 0, 0, 0, canvas, canvas);
        using var srcFrame = CoordinateGradientBgra(canvas.Width, canvas.Height);
        var layer = new CompositorLayer(srcFrame, transform, 1f, BlendMode.SourceOver) { SourceCrop = crop };

        // Initialize first, as the running ShowSession does before the layout dialog applies a live edit.
        using (var initial = comp.Composite([layer], TimeSpan.Zero))
            Assert.Equal(canvas, initial.Format);

        static WarpSection[] Resolve(VideoFormat canvas, double width, double height, VideoFormat output)
        {
            var spec = new ClipOutputMappingSpec(
                [new ClipOutputMappingSection("layout", true, 0, 0, width, height, 0, 0, output.Width, output.Height)],
                output.Width, output.Height);
            return OutputMappingResolver.Resolve(spec, canvas.Width, canvas.Height)
                .Select(s => new WarpSection(s.SourceCrop, s.Transform, s.Opacity, s.Mesh))
                .ToArray();
        }

        comp.SetWarpPass(output, Resolve(canvas, 2d / 3, 2d / 3, output));
        using (var sliced = comp.Composite([layer], TimeSpan.Zero))
        {
            Assert.Equal(output, sliced.Format);
            var farEdge = Sample(sliced, 0.95, 0.95);
            Assert.InRange(farEdge.R, (byte)145, (byte)180);
            Assert.InRange(farEdge.B, (byte)145, (byte)180);
        }

        comp.SetWarpPass(output, Resolve(canvas, 1, 1, output));
        using var full = comp.Composite([layer], TimeSpan.Zero);
        Assert.Equal(output, full.Format);
        var fullFarEdge = Sample(full, 0.95, 0.95);
        Assert.InRange(fullFarEdge.R, (byte)225, byte.MaxValue);
        Assert.InRange(fullFarEdge.B, (byte)225, byte.MaxValue);
    }
}
