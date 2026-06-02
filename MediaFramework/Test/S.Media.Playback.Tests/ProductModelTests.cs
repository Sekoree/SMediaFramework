using S.Media.Core.Triggers;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class ProductModelTests
{
    [Fact]
    public void RoutingScene_SnapshotOrdersPatchLayersAndMetrics()
    {
        var scene = new RoutingScene();
        scene.SetRoute(new OutputPatchRoute("camera", "program"));
        scene.SetRoute(new OutputPatchRoute("clip", "preview", FormatVersion: "v1"));
        scene.UpsertLayer(new SceneLayerDefinition("fg", "camera", 20, Opacity: 0.75f));
        scene.UpsertLayer(new SceneLayerDefinition("bg", "clip", 10));
        scene.SetTransition(new RoutingTransition("fg", RoutingTransitionKind.Fade, TimeSpan.FromMilliseconds(250), 0, 1));
        scene.SetSyncGroup(new SyncGroupDefinition("main", "clock", ["camera", "clip"]));
        scene.SetNdiPreset(new NdiEndpointPreset("ndi-program", "Program", IsInput: false));
        scene.SetPreviewProgram("preview", "program");
        scene.SetMetrics(new OperatorEndpointMetrics("program", Submitted: 10, Dropped: 1, QueueDepth: 2, State: "ok"));

        var snapshot = scene.Snapshot();

        Assert.Equal(["camera", "clip"], snapshot.Routes.Select(r => r.SourceId).ToArray());
        Assert.Equal(["bg", "fg"], snapshot.Layers.Select(l => l.LayerId).ToArray());
        Assert.Equal("preview", snapshot.PreviewOutputId);
        Assert.Equal("program", snapshot.ProgramOutputId);
        Assert.Single(snapshot.Transitions);
        Assert.Single(snapshot.SyncGroups);
        Assert.Single(snapshot.NdiPresets);
        Assert.Single(snapshot.Metrics);

        var after = new RoutingScene();
        after.SetRoute(new OutputPatchRoute("clip", "preview", FormatVersion: "v2"));
        after.UpsertLayer(new SceneLayerDefinition("bg", "clip", 10));
        var plan = RoutingScene.PlanChanges(snapshot, after.Snapshot());
        Assert.Single(plan.RoutesToAddOrUpdate);
        Assert.Single(plan.RoutesToRemove);
        Assert.Empty(plan.LayersToAddOrUpdate);
        Assert.Single(plan.LayersToRemove);
    }

    [Fact]
    public void SoundboardGrid_TracksPadsPreloadFeedbackBindingsAndBudget()
    {
        var grid = new SoundboardGrid(memoryBudgetBytes: 100);
        grid.SetPad(new SoundboardPadDefinition("pad-1", "kick", "Kick", SoundboardPadMode.Retrigger));
        grid.SetPad(new SoundboardPadDefinition("pad-2", "loop", "Loop", SoundboardPadMode.LatchToggle, GroupId: "loops"));

        Assert.True(grid.TryPreload("kick", 40));
        Assert.False(grid.TryPreload("loop", 80));
        grid.SetFeedback(new SoundboardPadFeedback("pad-1", SoundboardLedState.Ready, "Kick"));
        grid.BindTrigger(new SoundboardGridBinding("pad-1", "midi", "note36"));
        grid.SetVoiceControl(new SoundboardVoiceControl("pad-1", FadeToGain: 0.5f, Pan: -0.25f));

        var snapshot = grid.Snapshot();

        Assert.Equal(["pad-1", "pad-2"], snapshot.Pads.Select(p => p.PadId).ToArray());
        Assert.Equal(["kick"], snapshot.PreloadedCueIds);
        Assert.Equal(40, snapshot.EstimatedBytes);
        Assert.Single(snapshot.Feedback);
        Assert.Single(snapshot.Bindings);
        Assert.Single(snapshot.VoiceControls);
        Assert.True(snapshot.AutomaticReapingEnabled);

        Assert.True(grid.TryCreateScheduledFire("pad-1", TimeSpan.FromSeconds(1), out var fire));
        Assert.Equal("kick", fire.CueId);
        Assert.Empty(grid.EvictUntilWithinBudget());
    }

    [Fact]
    public void TriggerBindingSet_SimulatesDebouncedActionDispatch()
    {
        var bindings = new TriggerBindingSet();
        var trigger = new TriggerDescriptor(TriggerSourceKind.Midi, "grid", "note36");
        bindings.AddOrReplace(new TriggerBinding(
            "kick",
            trigger,
            new TriggerActionDescriptor(TriggerActionKind.Soundboard, "pad-1", "fire"),
            Debounce: TimeSpan.FromMilliseconds(100))
        {
            TypedRetriggerPolicy = TriggerRetriggerPolicy.Debounce,
        });
        bindings.SetTimecodeSyncPlan(new TimecodeSyncPlan(TimecodeSyncKind.Mtc, "midi-clock", 30));

        var t0 = DateTimeOffset.UtcNow;
        var first = bindings.Simulate(trigger, TriggerPayload.FromNumeric(1), t0);
        var second = bindings.Simulate(trigger, TriggerPayload.FromNumeric(1), t0 + TimeSpan.FromMilliseconds(50));
        var third = bindings.Simulate(trigger, TriggerPayload.FromNumeric(1), t0 + TimeSpan.FromMilliseconds(150));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(third);
        Assert.Equal(2, bindings.Dispatches.Count);
        Assert.Equal("pad-1", bindings.Dispatches[0].Action.TargetId);
        Assert.Equal(TimecodeSyncKind.Mtc, bindings.TimecodeSyncPlan!.Kind);
        Assert.Equal(TriggerRetriggerPolicy.Debounce, bindings.Bindings[0].TypedRetriggerPolicy);
    }

    [Fact]
    public void TriggerBindingSet_CanRegisterWithTriggerBusForSimulation()
    {
        var bindings = new TriggerBindingSet();
        var trigger = new TriggerDescriptor(TriggerSourceKind.Keyboard, "main", "Space");
        bindings.AddOrReplace(new TriggerBinding(
            "play-pause",
            trigger,
            new TriggerActionDescriptor(TriggerActionKind.MediaPlayer, "controller", "playPause")));
        var bus = new TriggerBus();

        bindings.RegisterWith(bus);

        Assert.True(bus.Fire("play-pause", TriggerPayload.FromText("down")));
        Assert.Single(bindings.Dispatches);
        Assert.Equal(TriggerActionKind.MediaPlayer, bindings.Dispatches[0].Action.Kind);
    }

    [Fact]
    public void ProductApiSamples_CreateCueAndSoundboardSamples()
    {
        var cues = ProductApiSamples.CreateCuePlayerSample();
        var grid = ProductApiSamples.CreateSoundboardGridSample();

        Assert.Equal(2, cues.Cues.Count);
        Assert.Equal(["intro"], cues.PrewarmKeys());
        Assert.Equal(["A1", "A2"], grid.Snapshot().Pads.Select(p => p.PadId).ToArray());
    }
}
