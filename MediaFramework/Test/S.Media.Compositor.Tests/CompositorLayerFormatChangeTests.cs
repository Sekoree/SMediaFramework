using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Mid-stream format-change coverage for the composition layer (the convergence's last open framework point —
/// see haplay_showsession_convergence). A slot fed frames of CHANGING dimensions — i.e. a live source that
/// switches resolution/aspect mid-stream — must keep compositing onto the fixed canvas without throwing. The
/// Next compositor handles this by design: slots hold an any-size frame, the CPU blit is dimension-agnostic,
/// the GL compositor keys layer textures by (Width, Height), and transforms resolve per frame. This test
/// guards that property (the OLD framework had a composition-layer reconfigure gap; the rewrite closed it).
/// </summary>
public sealed class CompositorLayerFormatChangeTests
{
    private static readonly Rational Fps = new(30, 1);

    private static VideoFrame Bgra(int w, int h)
    {
        var stride = w * 4;
        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(w, h, PixelFormat.Bgra32, Fps),
            [new byte[stride * h]],
            [stride]);
    }

    [Fact]
    public void Layer_slot_adapts_when_its_source_changes_resolution_midstream()
    {
        var canvas = new VideoFormat(320, 240, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = new VideoCompositorSource(canvas, compositor);
        var slot = source.AddSlot();

        // First resolution.
        slot.Output.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, Fps));
        slot.Output.Submit(Bgra(160, 120));
        Assert.True(source.TryReadNextFrame(out var f1));
        Assert.Equal(canvas.Width, f1.Format.Width);
        Assert.Equal(canvas.Height, f1.Format.Height);
        f1.Dispose();

        // Mid-stream switch to a different resolution AND aspect ratio — must not throw, still canvas-sized.
        slot.Output.Configure(new VideoFormat(200, 100, PixelFormat.Bgra32, Fps));
        slot.Output.Submit(Bgra(200, 100));
        Assert.True(source.TryReadNextFrame(out var f2));
        Assert.Equal(canvas.Width, f2.Format.Width);
        Assert.Equal(canvas.Height, f2.Format.Height);
        f2.Dispose();

        // Back to the original size — the per-size texture/scratch caches must round-trip cleanly.
        slot.Output.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, Fps));
        slot.Output.Submit(Bgra(160, 120));
        Assert.True(source.TryReadNextFrame(out var f3));
        Assert.Equal(canvas.Width, f3.Format.Width);
        Assert.Equal(canvas.Height, f3.Format.Height);
        f3.Dispose();
    }
}
