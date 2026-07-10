using System.Threading;
using Avalonia.Threading;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Project/recovery restore semantics for the deck. The routing test pumps the dispatcher after the
/// apply - the per-player binding rebuild that OnSharedOutputsCollectionChanged POSTS used to run right
/// after ApplyPlayerConfig and wipe every restored selection ("outputs not connected" after a session
/// restore); tests that never ran the posted jobs couldn't see it.
/// </summary>
public sealed class MediaPlayerProjectRestoreTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerProjectRestoreTests).Assembly)
            .Dispatch(body, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static PortAudioOutputDefinition Speakers(Guid id) =>
        new(id, "Main Speakers", 0, "Alsa", 0, "dev0", 2, 48_000);

    [Fact]
    public void RestoredRoutingSelection_SurvivesTheDeferredBindingRebuild()
    {
        RunUi(() =>
        {
            var outputs = new OutputManagementViewModel();
            outputs.ReplaceDefinitionsForLoad([Speakers(Guid.NewGuid())]);
            var player = new MediaPlayerViewModel(outputs, "P1");
            Dispatcher.UIThread.RunJobs();

            player.Outputs.Single().IsSelected = true;
            var config = player.BuildPlayerConfigSnapshot();

            // Project load order (MainViewModel.ApplyProjectSnapshot): definitions replaced first -
            // fresh line INSTANCES under the same display name - then the player config applied, then
            // the dispatcher runs the rebuilds those replacements posted.
            outputs.ReplaceDefinitionsForLoad([Speakers(Guid.NewGuid())]);
            player.ApplyPlayerConfigSnapshot(config);
            Dispatcher.UIThread.RunJobs();

            var binding = Assert.Single(player.Outputs);
            Assert.True(binding.IsSelected, "restored routing selection was lost to the deferred binding rebuild");
        });
    }

    [Fact]
    public void RestoredDeck_ComesBackIdle_NotPhantomLoaded()
    {
        RunUi(() =>
        {
            var outputs = new OutputManagementViewModel();
            var player = new MediaPlayerViewModel(outputs, "P1");
            var config = player.BuildPlayerConfigSnapshot() with { MediaFilePath = "/show/main.mkv" };

            player.ApplyPlayerConfigSnapshot(config);
            Dispatcher.UIThread.RunJobs();

            // Nothing is actually open after a restore, so the deck must not LOOK loaded (the header
            // display falls back to MediaFilePath) nor start analysing the file's waveform.
            Assert.Null(player.MediaFilePath);
            Assert.Null(player.CurrentMediaDisplay);
            Assert.False(player.IsMediaLoaded);
        });
    }
}
