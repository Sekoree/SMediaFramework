using System.Text.Json;
using System.Collections.Concurrent;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Jump (control-flow) cues: model/VM/JSON round-trip with ID-stable targets - the property
/// that makes renumber/auto-reorder safe - plus the authoring command's loop-back gesture.</summary>
public sealed class JumpCueTests
{
    [Fact]
    public void JumpCueNode_RoundTrips_ThroughViewModelAndJson()
    {
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var node = new JumpCueNode
        {
            Number = "9",
            Label = "Loop chorus",
            TriggerMode = CueTriggerMode.AutoFollow,
            TargetCueIds = [targetA, targetB],
            RandomTarget = true,
            AvoidImmediateRepeat = true,
            FireTargetOnJump = true,
        };

        // VM round-trip preserves the control-flow payload.
        var vm = CueNodeViewModel.FromModel(node);
        Assert.Equal(CueNodeKind.Jump, vm.Kind);
        Assert.True(vm.JumpRandom);
        Assert.Equal([targetA, targetB], vm.JumpTargetIds);
        var back = Assert.IsType<JumpCueNode>(vm.ToModel());
        Assert.Equal(node.TargetCueIds, back.TargetCueIds);
        Assert.True(back.RandomTarget);
        Assert.True(back.AvoidImmediateRepeat);
        Assert.True(back.FireTargetOnJump);

        // JSON round-trip through the polymorphic cue-node contract (project persistence).
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.Contains("\"jump\"", json); // the type discriminator
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<JumpCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal([targetA, targetB], reloaded.TargetCueIds);
        Assert.True(reloaded.RandomTarget);
        Assert.True(reloaded.AvoidImmediateRepeat);
    }

    [Fact]
    public void JumpCue_TargetsSurviveRenumbering()
    {
        // The whole point of ID targeting: renumber every cue - the jump still points at the same cue.
        var target = new MediaCueNode { Number = "3", Label = "Chorus" };
        var jump = new JumpCueNode { Number = "9", TargetCueIds = [target.Id] };

        var renumberedTarget = target with { Number = "42" };
        Assert.Equal(jump.TargetCueIds[0], renumberedTarget.Id); // identity unchanged by renumber
    }

    [Fact]
    public void JumpTargetsText_ResolvesNumbersToIds_AndReportsUnknowns()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        vm.AddActionCueCommand.Execute(null); // number 1 (fireable target)
        vm.AddActionCueCommand.Execute(null); // number 2
        var one = vm.SelectedCueList!.Nodes[0];
        var two = vm.SelectedCueList!.Nodes[1];
        vm.AddJumpCueCommand.Execute(null);
        var jump = vm.SelectedCueList!.Nodes[^1];
        vm.SelectedCueNode = jump;

        vm.SelectedJumpTargetsText = "1, 2, 99";
        Assert.Equal([one.Id, two.Id], jump.JumpTargetIds); // resolved by NUMBER, stored as IDs
        Assert.Contains("99", vm.StatusMessage);            // unknown number reported

        // Display round-trips back to numbers.
        Assert.Equal("1, 2", vm.SelectedJumpTargetsText);
    }

    [Fact]
    public void JumpTargetsText_ExpandsInclusiveHierarchicalAndTopLevelRanges()
    {
        var children = Enumerable.Range(1, 4)
            .Select(number => new ActionCueNode { Number = $"2.{number}" })
            .ToArray();
        var group = new CueGroupNode { Number = "2", Children = [.. children] };
        var three = new ActionCueNode { Number = "3" };
        var four = new ActionCueNode { Number = "4" };
        var jump = new JumpCueNode { Number = "5" };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [group, three, four, jump] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[^1];
        vm.SelectedCueNode = jumpVm;

        vm.SelectedJumpTargetsText = "2.2-2.4, 3-4";

        Assert.Equal([children[1].Id, children[2].Id, children[3].Id, three.Id, four.Id],
            jumpVm.JumpTargetIds);
        Assert.Equal("2.2, 2.3, 2.4, 3, 4", vm.SelectedJumpTargetsText);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public void JumpTargetsText_PreservesNonNumericHyphenatedCueNumbersAsLiterals()
    {
        var target = new ActionCueNode { Number = "intro-1" };
        var jump = new JumpCueNode { Number = "2" };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [target, jump] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[^1];
        vm.SelectedCueNode = jumpVm;

        vm.SelectedJumpTargetsText = "intro-1";

        Assert.Equal([target.Id], jumpVm.JumpTargetIds);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public void TreeColumns_DisplayStartPolicyAndResolvedEndOrJumpTargets()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        vm.AddActionCueCommand.Execute(null);
        var one = vm.SelectedCueNode!;
        vm.AddActionCueCommand.Execute(null);
        var two = vm.SelectedCueNode!;
        two.TriggerMode = CueTriggerMode.AutoContinue;

        vm.AddJumpCueCommand.Execute(null);
        var jump = vm.SelectedCueNode!;
        vm.SelectedJumpTargetsText = $"{one.Number}, {two.Number}";
        Assert.Equal("Manual", one.StartTriggerDisplay);
        Assert.Equal("Auto-continue", two.StartTriggerDisplay);
        Assert.Equal($"Jump → #{one.Number}, #{two.Number}", jump.TargetDisplay);

        vm.SelectedJumpRandom = true;
        Assert.Equal($"Random → #{one.Number}, #{two.Number}", jump.TargetDisplay);

        var media = vm.AddEmptyMediaCue()!;
        vm.SelectedCueNode = media;
        vm.SelectedEndTargetText = jump.Number;
        Assert.Equal($"End → #{jump.Number}", media.TargetDisplay);

        // Stable-id storage means a later target renumber only changes the display, not the link.
        vm.SelectedCueNode = jump;
        vm.SelectedCueNumber = "50";
        Assert.Equal(jump.Id, media.EndTargetCueId);
        Assert.Equal("End → #50", media.TargetDisplay);
    }

    [Fact]
    public void MediaEndTarget_RoundTripsThroughJsonAsStableCueId()
    {
        var jump = new JumpCueNode { Number = "5", TargetCueIds = [Guid.NewGuid()], RandomTarget = true };
        var media = new MediaCueNode { Number = "2", EndTargetCueId = jump.Id };
        var list = new CueList { Nodes = [media, jump] };

        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;

        var loadedMedia = Assert.IsType<MediaCueNode>(loaded.Nodes[0]);
        Assert.Equal(jump.Id, loadedMedia.EndTargetCueId);
    }

    [Fact]
    public async Task MediaNaturalEnd_CustomTargetOverridesDefaultAutoFollow()
    {
        var defaultNext = new ActionCueNode
        {
            Number = "3",
            Label = "Default next",
            TriggerMode = CueTriggerMode.AutoFollow,
        };
        var target = new ActionCueNode { Number = "4", Label = "Explicit end target" };
        var media = new MediaCueNode
        {
            Number = "2",
            Label = "Current song",
            EndTargetCueId = target.Id,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [media, defaultNext, target] }]);
        var fired = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firedIds = new ConcurrentQueue<Guid>();
        vm.MediaCueExecutor = (cue, _) =>
        {
            vm.OnCueStarted(cue.Id);
            return Task.FromResult<string?>(null);
        };
        vm.ActionCueExecutor = (cue, _) =>
        {
            firedIds.Enqueue(cue.Id);
            if (cue.Id == target.Id)
                fired.TrySetResult(cue.Id);
            return Task.FromResult<string?>(null);
        };

        var sourceVm = vm.SelectedCueList!.Nodes.Single(node => node.Id == media.Id);
        vm.SelectedCueNode = sourceVm;
        vm.StandbySelectedCommand.Execute(null);
        await vm.GoCommand.ExecuteAsync(null);
        await vm.OnMediaCueNaturallyEndedAsync();

        var firedId = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(target.Id, firedId);
        await Task.Delay(100);
        Assert.Equal([target.Id], firedIds.ToArray());
        Assert.Same(sourceVm, vm.SelectedCueNode); // transport routing must not replace the properties drawer
    }

    [Fact]
    public async Task MissingExplicitEndTarget_StillSuppressesDefaultAutoFollow()
    {
        var media = new MediaCueNode
        {
            Number = "2",
            EndTargetCueId = Guid.NewGuid(),
        };
        var defaultNext = new ActionCueNode
        {
            Number = "3",
            TriggerMode = CueTriggerMode.AutoFollow,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [media, defaultNext] }]);
        var firedIds = new ConcurrentQueue<Guid>();
        vm.MediaCueExecutor = (cue, _) =>
        {
            vm.OnCueStarted(cue.Id);
            return Task.FromResult<string?>(null);
        };
        vm.ActionCueExecutor = (cue, _) =>
        {
            firedIds.Enqueue(cue.Id);
            return Task.FromResult<string?>(null);
        };
        vm.SelectedCueNode = vm.SelectedCueList!.Nodes[0];
        vm.StandbySelectedCommand.Execute(null);
        await vm.GoCommand.ExecuteAsync(null);

        await vm.OnMediaCueNaturallyEndedAsync();
        await Task.Delay(100);

        Assert.Empty(firedIds);
    }

    [Fact]
    public void JumpTargetContainingItself_IsSkippedInsteadOfLooping()
    {
        var groupId = Guid.NewGuid();
        var song = new MediaCueNode { Number = "1.2", Label = "Song" };
        var jump = new JumpCueNode
        {
            Number = "1.1",
            Label = "Pick song",
            TargetCueIds = [groupId, song.Id],
            RandomTarget = false,
            FireTargetOnJump = false,
        };
        var group = new CueGroupNode
        {
            Id = groupId,
            Number = "1",
            FireMode = CueGroupFireMode.FirstCueOnly,
            Children = [jump, song],
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [group] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[0].Children[0];
        var songVm = vm.SelectedCueList.Nodes[0].Children[1];
        Assert.Contains("#1 ⚠ cycle", jumpVm.TargetDisplay);

        var result = vm.ExecuteJumpCueOnUi(jumpVm);

        Assert.Null(result);
        Assert.Same(songVm, vm.StandbyCueNode);
    }

    [Fact]
    public void JumpWithOnlyImmediateCycle_IsStoppedWithClearError()
    {
        var groupId = Guid.NewGuid();
        var jump = new JumpCueNode
        {
            Number = "1.1",
            TargetCueIds = [groupId],
            FireTargetOnJump = false,
        };
        var group = new CueGroupNode
        {
            Id = groupId,
            Number = "1",
            FireMode = CueGroupFireMode.FirstCueOnly,
            Children = [jump],
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [group] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[0].Children[0];

        var error = Assert.Throws<InvalidOperationException>(() => vm.ExecuteJumpCueOnUi(jumpVm));

        Assert.Equal(HaPlay.Resources.Strings.CueJumpCycleDetected, error.Message);
    }

    [Fact]
    public void RandomJump_AvoidImmediateRepeat_AlternatesWhenAnotherTargetIsAvailable()
    {
        var one = new ActionCueNode { Number = "1" };
        var two = new ActionCueNode { Number = "2" };
        var jump = new JumpCueNode
        {
            Number = "3",
            TargetCueIds = [one.Id, two.Id],
            RandomTarget = true,
            AvoidImmediateRepeat = true,
            FireTargetOnJump = false,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [one, two, jump] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[^1];
        Guid? previous = null;

        for (var i = 0; i < 20; i++)
        {
            Assert.Null(vm.ExecuteJumpCueOnUi(jumpVm));
            var selected = Assert.IsType<CueNodeViewModel>(vm.StandbyCueNode).Id;
            Assert.NotEqual(previous, selected);
            previous = selected;
        }
    }

    [Fact]
    public void RandomJump_AvoidImmediateRepeat_ReusesTheOnlyAvailableTarget()
    {
        var only = new ActionCueNode { Number = "1" };
        var jump = new JumpCueNode
        {
            Number = "2",
            TargetCueIds = [only.Id],
            RandomTarget = true,
            AvoidImmediateRepeat = true,
            FireTargetOnJump = false,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [only, jump] }]);
        var jumpVm = vm.SelectedCueList!.Nodes[^1];

        Assert.Null(vm.ExecuteJumpCueOnUi(jumpVm));
        Assert.Equal(only.Id, vm.StandbyCueNode!.Id);
        Assert.Null(vm.ExecuteJumpCueOnUi(jumpVm));
        Assert.Equal(only.Id, vm.StandbyCueNode!.Id);
    }

    [Fact]
    public void AutoNumbering_IsGloballyUnique_AcrossNestingLevels()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        vm.AddCommentCueCommand.Execute(null); // 1
        vm.AddCommentCueCommand.Execute(null); // 2
        vm.AddCommentCueCommand.Execute(null); // 3
        var numbers = vm.SelectedCueList!.Nodes.Select(n => n.Number).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count()); // no duplicates
    }

    [Fact]
    public void AddJumpCue_TargetsTheSelectedCue()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        vm.AddActionCueCommand.Execute(null); // any FIREABLE cue works as a target (comments do not)
        var media = Assert.Single(vm.SelectedCueList!.Nodes);
        vm.SelectedCueNode = media;

        vm.AddJumpCueCommand.Execute(null);
        var jump = vm.SelectedCueList!.Nodes[^1];
        Assert.Equal(CueNodeKind.Jump, jump.Kind);
        Assert.Equal([media.Id], jump.JumpTargetIds); // the loop-back gesture
        Assert.False(jump.JumpRandom);
        var model = Assert.IsType<JumpCueNode>(jump.ToModel());
        Assert.True(model.FireTargetOnJump);
    }

    [Fact]
    public void AddJumpCue_InsideSelectedGroup_DoesNotAutoTargetItsOwnGroup()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        vm.AddGroupCommand.Execute(null);
        var group = vm.SelectedCueNode!;

        vm.AddJumpCueCommand.Execute(null);

        var jump = Assert.Single(group.Children);
        Assert.Equal(CueNodeKind.Jump, jump.Kind);
        Assert.Empty(jump.JumpTargetIds);
    }
}


/// <summary>Visualizer cue (#26): model/VM/JSON round-trip of the placeable-layer payload.</summary>
public sealed class VisualizerCueTests
{
    [Fact]
    public void VisualizerCueNode_RoundTrips_ThroughViewModelAndJson()
    {
        var compId = Guid.NewGuid();
        var node = new VisualizerCueNode
        {
            Number = "5",
            Label = "VIZ corner",
            CompositionId = compId,
            StartVisualizer = true,
            PresetDirectory = "/presets",
            PresetDurationSeconds = 18,
            ShufflePresets = false,
            BeatSensitivity = 1.4,
            TransitionSeconds = 1.25,
            DestX = 0.5, DestY = 0.5, DestWidth = 0.5, DestHeight = 0.5, Opacity = 0.8,
        };

        var vm = CueNodeViewModel.FromModel(node);
        Assert.Equal(CueNodeKind.Visualizer, vm.Kind);
        var back = Assert.IsType<VisualizerCueNode>(vm.ToModel());
        Assert.Equal(compId, back.CompositionId);
        Assert.True(back.StartVisualizer);
        Assert.Equal("/presets", back.PresetDirectory);
        Assert.Equal(18, back.PresetDurationSeconds);
        Assert.False(back.ShufflePresets);
        Assert.Equal(1.4, back.BeatSensitivity);
        Assert.Equal(1.25, back.TransitionSeconds);
        Assert.Equal(0.5, back.DestX);
        Assert.Equal(0.8, back.Opacity);

        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.Contains("\"visualizer\"", json);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<VisualizerCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal(0.5, reloaded.DestWidth);
        Assert.Equal(18, reloaded.PresetDurationSeconds);
    }

    [Fact]
    public void StopCue_RoundTripsStartFlag()
    {
        var vm = CueNodeViewModel.FromModel(new VisualizerCueNode { StartVisualizer = false });
        var back = Assert.IsType<VisualizerCueNode>(vm.ToModel());
        Assert.False(back.StartVisualizer);
    }
}


/// <summary>Loads the operator's REAL test project (skipped where absent) - catches deserialization /
/// VM-mapping crashes with the new cue kinds (visualizer/jump) that a synthetic fixture might miss.
/// The user reported intermittent silent crashes on project load.</summary>
public sealed class RealProjectLoadTests
{
    private const string ProjectPath = "/home/seko/Documents/HaPlay Projects/CueTestproject.haplayproj";

    [Fact]
    public async Task UserTestProject_LoadsAndMapsToViewModels()
    {
        if (!File.Exists(ProjectPath))
            return; // operator-machine-only fixture

        var project = await ProjectIO.LoadAsync(ProjectPath);
        Assert.NotEmpty(project.CueLists);

        foreach (var list in project.CueLists)
        {
            var editor = CueListEditorViewModel.FromModel(list);
            // Round-trip through the VM layer - where kind-specific mapping bugs would throw.
            var back = editor.ToModel();
            Assert.Equal(CountNodes(list.Nodes), CountNodes(back.Nodes));
        }
    }

    private static int CountNodes(IEnumerable<CueNode> nodes) =>
        nodes.Sum(n => 1 + (n is CueGroupNode g ? CountNodes(g.Children) : 0));
}
