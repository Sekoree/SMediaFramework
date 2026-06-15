using HaPlay.Models;
using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlaybackEngineTests
{
    [Fact]
    public void NaturalEnd_UsesAwaitableCallbackContract()
    {
        var evt = typeof(CuePlaybackEngine).GetEvent(nameof(CuePlaybackEngine.NaturalEnd));

        Assert.NotNull(evt);
        Assert.Equal(typeof(Func<Task>), evt.EventHandlerType);
    }

    [Fact]
    public void BuildRoutePlan_KeepsEveryPlacementOnTheSameComposition()
    {
        // Picture-in-picture / same source in two regions: a cue can place its video twice on one
        // composition. The route plan must keep BOTH placements (it used to collapse to the first).
        var compId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Source = new FilePlaylistItem("/clip.mp4"),
            VideoPlacements =
            [
                new CueVideoPlacement { CompositionId = compId, LayerIndex = 0, DestX = 0.0, DestWidth = 0.5, DestHeight = 1.0 },
                new CueVideoPlacement { CompositionId = compId, LayerIndex = 1, DestX = 0.5, DestWidth = 0.5, DestHeight = 1.0 },
            ],
        };

        var plan = CuePlaybackEngine.BuildRoutePlan(cue);

        Assert.Equal(2, plan.Placements.Count);
        Assert.All(plan.Placements, p => Assert.Equal(compId, p.CompositionId));
        Assert.Equal(new[] { 0, 1 }, plan.Placements.Select(p => p.LayerIndex).ToArray());
        Assert.Equal(new[] { 0, 1 }, plan.PlacementSourceIndices.ToArray());
    }

    [Fact]
    public void BuildRoutePlan_PreservesSourcePlacementIndicesAfterLayerSort()
    {
        var compId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Source = new FilePlaylistItem("/clip.mp4"),
            VideoPlacements =
            [
                new CueVideoPlacement { CompositionId = compId, LayerIndex = 5 },
                new CueVideoPlacement { CompositionId = compId, LayerIndex = 1 },
            ],
        };

        var plan = CuePlaybackEngine.BuildRoutePlan(cue);

        Assert.Equal(new[] { 1, 5 }, plan.Placements.Select(p => p.LayerIndex).ToArray());
        Assert.Equal(new[] { 1, 0 }, plan.PlacementSourceIndices.ToArray());
    }

    [Fact]
    public void BuildRoutePlan_PreservesAudioRouteSourceIndicesPerOutput()
    {
        var outA = Guid.NewGuid();
        var outB = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Source = new FilePlaylistItem("/clip.mp4"),
            AudioRoutes =
            [
                new CueAudioRoute { OutputLineId = outA, SourceChannel = 0, OutputChannel = 1 },
                new CueAudioRoute { OutputLineId = outB, SourceChannel = 1, OutputChannel = 1 },
                new CueAudioRoute { OutputLineId = outA, SourceChannel = 2, OutputChannel = 2 },
            ],
        };

        var plan = CuePlaybackEngine.BuildRoutePlan(cue);

        Assert.Equal(new[] { 0, 2 }, plan.AudioByOutput[outA].Select(r => r.SourceIndex).ToArray());
        Assert.Equal(new[] { 1 }, plan.AudioByOutput[outB].Select(r => r.SourceIndex).ToArray());
    }

    [Fact]
    public void ValidateWiredRouteCounts_RejectsVideoOnlyPlanWithNoLayerSlots()
    {
        var plan = new CuePlaybackEngine.RoutePlan(
            new Dictionary<Guid, List<CuePlaybackEngine.AudioRoutePlanEntry>>(),
            [new CueVideoPlacement { CompositionId = Guid.NewGuid(), LayerIndex = 0 }],
            [0]);

        var error = CuePlaybackEngine.ValidateWiredRouteCounts(plan, audioSourceCount: 0, layerSlotCount: 0);

        Assert.Equal("No cue video output could be acquired.", error);
    }

    [Fact]
    public void ValidateWiredRouteCounts_AllowsMixedPlanWhenOneSideWired()
    {
        var outputLineId = Guid.NewGuid();
        var plan = new CuePlaybackEngine.RoutePlan(
            new Dictionary<Guid, List<CuePlaybackEngine.AudioRoutePlanEntry>>
            {
                [outputLineId] =
                [
                    new CuePlaybackEngine.AudioRoutePlanEntry(
                        new CueAudioRoute { OutputLineId = outputLineId, SourceChannel = 0, OutputChannel = 1 },
                        SourceIndex: 0),
                ],
            },
            [new CueVideoPlacement { CompositionId = Guid.NewGuid(), LayerIndex = 0 }],
            [0]);

        var error = CuePlaybackEngine.ValidateWiredRouteCounts(plan, audioSourceCount: 1, layerSlotCount: 0);

        Assert.Null(error);
    }
}
