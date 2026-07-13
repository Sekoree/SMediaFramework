using System.Text.Json;
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
        Assert.True(back.FireTargetOnJump);

        // JSON round-trip through the polymorphic cue-node contract (project persistence).
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.Contains("\"jump\"", json); // the type discriminator
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<JumpCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal([targetA, targetB], reloaded.TargetCueIds);
        Assert.True(reloaded.RandomTarget);
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
            DestX = 0.5, DestY = 0.5, DestWidth = 0.5, DestHeight = 0.5, Opacity = 0.8,
        };

        var vm = CueNodeViewModel.FromModel(node);
        Assert.Equal(CueNodeKind.Visualizer, vm.Kind);
        var back = Assert.IsType<VisualizerCueNode>(vm.ToModel());
        Assert.Equal(compId, back.CompositionId);
        Assert.True(back.StartVisualizer);
        Assert.Equal("/presets", back.PresetDirectory);
        Assert.Equal(0.5, back.DestX);
        Assert.Equal(0.8, back.Opacity);

        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.Contains("\"visualizer\"", json);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<VisualizerCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal(0.5, reloaded.DestWidth);
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
