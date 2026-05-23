using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlayerViewInteractionTests
{
    [Fact]
    public void AddGroupButton_Click_AddsGroupCue()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var view = new CuePlayerView { DataContext = vm };
            var window = HostInWindow(view);
            try
            {
                var addGroup = FindButtonByContent(window, Strings.AddGroupButton);
                ClickButton(window, addGroup);

                var selected = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                Assert.Equal(CueNodeKind.Group, selected.Kind);
                Assert.Single(vm.VisibleNodes);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AddAudioRouteButton_Click_AddsRouteToSelectedMediaCue()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            vm.ApplyCueLists(
            [
                new CueList
                {
                    Name = "Act 1",
                    Nodes =
                    {
                        new MediaCueNode
                        {
                            Number = "1",
                            Label = "Track",
                            Source = new FilePlaylistItem("/tmp/track.wav"),
                        },
                    },
                },
            ]);
            vm.SelectedCueNode = vm.VisibleNodes[0];
            var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            Assert.Empty(media.AudioRoutes);

            var view = new CuePlayerView { DataContext = vm };
            var window = HostInWindow(view);
            try
            {
                var addRoute = FindButtonByContent(window, Strings.AddAudioRouteButton);
                ClickButton(window, addRoute);

                Assert.Single(media.AudioRoutes);
                Assert.Single(vm.VisibleAudioRoutes);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerViewInteractionTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static Window HostInWindow(Control view)
    {
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            Content = view,
        };

        window.Show();
        return window;
    }

    private static Button FindButtonByContent(Window window, string content) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => string.Equals(b.Content?.ToString(), content, StringComparison.Ordinal));

    private static void ClickButton(Window window, Button button)
    {
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
    }
}
