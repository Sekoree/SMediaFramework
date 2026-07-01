using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [Fact]
    public void Go_DispatchedStatusMessage_IsRaisedOnUiThread()
    {
        var seenFinalStatus = new ManualResetEventSlim(false);
        var statusRaisedOffUiThread = false;

        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddMediaCueCommand.Execute(null);
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.SourceOrAction = "/tmp/test.mp3";
            vm.MediaCueExecutor = async (_, _) =>
            {
                await Task.Run(static () => Thread.Sleep(10));
                return "done";
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CuePlayerViewModel.StatusMessage)
                    || vm.StatusMessage?.Contains("done", StringComparison.Ordinal) != true)
                    return;
                statusRaisedOffUiThread = !Dispatcher.UIThread.CheckAccess();
                seenFinalStatus.Set();
            };

            vm.StandbySelectedCommand.Execute(null);
            vm.OnCueStarted(cue.Id);
            vm.GoCommand.Execute(null);
        });

        PumpUntil(() => seenFinalStatus.IsSet, TimeSpan.FromSeconds(2));

        Assert.True(seenFinalStatus.IsSet);
        Assert.False(statusRaisedOffUiThread);
    }

    [Fact]
    public void Go_MediaExecutorReturnsWithoutCueStarted_RestoresCueToStandby()
    {
        var seenFailureStatus = new ManualResetEventSlim(false);
        CuePlayerViewModel? vm = null;
        CueNodeViewModel? cue = null;

        DispatchUi(() =>
        {
            vm = new CuePlayerViewModel();
            vm.AddMediaCueCommand.Execute(null);
            cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.SourceOrAction = "/tmp/test.mp4";
            vm.MediaCueExecutor = async (_, _) =>
            {
                await Task.Run(static () => Thread.Sleep(10));
                return "No cue video output could be acquired.";
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CuePlayerViewModel.StatusMessage)
                    || vm.StatusMessage?.Contains("No cue video output", StringComparison.Ordinal) != true)
                    return;
                seenFailureStatus.Set();
            };

            vm.StandbySelectedCommand.Execute(null);
            vm.GoCommand.Execute(null);
        });

        PumpUntil(() => seenFailureStatus.IsSet, TimeSpan.FromSeconds(2));

        DispatchUi(() =>
        {
            Assert.NotNull(vm);
            Assert.NotNull(cue);
            Assert.Null(vm!.CurrentCueNode);
            Assert.Same(cue, vm.StandbyCueNode);
            Assert.Same(cue, vm.SelectedCueNode);
            Assert.Contains("Failed to start", vm.StatusMessage);
            Assert.DoesNotContain("Triggered", vm.StatusMessage);
        });
    }

    [Fact]
    public void IdleCuePlacementEdit_FlagsShowDocumentStaleForNextFire()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var staleMarks = 0;
            var liveUpdates = 0;
            vm.CueClipModelStaleCallback = () => staleMarks++;
            vm.UpdateActiveCueVideoPlacementCallback = (_, _, _) => { liveUpdates++; return Task.CompletedTask; };

            vm.AddMediaCueCommand.Execute(null);
            Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            vm.AddVideoPlacementCommand.Execute(null);
            var placement = Assert.Single(vm.VisibleVideoPlacements);

            // The cue was never fired → editing its placement must flag the backing show document stale (so the
            // next GO reloads the current geometry) rather than attempting a live update on a non-running cue.
            placement.DestX = 0.25;
            placement.DestWidth = 1.0;

            Assert.True(staleMarks > 0, "idle placement edit should flag the show document stale");
            Assert.Equal(0, liveUpdates);
        });
    }

    [Fact]
    public void ActiveCuePlacementEdit_PushesLiveUpdate_WithoutMarkingStale()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var staleMarks = 0;
            var liveUpdates = 0;
            vm.CueClipModelStaleCallback = () => staleMarks++;
            vm.UpdateActiveCueVideoPlacementCallback = (_, _, _) => { liveUpdates++; return Task.CompletedTask; };

            vm.AddMediaCueCommand.Execute(null);
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            vm.AddVideoPlacementCommand.Execute(null);
            var placement = Assert.Single(vm.VisibleVideoPlacements);

            vm.OnCueStarted(cue.Id); // now running → live path, not document reload

            placement.DestX = 0.25;

            Assert.True(liveUpdates > 0, "running cue placement edit should push a live update");
            Assert.Equal(0, staleMarks);
        });
    }

    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerViewInteractionTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CuePlayerViewInteractionTests).Assembly);
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            session.Dispatch(static () => Dispatcher.UIThread.RunJobs(), CancellationToken.None);
            Thread.Sleep(10);
        }
    }

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
