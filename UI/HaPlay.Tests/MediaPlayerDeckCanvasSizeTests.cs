using HaPlay.Models;
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

    // ── ResolveDeckCanvas: the media-player raster (size + rate), autosized to the source. ──────────────

    [Fact]
    public void AsSource_MatchesTheSourceRasterAndRate_NotTheOutput()
    {
        // A 9:16 vertical clip on a 16:9 output: the canvas is the SOURCE raster (so the OUTPUT letterboxes it),
        // NOT the 1920x1080 output (which would crop it to cover — the reported bug).
        var canvas = MediaPlayerViewModel.ResolveDeckCanvas(
            PlayerOutputPreset.AsSource, 0, 0,
            sourceWidth: 1080, sourceHeight: 1920, sourceFpsNum: 60, sourceFpsDen: 1,
            outputResolutions: [(1920, 1080)]);
        Assert.Equal((1080, 1920, 60, 1), canvas);
    }

    [Fact]
    public void AsSource_UnknownSourceRaster_FallsBackToTheDrivenOutput()
    {
        // A live input exposes no raster up front → fall back to the largest driven output; rate defaults to 30.
        var canvas = MediaPlayerViewModel.ResolveDeckCanvas(
            PlayerOutputPreset.AsSource, 0, 0,
            sourceWidth: 0, sourceHeight: 0, sourceFpsNum: 0, sourceFpsDen: 0,
            outputResolutions: [(1280, 720), (3840, 2160)]);
        Assert.Equal((3840, 2160, 30, 1), canvas);
    }

    [Fact]
    public void FixedPreset_SizesToThePresetRaster_IgnoringSourceAndOutput()
    {
        // A 720p60 preset pins the canvas to 1280x720@60 regardless of the (4K) source and (1080p) output — the
        // source is then letterboxed into it by the clip placement.
        var canvas = MediaPlayerViewModel.ResolveDeckCanvas(
            PlayerOutputPreset.Preset720p60, 0, 0,
            sourceWidth: 3840, sourceHeight: 2160, sourceFpsNum: 24, sourceFpsDen: 1,
            outputResolutions: [(1920, 1080)]);
        Assert.Equal((1280, 720, 60, 1), canvas);
    }

    [Fact]
    public void CustomPreset_SizesToTheCustomRaster_AtTheSourceRate()
    {
        var canvas = MediaPlayerViewModel.ResolveDeckCanvas(
            PlayerOutputPreset.Custom, customWidth: 2560, customHeight: 1440,
            sourceWidth: 1920, sourceHeight: 1080, sourceFpsNum: 25, sourceFpsDen: 1,
            outputResolutions: []);
        Assert.Equal((2560, 1440, 25, 1), canvas);
    }
}
