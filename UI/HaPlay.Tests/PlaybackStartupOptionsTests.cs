using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Startup-option truthfulness (review P2-8): the documented UYVY passthrough switch must actually
/// change the live pipeline - and independently of logging configuration, since the parse used to be
/// nested inside the logging setup where <c>--media-log off</c> returned before reaching it.
/// </summary>
[Collection("PlaybackVideoPipelineStatics")]
public sealed class PlaybackStartupOptionsTests : IDisposable
{
    public PlaybackStartupOptionsTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        PlaybackVideoPipeline.CliRequestedUyvyPassthrough = false;
        PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = false;
    }

    [Fact]
    public void PassthroughSwitch_SetsBothPipelineFlags()
    {
        var applied = PlaybackVideoPipeline.ApplyCliStartupOptions(
            ["--media-log", "off", PlaybackVideoPipeline.UyvyPassthroughSwitch]);

        Assert.True(applied);
        Assert.True(PlaybackVideoPipeline.CliRequestedUyvyPassthrough);
        Assert.True(PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo);
    }

    [Fact]
    public void NoSwitch_LeavesFlagsUntouched()
    {
        var applied = PlaybackVideoPipeline.ApplyCliStartupOptions(["--media-log-level", "warning"]);

        Assert.False(applied);
        Assert.False(PlaybackVideoPipeline.CliRequestedUyvyPassthrough);
        Assert.False(PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo);
    }

    [Fact]
    public void CliOverride_WinsOverConflictingPersistedPreference()
    {
        // MainViewModel applies the persisted setting only when the CLI did not request passthrough
        // (MainViewModel.cs). This mirrors that guard: with the switch applied, a persisted `false`
        // must not clear the preference.
        PlaybackVideoPipeline.ApplyCliStartupOptions([PlaybackVideoPipeline.UyvyPassthroughSwitch]);

        var persistedPreference = false;
        if (!PlaybackVideoPipeline.CliRequestedUyvyPassthrough)
            PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = persistedPreference;

        Assert.True(PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo);
    }
}
