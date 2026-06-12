using Avalonia.Headless;
using HaPlay.Playback;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UI rewrite P4 (plan §3.2): Now Playing group aggregate rows + proportional group seek.</summary>
public sealed class NowPlayingGroupRowTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(NowPlayingGroupRowTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static (CuePlayerViewModel Vm, CueNodeViewModel Group, CueNodeViewModel Child1, CueNodeViewModel Child2)
        CreateGroupWithTwoChildren()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.True(group.IsGroup);

        vm.AddMediaCueCommand.Execute(null);
        var child1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.SelectedCueNode = group;
        vm.AddMediaCueCommand.Execute(null);
        var child2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.Equal(2, group.Children.Count);
        return (vm, group, child1, child2);
    }

    [Fact]
    public void GroupChildren_AggregateIntoOneRow_WithLongestTimelineProgress()
    {
        DispatchUi(static () =>
        {
            var (vm, group, child1, child2) = CreateGroupWithTwoChildren();

            vm.OnCueStarted(child1.Id);
            vm.OnCueStarted(child2.Id);

            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));
            Assert.Equal(group.Id, row.GroupId);
            Assert.Equal(2, row.Children.Count);
            Assert.False(row.IsExpanded); // collapsed by default — aggregate is the glance surface

            // Longest child timeline drives the aggregate: 30 s into {60 s, 120 s} = 25 %.
            vm.OnCueProgress(new CuePlaybackProgress(child1.Id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
            vm.OnCueProgress(new CuePlaybackProgress(child2.Id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)));
            Assert.Equal(25.0, row.ProgressPercent, 1);
            Assert.Equal(120_000, row.LongestDurationMs);

            // First child ends → group row stays for the survivor; second ends → row gone.
            vm.OnCueEnded(child1.Id);
            Assert.Single(((ActiveGroupViewModel)Assert.Single(vm.NowPlayingRows)).Children);
            vm.OnCueEnded(child2.Id);
            Assert.Empty(vm.NowPlayingRows);
        });
    }

    [Fact]
    public void TopLevelCue_StaysAFlatRow()
    {
        DispatchUi(static () =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddMediaCueCommand.Execute(null);
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

            vm.OnCueStarted(cue.Id);

            Assert.IsType<ActiveCueViewModel>(Assert.Single(vm.NowPlayingRows));
        });
    }

    [Fact]
    public void GroupSeek_SeeksEveryChildProportionally_RespectingLock()
    {
        DispatchUi(static () =>
        {
            var (vm, _, child1, child2) = CreateGroupWithTwoChildren();
            var seeks = new List<(Guid CueId, TimeSpan Position)>();
            vm.SeekCueCallback = (id, pos) =>
            {
                seeks.Add((id, pos));
                return Task.CompletedTask;
            };

            vm.OnCueStarted(child1.Id);
            vm.OnCueStarted(child2.Id);
            vm.OnCueProgress(new CuePlaybackProgress(child1.Id, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
            vm.OnCueProgress(new CuePlaybackProgress(child2.Id, TimeSpan.Zero, TimeSpan.FromSeconds(120)));
            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));

            // Locked (default): nothing seeks.
            vm.SeekActiveGroupToFractionAsync(row, 0.5).GetAwaiter().GetResult();
            Assert.Empty(seeks);

            vm.NowPlayingSeekUnlocked = true;
            vm.SeekActiveGroupToFractionAsync(row, 0.5).GetAwaiter().GetResult();

            // Proportional: each child seeks to half of ITS OWN duration.
            Assert.Equal(2, seeks.Count);
            Assert.Contains(seeks, s => s.CueId == child1.Id && s.Position == TimeSpan.FromSeconds(30));
            Assert.Contains(seeks, s => s.CueId == child2.Id && s.Position == TimeSpan.FromSeconds(60));
        });
    }

    [Fact]
    public void GroupSeek_PrefersBatchedCallback_ForCoordinatedEngineSeek()
    {
        DispatchUi(static () =>
        {
            var (vm, _, child1, child2) = CreateGroupWithTwoChildren();
            var sequential = new List<Guid>();
            var batches = new List<IReadOnlyList<(Guid CueId, TimeSpan Position)>>();
            vm.SeekCueCallback = (id, _) =>
            {
                sequential.Add(id);
                return Task.CompletedTask;
            };
            vm.SeekCuesCallback = targets =>
            {
                batches.Add(targets);
                return Task.CompletedTask;
            };

            vm.OnCueStarted(child1.Id);
            vm.OnCueStarted(child2.Id);
            vm.OnCueProgress(new CuePlaybackProgress(child1.Id, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
            vm.OnCueProgress(new CuePlaybackProgress(child2.Id, TimeSpan.Zero, TimeSpan.FromSeconds(120)));
            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));

            vm.NowPlayingSeekUnlocked = true;
            vm.SeekActiveGroupToFractionAsync(row, 0.5).GetAwaiter().GetResult();

            // One coordinated batch with both children, proportional positions; no per-cue calls.
            Assert.Empty(sequential);
            var batch = Assert.Single(batches);
            Assert.Equal(2, batch.Count);
            Assert.Contains(batch, t => t.CueId == child1.Id && t.Position == TimeSpan.FromSeconds(30));
            Assert.Contains(batch, t => t.CueId == child2.Id && t.Position == TimeSpan.FromSeconds(60));
        });
    }

    [Fact]
    public void NowPlayingRows_ShowRemainingTimeInParentheses()
    {
        DispatchUi(static () =>
        {
            var (vm, _, child1, child2) = CreateGroupWithTwoChildren();

            vm.OnCueStarted(child1.Id);
            vm.OnCueStarted(child2.Id);
            vm.OnCueProgress(new CuePlaybackProgress(child1.Id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
            vm.OnCueProgress(new CuePlaybackProgress(child2.Id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)));
            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));

            Assert.Equal("00:30 / 01:00 (-00:30)", row.Children.First(c => c.CueId == child1.Id).PositionDisplay);
            // Group aggregate runs on the longest child timeline.
            Assert.Equal("00:30 / 02:00 (-01:30)", row.PositionDisplay);

            // Unknown duration (live sources): no remaining segment.
            var live = new ActiveCueViewModel(child1, child1.Id, _ => { }) { PositionMs = 30_000 };
            Assert.StartsWith("00:30 / ", live.PositionDisplay);
            Assert.DoesNotContain("(", live.PositionDisplay);
        });
    }

    [Fact]
    public void GroupCancel_CancelsEveryChild()
    {
        DispatchUi(static () =>
        {
            var (vm, _, child1, child2) = CreateGroupWithTwoChildren();
            var cancelled = new List<Guid>();
            vm.CancelCueCallback = id =>
            {
                cancelled.Add(id);
                return Task.CompletedTask;
            };

            vm.OnCueStarted(child1.Id);
            vm.OnCueStarted(child2.Id);
            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));

            row.CancelAllCommand.Execute(null);

            Assert.Equal(2, cancelled.Count);
            Assert.Contains(child1.Id, cancelled);
            Assert.Contains(child2.Id, cancelled);
        });
    }
}
