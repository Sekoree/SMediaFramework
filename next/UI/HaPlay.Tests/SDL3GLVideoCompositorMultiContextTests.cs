using System;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Present.SDL3;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

// Repro for the chained-stage scenario (mixer compositor + composition-FX / per-output mapping
// compositor) that runs two SDL3GLVideoCompositor instances on the SAME thread. Compositors on one
// thread now share a single GL context (SharedSdlGlContext), so they can no longer displace each
// other's "current" binding; before that fix the second instance left its private context current and
// the first rendered/read back through the wrong (differently sized) context — producing corrupted
// ("red/flipped/flickering") frames. This guards both the sharing and the per-Composite make-current.
// Skips on pure-CPU CI.
public sealed class SDL3GLVideoCompositorMultiContextTests
{
    private readonly ITestOutputHelper _o;
    public SDL3GLVideoCompositorMultiContextTests(ITestOutputHelper o) => _o = o;

    private static VideoFrame SolidBgra(int w, int h, byte b, byte g, byte r)
    {
        var stride = w * 4;
        var buf = new byte[stride * h];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
        }
        return new VideoFrame(TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)), buf, stride, release: null);
    }

    private static (byte B, byte G, byte R) CenterPixel(VideoFrame frame)
    {
        var span = frame.Planes[0].Span;
        var stride = frame.Strides[0];
        var o = (frame.Format.Height / 2) * stride + (frame.Format.Width / 2) * 4;
        return (span[o], span[o + 1], span[o + 2]);
    }

    [Fact]
    public void Two_compositors_on_one_thread_do_not_corrupt_each_other()
    {
        if (!SDL3GLVideoCompositor.TryProbe(out var err))
        {
            _o.WriteLine("GL unavailable, skipping: " + err);
            return;
        }

        // Different sizes — like a 1080p mixer/canvas next to a 720p mapping/FX compositor. Same-size
        // contexts can mask the bug because their GL object names line up; mismatched sizes expose it.
        var fmtA = new VideoFormat(128, 64, PixelFormat.Bgra32, new Rational(30, 1));
        var fmtB = new VideoFormat(64, 128, PixelFormat.Bgra32, new Rational(30, 1));
        using var a = new SDL3GLVideoCompositor(fmtA);
        using var b = new SDL3GLVideoCompositor(fmtB);

        var (ta, ca) = PlacementResolver.Resolve(RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, fmtA, fmtA);
        var (tb, cb) = PlacementResolver.Resolve(RectNormalized.Full, PlacementFit.Stretch, 0f, 0f, 0f, 0f, fmtB, fmtB);

        using var redSrcA = SolidBgra(fmtA.Width, fmtA.Height, 0, 0, 255);    // pure red
        using var blueSrcB = SolidBgra(fmtB.Width, fmtB.Height, 255, 0, 0);   // pure blue

        // Warm both contexts (initializes each, leaving b's context current last).
        using (var _ = a.Composite([new CompositorLayer(redSrcA, ta, 1f, BlendMode.Source) { SourceCrop = ca }], TimeSpan.Zero)) { }
        using (var _ = b.Composite([new CompositorLayer(blueSrcB, tb, 1f, BlendMode.Source) { SourceCrop = cb }], TimeSpan.Zero)) { }

        // Now composite red through A again. With b's context current, A must re-make its own context
        // current or it reads back garbage.
        using var aFrame = a.Composite([new CompositorLayer(redSrcA, ta, 1f, BlendMode.Source) { SourceCrop = ca }], TimeSpan.Zero);
        var (b1, g1, r1) = CenterPixel(aFrame);
        _o.WriteLine($"A after B: BGR=({b1},{g1},{r1}) expected ~(0,0,255) red");

        Assert.True(r1 > 200 && b1 < 50 && g1 < 50,
            $"Compositor A produced corrupted output after Compositor B ran on the same thread: BGR=({b1},{g1},{r1}).");
    }
}
