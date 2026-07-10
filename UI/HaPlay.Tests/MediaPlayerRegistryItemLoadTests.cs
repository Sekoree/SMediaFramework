using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Regression for the 2026-07-03 report "a YouTube deck item is instantly done": the deck's
/// <c>CanLoadMedia</c> gate predated the registry-URI item kinds (youtube:// / mmd://), so playing one
/// silently no-oped - no open, no error, nothing in the log. Playing such an item must REACH the open
/// path (and surface ITS error when the asset/model is missing), never return silently idle.
/// </summary>
public sealed class MediaPlayerRegistryItemLoadTests
{
    private static Task DispatchUi(Func<Task> action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerRegistryItemLoadTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static MediaPlayerViewModel CreatePlayer()
    {
        var outputs = new OutputManagementViewModel();
        var player = new MediaPlayerViewModel(outputs, "P1");
        outputs.ActivePlayersProbe = () => [player];
        return player;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
    }

    [Fact]
    public Task PlayingAnUnpreparedYouTubeItem_ReachesTheOpenPath_AndSurfacesItsError() =>
        DispatchUi(async () =>
        {
            var player = CreatePlayer();
            var item = new YouTubePlaylistItem("dQw4w9WgXcQ")
            {
                Title = "unprepared",
                AudioOnly = true,
                AudioStreamDescriptor = "test-no-such-descriptor|none|", // no cache entry → open error
            };

            await player.PlayPlaylistItemAsync(item);

            // The gate must admit the item: the open runs and FAILS actionably (nothing is prepared in
            // this test env) - the silent-no-op regression left LastLoadError null and the deck idle.
            await WaitUntilAsync(() => player.LastLoadError is not null || player.IsMediaLoaded);
            Assert.True(player.LastLoadError is not null || player.IsMediaLoaded,
                "playing a YouTube item neither opened nor surfaced an open error - the CanLoadMedia gate silently dropped it");
        });

    [Fact]
    public Task PlayingAnMMDItemWithAMissingModel_ReachesTheOpenPath_AndSurfacesItsError() =>
        DispatchUi(async () =>
        {
            var player = CreatePlayer();
            var item = new MMDPlaylistItem("/nonexistent/model.pmx");

            await player.PlayPlaylistItemAsync(item);

            await WaitUntilAsync(() => player.LastLoadError is not null || player.IsMediaLoaded);
            Assert.True(player.LastLoadError is not null || player.IsMediaLoaded,
                "playing an MMD item neither opened nor surfaced an open error - the CanLoadMedia gate silently dropped it");
        });
}
