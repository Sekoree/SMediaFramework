using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class CompositionSizingTests
{
    [Fact]
    public void Local_video_output_with_window_size_reports_its_resolution()
    {
        var def = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Projector", default, default, ScreenIndex: 0,
            WindowWidth: 1280, WindowHeight: 720);

        Assert.True(CuePlayerViewModel.TryGetOutputResolution(def, out var w, out var h));
        Assert.Equal((1280, 720), (w, h));
    }

    [Fact]
    public void Local_video_output_without_window_size_has_no_resolution()
    {
        var def = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Projector", default, default, ScreenIndex: 0,
            WindowWidth: null, WindowHeight: null);

        Assert.False(CuePlayerViewModel.TryGetOutputResolution(def, out _, out _));
    }

    [Fact]
    public void Ndi_output_with_resolution_lock_reports_its_resolution()
    {
        var def = new NDIOutputDefinition(
            Guid.NewGuid(), "Wall", "wall", null, default, AudioChannelCount: 2, AudioSampleRate: 48_000,
            ResolutionLockWidth: 3840, ResolutionLockHeight: 2160);

        Assert.True(CuePlayerViewModel.TryGetOutputResolution(def, out var w, out var h));
        Assert.Equal((3840, 2160), (w, h));
    }

    [Fact]
    public void Ndi_output_without_resolution_lock_has_no_resolution()
    {
        var def = new NDIOutputDefinition(
            Guid.NewGuid(), "Wall", "wall", null, default, AudioChannelCount: 2, AudioSampleRate: 48_000);

        Assert.False(CuePlayerViewModel.TryGetOutputResolution(def, out _, out _));
    }
}
