using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.ViewModels;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

// Resolution lookup used by output setup dialogs and media-player composition canvas selection.
public sealed class CompositionSizingTests
{
    private static VideoFormat SourceFormat => new(1920, 1080, PixelFormat.Bgra32, new Rational(60, 1));

    [Fact]
    public void Local_video_output_with_window_size_reports_its_resolution()
    {
        var def = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Projector", default, default, ScreenIndex: 0,
            WindowWidth: 1280, WindowHeight: 720);

        Assert.True(HaPlayPlaybackSession.TryGetOutputResolution(def, out var w, out var h));
        Assert.Equal((1280, 720), (w, h));
    }

    [Fact]
    public void Local_video_output_without_window_size_has_no_resolution()
    {
        var def = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Projector", default, default, ScreenIndex: 0,
            WindowWidth: null, WindowHeight: null);

        Assert.False(HaPlayPlaybackSession.TryGetOutputResolution(def, out _, out _));
    }

    [Fact]
    public void Ndi_output_with_resolution_lock_reports_its_resolution()
    {
        var def = new NDIOutputDefinition(
            Guid.NewGuid(), "Wall", "wall", null, default, AudioChannelCount: 2, AudioSampleRate: 48_000,
            ResolutionLockWidth: 3840, ResolutionLockHeight: 2160);

        Assert.True(HaPlayPlaybackSession.TryGetOutputResolution(def, out var w, out var h));
        Assert.Equal((3840, 2160), (w, h));
    }

    [Fact]
    public void Ndi_output_without_resolution_lock_has_no_resolution()
    {
        var def = new NDIOutputDefinition(
            Guid.NewGuid(), "Wall", "wall", null, default, AudioChannelCount: 2, AudioSampleRate: 48_000);

        Assert.False(HaPlayPlaybackSession.TryGetOutputResolution(def, out _, out _));
    }

    [Fact]
    public void Media_player_composition_canvas_uses_source_raster_for_local_resizable_outputs()
    {
        var lines = new[]
        {
            Line(new LocalVideoOutputDefinition(
                Guid.NewGuid(), "Program", VideoOutputEngine.SdlOpenGl, VideoSurfaceMode.Windowed, 0,
                WindowWidth: 640, WindowHeight: 480)),
        };

        var size = HaPlayPlaybackSession.MediaPlayerCompositionCanvasSize(lines, SourceFormat);

        Assert.Equal((1920, 1080), size);
    }

    [Fact]
    public void Media_player_composition_canvas_uses_video_ndi_resolution_lock()
    {
        var lines = new[]
        {
            Line(new LocalVideoOutputDefinition(
                Guid.NewGuid(), "Program", VideoOutputEngine.SdlOpenGl, VideoSurfaceMode.Windowed, 0,
                WindowWidth: 1280, WindowHeight: 720)),
            Line(new NDIOutputDefinition(
                Guid.NewGuid(), "Wall", "wall", null, NDIOutputStreamMode.VideoOnly, 2, 48_000,
                ResolutionLockWidth: 3840, ResolutionLockHeight: 2160)),
        };

        var size = HaPlayPlaybackSession.MediaPlayerCompositionCanvasSize(lines, SourceFormat);

        Assert.Equal((3840, 2160), size);
    }

    [Fact]
    public void Media_player_composition_canvas_ignores_audio_only_ndi_resolution_lock()
    {
        var lines = new[]
        {
            Line(new NDIOutputDefinition(
                Guid.NewGuid(), "Audio", "audio", null, NDIOutputStreamMode.AudioOnly, 2, 48_000,
                ResolutionLockWidth: 3840, ResolutionLockHeight: 2160)),
        };

        var size = HaPlayPlaybackSession.MediaPlayerCompositionCanvasSize(lines, SourceFormat);

        Assert.Equal((1920, 1080), size);
    }

    [Theory]
    // The free-running composition pump must oversample the source (>= 2x, >= 60fps) so it never beats
    // 1:1 against the audio-paced decoder and drops/repeats frames at the latest-wins layer slot.
    [InlineData(24, 1, 60, 1)]      // 24fps  -> 60 (2x = 48 < 60, floor to 60)
    [InlineData(25, 1, 60, 1)]      // 25fps  -> 60
    [InlineData(30, 1, 60, 1)]      // 30fps  -> 60 (2x = 60)
    [InlineData(50, 1, 100, 1)]     // 50fps  -> 100 (2x already >= 60)
    [InlineData(60, 1, 120, 1)]     // 60fps  -> 120 (2x; a 60 pump would itself beat a 60 source)
    [InlineData(24000, 1001, 60, 1)] // 23.976 -> 60
    [InlineData(0, 1, 60, 1)]       // unknown -> 60
    public void Composition_pump_rate_oversamples_the_source(int srcNum, int srcDen, int expNum, int expDen)
    {
        var rate = HaPlayPlaybackSession.OversampleCompositionPumpRate(new Rational(srcNum, srcDen));

        var rateFps = rate.Numerator / (double)rate.Denominator;
        var expectedFps = expNum / (double)expDen;
        Assert.Equal(expectedFps, rateFps, 3);
        // Always at least 60fps and at least double the (known) source rate.
        Assert.True(rateFps >= 60.0 - 1e-6);
        if (srcNum > 0)
            Assert.True(rateFps >= 2.0 * (srcNum / (double)srcDen) - 1e-6);
    }

    private static OutputLineViewModel Line(OutputDefinition definition) => new(definition, _ => { });
}
