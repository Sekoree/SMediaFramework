using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class MediaPlayerRoutingConflictTests
{
    private static Task DispatchUi(Func<Task> action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerRoutingConflictTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public async Task Selecting_video_output_already_selected_on_another_player_can_be_cancelled()
    {
        await DispatchUi(async () =>
        {
            var (p1, p2) = CreateTwoPlayersWithOutput(
                new LocalVideoOutputDefinition(Guid.NewGuid(), "Program", VideoOutputEngine.SDLOpenGl,
                    VideoSurfaceMode.Windowed, 0, 1280, 720));
            var prompted = false;
            p2.VideoOutputRouteConflictPrompt = conflict =>
            {
                prompted = true;
                Assert.Same(p2, conflict.TargetPlayer);
                Assert.Equal("Program", conflict.OutputLine.Definition.DisplayName);
                Assert.Equal([p1], conflict.ExistingPlayers);
                return Task.FromResult(false);
            };

            p1.Outputs[0].IsSelected = true;
            p2.Outputs[0].IsSelected = true;

            await Task.Yield();
            Assert.True(prompted);
            Assert.True(p1.Outputs[0].IsSelected);
            Assert.False(p2.Outputs[0].IsSelected);
        });
    }

    [Fact]
    public async Task Confirming_video_output_conflict_rewires_selection_to_current_player()
    {
        await DispatchUi(async () =>
        {
            var (p1, p2) = CreateTwoPlayersWithOutput(
                new NDIOutputDefinition(Guid.NewGuid(), "NDI Program", "program", null,
                    NDIOutputStreamMode.VideoAndAudio, 2, 48_000));
            p2.VideoOutputRouteConflictPrompt = _ => Task.FromResult(true);

            p1.Outputs[0].IsSelected = true;
            p2.Outputs[0].IsSelected = true;

            await WaitUntilAsync(() => p2.Outputs[0].IsSelected && !p1.Outputs[0].IsSelected);
            Assert.Equal("Rewired 'NDI Program' to P2.", p2.StatusMessage);
        });
    }

    [Fact]
    public async Task Audio_outputs_can_still_be_selected_on_multiple_players()
    {
        await DispatchUi(async () =>
        {
            var (p1, p2) = CreateTwoPlayersWithOutput(
                new PortAudioOutputDefinition(Guid.NewGuid(), "PA", 0, "Alsa", 1, "dev", 2, 48_000));
            p2.VideoOutputRouteConflictPrompt = _ => throw new InvalidOperationException("audio should not prompt");

            p1.Outputs[0].IsSelected = true;
            p2.Outputs[0].IsSelected = true;

            await Task.Yield();
            Assert.True(p1.Outputs[0].IsSelected);
            Assert.True(p2.Outputs[0].IsSelected);
        });
    }

    private static (MediaPlayerViewModel P1, MediaPlayerViewModel P2) CreateTwoPlayersWithOutput(OutputDefinition output)
    {
        var outputs = new OutputManagementViewModel();
        outputs.ReplaceDefinitionsForLoad([output]);
        var p1 = new MediaPlayerViewModel(outputs, "P1");
        var p2 = new MediaPlayerViewModel(outputs, "P2");
        outputs.ActivePlayersProbe = () => [p1, p2];
        return (p1, p2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
