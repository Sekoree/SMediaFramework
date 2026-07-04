using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <c>MediaPlayerViewModel.ResolveDeckCanvasSize</c> — the composition-canvas size the deck uses
/// on the ShowSession path. It composites at the largest driven output resolution (best quality, one downscale
/// per line) instead of a fixed 1080p, falling back to 1080p when no output advertises a locked resolution.</summary>
public sealed class MediaPlayerDeckCanvasSizeTests
{
    [Fact]
    public void NoResolutions_FallsBackTo1080p()
    {
        Assert.Equal((1920, 1080), MediaPlayerViewModel.ResolveDeckCanvasSize([]));
    }

    [Fact]
    public void SingleOutput_UsesItsResolution()
    {
        Assert.Equal((3840, 2160), MediaPlayerViewModel.ResolveDeckCanvasSize([(3840, 2160)]));
    }

    [Fact]
    public void MultipleOutputs_PicksLargestByArea()
    {
        // A 4K line and a 720p line → composite at 4K so the 4K line is native and the 720p line scales down.
        Assert.Equal((3840, 2160),
            MediaPlayerViewModel.ResolveDeckCanvasSize([(1280, 720), (3840, 2160), (1920, 1080)]));
    }

    [Fact]
    public void ZeroOrNegativeDimensions_AreIgnored()
    {
        // An auto-sized window advertises (0,0); it must not win over a real resolution nor collapse the canvas.
        Assert.Equal((1920, 1080), MediaPlayerViewModel.ResolveDeckCanvasSize([(0, 0), (1920, 1080)]));
        Assert.Equal((1920, 1080), MediaPlayerViewModel.ResolveDeckCanvasSize([(0, 0), (-1, -1)])); // all invalid → fallback
    }

    [Fact]
    public void PortraitVsLandscape_ComparesByPixelArea()
    {
        // 1080x1920 (portrait 4K/2? ~2.07MP) vs 1920x1080 (~2.07MP) are equal area → first-seen wins (stable).
        Assert.Equal((1080, 1920), MediaPlayerViewModel.ResolveDeckCanvasSize([(1080, 1920), (1920, 1080)]));
    }
}
