using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// P4 close-out (plan §3.1): the property drawer never shows a hidden/stale tab after a cue
/// switch, and remembers the last tab per cue type — the A→B→A scenario from the plan.
/// </summary>
public sealed class CueDrawerTabMemoryTests
{
    private const int AudioTabIndex = 2; // General, Preview, Audio, Video, Text, Action, Comment, Group

    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CueDrawerTabMemoryTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public void SwitchingCueTypes_RestoresPerTypeTab_AndNeverLeavesHiddenTabSelected()
    {
        DispatchUi(static () =>
        {
            var vm = new CuePlayerViewModel();
            var view = new CuePlayerView { DataContext = vm };
            var window = new Window { Width = 1280, Height = 800, Content = view };
            window.Show();
            try
            {
                vm.AddMediaCueCommand.Execute(null);
                var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                vm.SelectedCueNode = null;
                vm.AddGroupCommand.Execute(null);
                var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                Dispatcher.UIThread.RunJobs();

                var tabs = view.FindControl<TabControl>("CueDrawerTabs")!;

                // Operator works on the media cue's Audio tab.
                vm.SelectedCueNode = media;
                Dispatcher.UIThread.RunJobs();
                tabs.SelectedIndex = AudioTabIndex;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(AudioTabIndex, tabs.SelectedIndex);

                // Switch to the group: the Audio tab is hidden for groups, so the drawer must not
                // keep it selected (the original stale/blank-drawer bug).
                vm.SelectedCueNode = group;
                Dispatcher.UIThread.RunJobs();
                var groupTab = Assert.IsType<TabItem>(tabs.SelectedItem);
                Assert.True(groupTab.IsVisible, "drawer landed on a hidden tab for the group cue");
                Assert.NotEqual(AudioTabIndex, tabs.SelectedIndex);

                // Back to the media cue: the Audio tab is restored (per-type memory).
                vm.SelectedCueNode = media;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(AudioTabIndex, tabs.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
