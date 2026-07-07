using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UI rewrite P1 (plan §1): workspace shell + the toast overlay queue.</summary>
public sealed class ShellAndToastTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ShellAndToastTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public void Workspaces_ContainAllSixEntries()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();

            Assert.Equal(
                [WorkspaceItem.Players, WorkspaceItem.Cues, WorkspaceItem.Soundboard, WorkspaceItem.Control, WorkspaceItem.Io, WorkspaceItem.Project],
                vm.Workspaces);
        });
    }

    [Fact]
    public void HeavyWorkspaceViews_AreExposedOnlyAfterFirstVisit_AndThenRetained()
    {
        var dir = Directory.CreateTempSubdirectory("haplay-workspace-").FullName;
        AppSettings.FilePathOverride = Path.Combine(dir, "app-settings.json");
        try
        {
            DispatchUi(static () =>
            {
                var vm = new MainViewModel();
                Assert.Same(WorkspaceItem.Players, vm.SelectedWorkspace);
                Assert.Null(vm.LoadedCueWorkspace);
                Assert.Null(vm.LoadedSoundboardWorkspace);
                Assert.Null(vm.LoadedControlWorkspace);

                vm.SelectedWorkspace = WorkspaceItem.Cues;
                Assert.Same(vm.CuePlayer, vm.LoadedCueWorkspace);
                vm.SelectedWorkspace = WorkspaceItem.Soundboard;
                Assert.Same(vm.Soundboard, vm.LoadedSoundboardWorkspace);
                vm.SelectedWorkspace = WorkspaceItem.Control;
                Assert.Same(vm.Control, vm.LoadedControlWorkspace);

                vm.SelectedWorkspace = WorkspaceItem.Players;
                Assert.Same(vm.CuePlayer, vm.LoadedCueWorkspace);
                Assert.Same(vm.Soundboard, vm.LoadedSoundboardWorkspace);
                Assert.Same(vm.Control, vm.LoadedControlWorkspace);
                vm.ShutdownCleanup();
            });
        }
        finally
        {
            AppSettings.FilePathOverride = null;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddPlayer_AddsAndSelectsNewPlayerTab()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();

            vm.AddPlayerCommand.Execute(null);

            Assert.Equal(2, vm.PlayerTabs.Count);
            Assert.Same(vm.Players[1], vm.PlayerTabs[1]);
            Assert.Same(vm.Players[1], vm.SelectedPlayer);
        });
    }

    [Theory]
    [InlineData("outputs", "io")]
    [InlineData("midi", "io")]
    [InlineData("players", "players")]
    [InlineData("io", "io")]
    [InlineData(null, null)]
    public void MigrateLegacyId_MapsMergedWorkspaces(string? stored, string? expected)
    {
        Assert.Equal(expected, WorkspaceItem.MigrateLegacyId(stored));
    }

    [Fact]
    public void Toasts_PostAppendsAndCapsAtThree()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();

            ToastCenter.Error("first");
            ToastCenter.Warn("second");
            ToastCenter.Info("third");
            ToastCenter.Info("fourth");

            Assert.Equal(3, vm.Toasts.Count);
            // Oldest ("first") was evicted to make room.
            Assert.Equal(["second", "third", "fourth"], vm.Toasts.Select(t => t.Message));
            Assert.True(vm.Toasts[0].IsWarning);
            Assert.True(vm.Toasts[1].IsInfo);
            Assert.False(vm.Toasts[1].IsError);
        });
    }

    [Fact]
    public void Toasts_CloseRemovesAndPinToggles()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();

            ToastCenter.Error("boom");
            var toast = Assert.Single(vm.Toasts);

            toast.TogglePinCommand.Execute(null);
            Assert.True(toast.IsPinned);
            toast.TogglePinCommand.Execute(null);
            Assert.False(toast.IsPinned);

            toast.CloseCommand.Execute(null);
            Assert.Empty(vm.Toasts);
        });
    }

    [Fact]
    public void Toasts_BlankMessagesAreDropped()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();

            ToastCenter.Info("");
            ToastCenter.Info("   ");

            Assert.Empty(vm.Toasts);
        });
    }
}
