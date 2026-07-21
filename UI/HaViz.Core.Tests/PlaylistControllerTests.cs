using HaViz.Core;
using Xunit;

namespace HaViz.Core.Tests;

public sealed class PlaylistControllerTests
{
    private static PlaylistController WithTracks(int count)
    {
        var playlist = new PlaylistController();
        playlist.SetTracks(Enumerable.Range(0, count).Select(i => new TrackInfo($"uri{i}", $"track{i}")));
        return playlist;
    }

    [Fact]
    public void ManualNext_WrapsRegardlessOfLoopMode()
    {
        var playlist = WithTracks(2);
        playlist.LoopMode = LoopMode.Off;

        Assert.Equal("track0", playlist.Next()!.DisplayName);
        Assert.Equal("track1", playlist.Next()!.DisplayName);
        Assert.Equal("track0", playlist.Next()!.DisplayName); // wraps even with Loop Off
    }

    [Fact]
    public void ManualPrevious_WrapsToEnd()
    {
        var playlist = WithTracks(3);
        playlist.Next(); // track0
        Assert.Equal("track2", playlist.Previous()!.DisplayName);
    }

    [Fact]
    public void TrackEnd_LoopOff_StopsAtPlaylistEnd()
    {
        var playlist = WithTracks(2);
        playlist.LoopMode = LoopMode.Off;
        playlist.Next(); // track0

        Assert.Equal("track1", playlist.AdvanceAfterTrackEnd()!.DisplayName);
        Assert.Null(playlist.AdvanceAfterTrackEnd());
        Assert.Null(playlist.Current);
        // A fresh manual Next restarts from the top after the stop.
        Assert.Equal("track0", playlist.Next()!.DisplayName);
    }

    [Fact]
    public void TrackEnd_LoopOne_RepeatsCurrent()
    {
        var playlist = WithTracks(3);
        playlist.LoopMode = LoopMode.One;
        playlist.Next(); // track0

        Assert.Equal("track0", playlist.AdvanceAfterTrackEnd()!.DisplayName);
        Assert.Equal("track0", playlist.AdvanceAfterTrackEnd()!.DisplayName);
    }

    [Fact]
    public void TrackEnd_LoopAll_WrapsForever()
    {
        var playlist = WithTracks(2);
        playlist.LoopMode = LoopMode.All;
        playlist.Next(); // track0

        Assert.Equal("track1", playlist.AdvanceAfterTrackEnd()!.DisplayName);
        Assert.Equal("track0", playlist.AdvanceAfterTrackEnd()!.DisplayName);
    }

    [Fact]
    public void EmptyPlaylist_EverythingIsNull()
    {
        var playlist = new PlaylistController();
        Assert.Null(playlist.Next());
        Assert.Null(playlist.Previous());
        Assert.Null(playlist.AdvanceAfterTrackEnd());
    }

    [Fact]
    public void Select_OutOfRange_ReturnsNullAndKeepsPosition()
    {
        var playlist = WithTracks(2);
        playlist.Next();
        Assert.Null(playlist.Select(5));
        Assert.Equal("track0", playlist.Current!.DisplayName);
        Assert.Equal("track1", playlist.Select(1)!.DisplayName);
    }
}

public sealed class VizNdiSettingsTests
{
    [Fact]
    public void Normalized_ClampsAndEvensDimensions()
    {
        var settings = new VizNdiSettings
        {
            NdiName = "  ",
            Width = 333,
            Height = 9999,
            Fps = 0,
            PresetDurationSeconds = 1,
            BeatSensitivity = 99,
            TransitionSeconds = -5,
        }.Normalized();

        Assert.Equal("HaViz", settings.NdiName);
        Assert.Equal(332, settings.Width);   // even
        Assert.Equal(2160, settings.Height); // clamped to max, even
        Assert.Equal(1, settings.Fps);
        Assert.Equal(5, settings.PresetDurationSeconds);
        Assert.Equal(5, settings.BeatSensitivity);
        Assert.Equal(0, settings.TransitionSeconds);
    }
}

public sealed class DownmixTests
{
    [Fact]
    public void Stereo_PassesThrough()
    {
        float[] input = [0.1f, 0.2f, 0.3f, 0.4f];
        var stereo = new float[4];
        VizNdiEngine.DownmixToStereo(input, 2, stereo, 2);
        Assert.Equal(input, stereo);
    }

    [Fact]
    public void Mono_DuplicatesToBothChannels()
    {
        float[] input = [0.5f, -0.25f];
        var stereo = new float[4];
        VizNdiEngine.DownmixToStereo(input, 1, stereo, 2);
        Assert.Equal([0.5f, 0.5f, -0.25f, -0.25f], stereo);
    }

    [Fact]
    public void Multichannel_TakesFrontPair()
    {
        float[] input = [0.1f, 0.2f, 0.9f, 0.9f, 0.3f, 0.4f, 0.9f, 0.9f]; // 4ch, 2 frames
        var stereo = new float[4];
        VizNdiEngine.DownmixToStereo(input, 4, stereo, 2);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], stereo);
    }
}
