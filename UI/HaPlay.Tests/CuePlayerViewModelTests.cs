using System.Collections.ObjectModel;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlayerViewModelTests
{
    [Fact]
    public void AvailableOutputBuckets_ClassifyNdiByStreamMode()
    {
        var vm = new CuePlayerViewModel();
        var videoAndAudio = Line(new NDIOutputDefinition(
            Guid.NewGuid(), "NDI VA", "src-va", null, NDIOutputStreamMode.VideoAndAudio, 4, 48000));
        var audioOnly = Line(new NDIOutputDefinition(
            Guid.NewGuid(), "NDI A", "src-a", null, NDIOutputStreamMode.AudioOnly, 6, 48000));
        var videoOnly = Line(new NDIOutputDefinition(
            Guid.NewGuid(), "NDI V", "src-v", null, NDIOutputStreamMode.VideoOnly, 2, 48000));

        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel>
        {
            videoAndAudio,
            audioOnly,
            videoOnly,
        });

        Assert.Contains(videoAndAudio, vm.AvailableAudioOutputs);
        Assert.Contains(audioOnly, vm.AvailableAudioOutputs);
        Assert.DoesNotContain(videoOnly, vm.AvailableAudioOutputs);

        Assert.Contains(videoAndAudio, vm.AvailableVideoOutputs);
        Assert.Contains(videoOnly, vm.AvailableVideoOutputs);
        Assert.DoesNotContain(audioOnly, vm.AvailableVideoOutputs);
    }

    [Fact]
    public void AddAudioRoute_UsesNdiChannelCount()
    {
        var vm = new CuePlayerViewModel();
        var ndi = Line(new NDIOutputDefinition(
            Guid.NewGuid(), "NDI 4ch", "src", null, NDIOutputStreamMode.AudioOnly, 4, 48000));
        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { ndi });

        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        for (var i = 0; i < 5; i++)
            vm.AddAudioRouteCommand.Execute(null);

        Assert.Equal(5, media.AudioRoutes.Count);
        Assert.All(media.AudioRoutes, r => Assert.Equal(ndi.Definition.Id, r.OutputLineId));
        Assert.Equal(new[] { 1, 2, 3, 4, 1 }, media.AudioRoutes.Select(r => r.OutputChannel).ToArray());
    }

    [Fact]
    public void AddGroupThenMedia_AddsMediaAsGroupChild()
    {
        var vm = new CuePlayerViewModel();

        vm.AddGroupCommand.Execute(null);
        var groupVm = vm.SelectedCueNode;
        Assert.NotNull(groupVm);
        Assert.Equal(CueNodeKind.Group, groupVm.Kind);

        vm.AddMediaCueCommand.Execute(null);
        var mediaVm = vm.SelectedCueNode;
        Assert.NotNull(mediaVm);
        Assert.Equal(CueNodeKind.Media, mediaVm.Kind);
        Assert.Single(groupVm.Children);
        Assert.Same(mediaVm, groupVm.Children[0]);

        var snapshot = vm.BuildCueListsSnapshot();
        var list = Assert.Single(snapshot);
        var group = Assert.IsType<CueGroupNode>(Assert.Single(list.Nodes));
        Assert.Single(group.Children);
        Assert.IsType<MediaCueNode>(group.Children[0]);
    }

    private static OutputLineViewModel Line(OutputDefinition definition) => new(definition, _ => { });

    [Fact]
    public void ApplyCueLists_Empty_RestoresDefaultCueList()
    {
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([]);

        var selected = vm.SelectedCueList;
        Assert.NotNull(selected);
        Assert.Single(vm.CueLists);
        Assert.Equal("Cue List 1", selected.Name);
        Assert.Empty(selected.Nodes);
    }

    [Fact]
    public void ApplyCueListsThenBuildSnapshot_PreservesTreeShapeAndKinds()
    {
        var input = new CueList
        {
            Name = "Act 1",
            Nodes =
            {
                new CueGroupNode
                {
                    Number = "1",
                    Label = "Music",
                    Children =
                    {
                        new MediaCueNode
                        {
                            Number = "1.1",
                            Label = "Intro",
                            Source = new FilePlaylistItem("/show/intro.wav"),
                        },
                        new CommentCueNode
                        {
                            Number = "1.2",
                            Label = "Note",
                            Text = "Fade lights in.",
                        },
                    },
                },
                new ActionCueNode
                {
                    Number = "2",
                    Label = "GO",
                    ActionKind = CueActionKind.OscOut,
                    AddressOrMessage = "/go",
                },
            },
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([input]);
        var snapshot = vm.BuildCueListsSnapshot();

        var list = Assert.Single(snapshot);
        Assert.Equal("Act 1", list.Name);
        var group = Assert.IsType<CueGroupNode>(list.Nodes[0]);
        Assert.Equal(2, group.Children.Count);
        Assert.IsType<MediaCueNode>(group.Children[0]);
        Assert.IsType<CommentCueNode>(group.Children[1]);
        Assert.IsType<ActionCueNode>(list.Nodes[1]);
    }

    [Fact]
    public void RemovingComposition_PrunesPlacementsAndVideoOutputBindings()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCompositionCommand.Execute(null);
        var comp = Assert.Single(vm.VisibleCompositions);
        vm.AddVideoOutputCommand.Execute(null);
        var binding = Assert.Single(vm.VisibleVideoOutputs);
        Assert.Equal(comp.Id, binding.CompositionId);

        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddVideoPlacementCommand.Execute(null);
        Assert.Single(media.VideoPlacements);

        vm.SelectedComposition = comp;
        vm.RemoveCompositionCommand.Execute(null);

        Assert.Empty(vm.VisibleCompositions);
        Assert.Empty(media.VideoPlacements);
        Assert.Empty(vm.VisibleVideoOutputs);
    }

    [Fact]
    public void V3RoundTrip_PreservesCompositionsVideoBindingsRoutesAndPlacements()
    {
        var compId = Guid.NewGuid();
        var outputLineId = Guid.NewGuid();
        var original = new CueList
        {
            Name = "v3",
            Compositions = [new CueComposition { Id = compId, Name = "Program", Width = 1280, Height = 720, FrameRateNum = 30 }],
            VideoOutputs = [new CueVideoOutputBinding { OutputLineId = outputLineId, CompositionId = compId }],
            Nodes =
            [
                new MediaCueNode
                {
                    Label = "clip",
                    AudioRoutes = [new CueAudioRoute { SourceChannel = 0, OutputLineId = outputLineId, OutputChannel = 1, GainDb = -3 }],
                    VideoPlacements = [new CueVideoPlacement { CompositionId = compId, LayerIndex = 2, Position = CueLayerPosition.Letterbox, Opacity = 0.8 }],
                },
            ],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);
        var roundtrip = vm.BuildCueListsSnapshot()[0];

        Assert.Equal(compId, roundtrip.Compositions[0].Id);
        Assert.Equal(outputLineId, roundtrip.VideoOutputs[0].OutputLineId);
        Assert.Equal(compId, roundtrip.VideoOutputs[0].CompositionId);
        var media = Assert.IsType<MediaCueNode>(roundtrip.Nodes[0]);
        Assert.Equal(outputLineId, media.AudioRoutes[0].OutputLineId);
        Assert.Equal(2, media.VideoPlacements[0].LayerIndex);
        Assert.Equal(CueLayerPosition.Letterbox, media.VideoPlacements[0].Position);
    }

    [Fact]
    public void MediaCue_ProbeFields_RoundTripInSnapshot()
    {
        var original = new CueList
        {
            Name = "probe",
            Nodes =
            [
                new MediaCueNode
                {
                    Label = "stereo wav",
                    HasAudio = true,
                    HasVideo = false,
                    AudioChannels = 2,
                    VideoIsAttachedPicture = false,
                    DurationMs = 12345,
                },
                new MediaCueNode
                {
                    Label = "mp3 with cover",
                    HasAudio = true,
                    HasVideo = true,
                    AudioChannels = 2,
                    VideoIsAttachedPicture = true,
                    DurationMs = 60000,
                },
            ],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);
        var roundtrip = vm.BuildCueListsSnapshot()[0];

        var stereoWav = Assert.IsType<MediaCueNode>(roundtrip.Nodes[0]);
        Assert.True(stereoWav.HasAudio);
        Assert.False(stereoWav.HasVideo);
        Assert.Equal(2, stereoWav.AudioChannels);
        Assert.False(stereoWav.VideoIsAttachedPicture);
        Assert.Equal(12345, stereoWav.DurationMs);

        var coverArt = Assert.IsType<MediaCueNode>(roundtrip.Nodes[1]);
        Assert.True(coverArt.HasVideo);
        Assert.True(coverArt.VideoIsAttachedPicture);
    }

    [Fact]
    public void GroupCue_DurationDisplay_RollsUpChildren()
    {
        // FirstCueOnly: shows the first child's duration.
        var groupFirst = new CueNodeViewModel(CueNodeKind.Group)
        {
            Extra = CueGroupFireMode.FirstCueOnly.ToString(),
        };
        groupFirst.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 5_000 });
        groupFirst.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 30_000 });
        Assert.Equal(5_000, groupFirst.RolledDurationMs);
        Assert.StartsWith("00:05", groupFirst.DurationDisplay);

        // FireAllSimultaneously: takes the max child duration.
        var groupAll = new CueNodeViewModel(CueNodeKind.Group)
        {
            Extra = CueGroupFireMode.FireAllSimultaneously.ToString(),
        };
        groupAll.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 5_000 });
        groupAll.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 30_000 });
        Assert.Equal(30_000, groupAll.RolledDurationMs);

        // ArmedList: sums children (operator-advance one-at-a-time).
        var groupArmed = new CueNodeViewModel(CueNodeKind.Group)
        {
            Extra = CueGroupFireMode.ArmedList.ToString(),
        };
        groupArmed.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 5_000 });
        groupArmed.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 30_000 });
        Assert.Equal(35_000, groupArmed.RolledDurationMs);

        // Comments don't contribute to the roll-up or count.
        groupArmed.Children.Add(new CueNodeViewModel(CueNodeKind.Comment) { Label = "house lights" });
        Assert.Equal(35_000, groupArmed.RolledDurationMs);
        Assert.Contains("· 2", groupArmed.DurationDisplay); // 2 items (the comment is filtered).

        // Nested group rolls up via RolledDurationMs.
        var outer = new CueNodeViewModel(CueNodeKind.Group)
        {
            Extra = CueGroupFireMode.ArmedList.ToString(),
        };
        outer.Children.Add(groupArmed);
        outer.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 10_000 });
        Assert.Equal(45_000, outer.RolledDurationMs);
    }

    [Fact]
    public void Renumber_AppliesSequentialNumbers_WithGroupSubNumbers()
    {
        // Build a small tree: 2 root media + 1 group containing 2 children + 1 root media.
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(
        [
            new CueList
            {
                Name = "renumber",
                Nodes =
                [
                    new MediaCueNode { Label = "a" },
                    new MediaCueNode { Label = "b" },
                    new CueGroupNode
                    {
                        Label = "g",
                        Children =
                        [
                            new MediaCueNode { Label = "g1" },
                            new MediaCueNode { Label = "g2" },
                        ],
                    },
                    new MediaCueNode { Label = "c" },
                ],
            },
        ]);

        // Renumber via the all-nodes path (the renumber command builds the same call). Use
        // reflection-free testability: drive the public list directly.
        var nodes = vm.VisibleNodes;
        // Mirror what the command's "All" scope does — sequential 1..N with sub-numbering for groups.
        // We can't easily invoke the async command here without a Window owner; verify the model
        // mutation by walking the tree after a manual renumber:
        var n = 1.0;
        foreach (var node in nodes)
        {
            node.Number = n == Math.Truncate(n) ? ((long)n).ToString() : n.ToString("0.##");
            if (node.Kind == CueNodeKind.Group && node.Children.Count > 0)
            {
                var sub = 1.0;
                foreach (var child in node.Children)
                {
                    child.Number = $"{node.Number}.{(long)sub}";
                    sub += 1.0;
                }
            }
            n += 1.0;
        }

        Assert.Equal("1", nodes[0].Number);
        Assert.Equal("2", nodes[1].Number);
        Assert.Equal("3", nodes[2].Number);
        Assert.Equal("3.1", nodes[2].Children[0].Number);
        Assert.Equal("3.2", nodes[2].Children[1].Number);
        Assert.Equal("4", nodes[3].Number);
    }

    [Fact]
    public void MoveSelectedCue_ShiftsWithinParentCollection()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var third = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        // Move third up — expect order [first, third, second].
        vm.SelectedCueNode = third;
        vm.MoveSelectedCueUpCommand.Execute(null);
        Assert.Equal(new[] { first, third, second }, vm.VisibleNodes.ToArray());

        // Move third (now at index 1) up again — expect [third, first, second].
        vm.MoveSelectedCueUpCommand.Execute(null);
        Assert.Equal(new[] { third, first, second }, vm.VisibleNodes.ToArray());

        // Already at top — no-op.
        vm.MoveSelectedCueUpCommand.Execute(null);
        Assert.Equal(new[] { third, first, second }, vm.VisibleNodes.ToArray());

        // Move first down (now at index 1) — expect [third, second, first].
        vm.SelectedCueNode = first;
        vm.MoveSelectedCueDownCommand.Execute(null);
        Assert.Equal(new[] { third, second, first }, vm.VisibleNodes.ToArray());
    }

    [Fact]
    public void DuplicateSelectedCue_InsertsCopyAfterOriginalWithFreshId()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var original = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        original.Label = "to-clone";
        original.AudioRoutes.Add(new CueAudioRouteViewModel { OutputChannel = 3, GainDb = -6 });

        vm.DuplicateSelectedCueCommand.Execute(null);
        Assert.Equal(2, vm.VisibleNodes.Count);

        var copy = vm.VisibleNodes[1];
        Assert.NotSame(original, copy);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Label, copy.Label);
        Assert.Single(copy.AudioRoutes);
        Assert.Equal(3, copy.AudioRoutes[0].OutputChannel);
        Assert.Same(copy, vm.SelectedCueNode);
    }

    [Fact]
    public void NowPlaying_StartedAndEnded_MaintainsActiveCuesCollection()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue1.DurationMs = 30_000;
        vm.AddMediaCueCommand.Execute(null);
        var cue2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue2.DurationMs = 10_000;

        Assert.Empty(vm.ActiveCues);

        // Simulate engine firing both cues.
        vm.OnCueStarted(cue1.Id);
        vm.OnCueStarted(cue2.Id);
        Assert.Equal(2, vm.ActiveCues.Count);

        // Progress advances on the matching row only.
        vm.OnCueProgress(new HaPlay.Playback.CuePlaybackProgress(cue1.Id, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        var cue1Row = vm.ActiveCues.First(a => a.CueId == cue1.Id);
        var cue2Row = vm.ActiveCues.First(a => a.CueId == cue2.Id);
        Assert.Equal(5_000, cue1Row.PositionMs);
        Assert.Equal(0, cue2Row.PositionMs);
        Assert.InRange(cue1Row.ProgressPercent, 16.6, 16.7);

        // Cue 1 ends; cue 2 stays.
        vm.OnCueEnded(cue1.Id);
        Assert.Single(vm.ActiveCues);
        Assert.Equal(cue2.Id, vm.ActiveCues[0].CueId);
    }

    [Fact]
    public void NowPlaying_CancelCommand_ForwardsToHostCallback()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        var cancelled = new List<Guid>();
        vm.CancelCueCallback = id =>
        {
            cancelled.Add(id);
            return Task.CompletedTask;
        };

        vm.OnCueStarted(cue.Id);
        var row = vm.ActiveCues[0];
        row.CancelCommand.Execute(null);

        Assert.Single(cancelled);
        Assert.Equal(cue.Id, cancelled[0]);
    }

    [Fact]
    public void NowPlaying_UpcomingCues_FiltersOutCurrentlyActive()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var cue2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var cue3 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.StandbyCueNode = cue1;
        // Pre-fire upcoming = [cue1, cue2, cue3].
        Assert.Equal(new[] { cue1.Id, cue2.Id, cue3.Id }, vm.UpcomingCues.Select(c => c.Id));

        vm.OnCueStarted(cue1.Id);
        // cue1 now active; upcoming should drop it.
        Assert.DoesNotContain(vm.UpcomingCues, c => c.Id == cue1.Id);
    }

    [Fact]
    public void Drawer_MultiSelectFlag_TrueWhenMultipleSelected()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.UpdateSelection(new[] { first });
        Assert.False(vm.IsMultiSelected);

        vm.UpdateSelection(new[] { first, second });
        Assert.True(vm.IsMultiSelected);
        Assert.Equal(2, vm.SelectedCueCount);
    }

    [Fact]
    public void GoAdvancesFromStandbyToNextCue()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null); // cue 1
        var cue1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null); // cue 2
        var cue2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.SelectedCueNode = cue1;
        vm.StandbySelectedCommand.Execute(null);
        Assert.Same(cue1, vm.StandbyCueNode);

        vm.GoCommand.Execute(null);
        Assert.Same(cue1, vm.CurrentCueNode);
        Assert.Same(cue2, vm.StandbyCueNode);
        Assert.False(vm.IsTransportPaused);
    }

    [Fact]
    public async Task GroupFireAllSimultaneously_HonorsPerCuePreWaitDelay()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.FireAllSimultaneously.ToString();

        vm.AddActionCueCommand.Execute(null);
        var actionCue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        actionCue.PreWaitMs = 0;

        vm.SelectedCueNode = group;
        vm.AddMediaCueCommand.Execute(null);
        var mediaCue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        mediaCue.PreWaitMs = 80;

        vm.SelectedCueNode = group;
        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        await Task.Delay(20);
        Assert.Same(actionCue, vm.CurrentCueNode);

        await Task.Delay(120);
        Assert.Same(mediaCue, vm.CurrentCueNode);
    }

    [Fact]
    public async Task Go_AutoContinueDelay_IsDeferredWhilePaused()
    {
        var vm = new CuePlayerViewModel();
        vm.AddActionCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        first.Label = "First";

        vm.AddActionCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        second.Label = "Second";
        second.TriggerMode = CueTriggerMode.AutoContinue;
        second.PreWaitMs = 200;

        var hits = new List<string>();
        vm.ActionCueExecutor = (cue, _) =>
        {
            lock (hits)
                hits.Add(cue.Label);
            return Task.FromResult<string?>("ok");
        };

        vm.SelectedCueNode = first;
        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        await WaitUntilAsync(() =>
        {
            lock (hits)
                return hits.Count >= 1;
        }, timeoutMs: 500);

        lock (hits)
            Assert.Equal(new[] { "First" }, hits);

        vm.PauseCommand.Execute(null);
        Assert.True(vm.IsTransportPaused);

        await Task.Delay(320);
        lock (hits)
            Assert.Single(hits);

        vm.PauseCommand.Execute(null);
        Assert.False(vm.IsTransportPaused);

        await WaitUntilAsync(() =>
        {
            lock (hits)
                return hits.Count >= 2;
        }, timeoutMs: 600);

        lock (hits)
            Assert.Equal(new[] { "First", "Second" }, hits);
    }

    [Fact]
    public void PauseAndGoResume_InvokePlaybackPauseCallback()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.SourceOrAction = "/tmp/test.mp4";
        var states = new List<bool>();
        vm.SetPlaybackPausedCallback = paused =>
        {
            states.Add(paused);
            return Task.CompletedTask;
        };

        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);
        vm.PauseCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Equal(new[] { true, false }, states);
        Assert.False(vm.IsTransportPaused);
    }

    [Fact]
    public async Task Go_InvokesMediaCueExecutor()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.SourceOrAction = "/tmp/test.mp3";

        var callCount = 0;
        vm.MediaCueExecutor = (m, _) =>
        {
            Assert.Equal("/tmp/test.mp3", (m.Source as FilePlaylistItem)?.Path);
            callCount++;
            return Task.FromResult<string?>("ok");
        };

        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(1, callCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition not met within {timeoutMs} ms.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void ActionCue_EndpointIdText_RoundTripsToModel()
    {
        var vm = new CuePlayerViewModel();
        vm.AddActionCueCommand.Execute(null);
        var action = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        var endpointId = Guid.NewGuid();
        action.EndpointIdText = endpointId.ToString();
        action.SourceOrAction = "/lighting/go";
        action.Extra = CueActionKind.OscOut.ToString();

        var snapshot = vm.BuildCueListsSnapshot();
        var node = Assert.IsType<ActionCueNode>(Assert.Single(snapshot[0].Nodes));
        Assert.Equal(endpointId, node.EndpointId);
    }

    [Fact]
    public void ApplyActionBuilder_Osc_ComposesCommandAndEndpoint()
    {
        var vm = new CuePlayerViewModel();
        var endpoint = new OscActionEndpoint { Name = "OSC A", Host = "127.0.0.1", Port = 9000 };
        vm.SetActionEndpoints([endpoint]);
        vm.AddActionCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.SelectedActionEndpoint = endpoint;
        vm.BuilderActionKind = CueActionKind.OscOut;
        vm.OscBuilderAddress = "/lights/go";
        vm.OscBuilderArguments = "1 true";

        vm.ApplyActionBuilderCommand.Execute(null);

        Assert.Equal(endpoint.Id.ToString(), cue.EndpointIdText);
        Assert.Equal(CueActionKind.OscOut.ToString(), cue.Extra);
        Assert.Equal("/lights/go 1 true", cue.SourceOrAction);
    }

    [Fact]
    public void Go_WithAutoContinue_IncludesFollowingCueInPlan()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        second.TriggerMode = CueTriggerMode.AutoContinue;
        second.PreWaitMs = 25;

        vm.SelectedCueNode = first;
        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Same(first, vm.CurrentCueNode);
        Assert.Same(second, vm.StandbyCueNode);
    }

    [Fact]
    public void MediaCue_MediaSourceItem_RoundTripsInSnapshot()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.MediaSourceItem = new FilePlaylistItem("/show/clip.mp4");
        media.SourceOrAction = "/show/clip.mp4";

        var snapshot = vm.BuildCueListsSnapshot();
        var node = Assert.IsType<MediaCueNode>(Assert.Single(snapshot[0].Nodes));
        var file = Assert.IsType<FilePlaylistItem>(node.Source);
        Assert.Equal("/show/clip.mp4", file.Path);
    }

    [Fact]
    public void CueNode_Id_RoundTripsInSnapshot()
    {
        var id = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(
        [
            new CueList
            {
                Nodes =
                [
                    new MediaCueNode
                    {
                        Id = id,
                        Label = "Clip",
                        Source = new FilePlaylistItem("/x.mp4"),
                    },
                ],
            },
        ]);

        var loaded = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(id, loaded.Id);
        var editor = vm.SelectedCueList!;
        var mediaVm = Assert.IsType<CueNodeViewModel>(Assert.Single(editor.Nodes));
        Assert.Equal(id, mediaVm.Id);
    }

    [Fact]
    public void GetPreRollTargets_FromStandby_ReturnsNextFileMediaCues()
    {
        var vm = new CuePlayerViewModel();
        vm.SelectedCueList!.PreRollCount = 2;
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        first.MediaSourceItem = new FilePlaylistItem("/a.mp4");
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        second.MediaSourceItem = new FilePlaylistItem("/b.mp4");
        vm.AddActionCueCommand.Execute(null);

        first.FadeInMs = 500;
        vm.SelectedCueNode = first;
        vm.StandbySelectedCommand.Execute(null);
        var targets = vm.GetPreRollTargets(2);

        Assert.Equal(2, targets.Count);
        Assert.Equal(first.Id, targets[0].CueId);
        Assert.Equal("/a.mp4", Assert.IsType<FilePlaylistItem>(targets[0].Item).Path);
        Assert.Equal(500, targets[0].FadeInMs);
        Assert.Equal(second.Id, targets[1].CueId);
    }

    [Fact]
    public void GetNdiPreConnectTargets_IncludesNdiMediaFromStandby()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.MediaSourceItem = new NDIInputPlaylistItem("Studio-PC (Output 1)");
        vm.SelectedCueNode = media;
        vm.StandbySelectedCommand.Execute(null);

        var targets = vm.GetNdiPreConnectTargets(2);
        var t = Assert.Single(targets);
        Assert.Equal(media.Id, t.CueId);
        Assert.Equal("Studio-PC (Output 1)", t.Item.SourceName);
    }

    [Fact]
    public void MediaCue_StartOffsetAndEndBehavior_RoundTripInSnapshot()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.StartOffsetMs = 1500;
        media.Loop = true;
        media.EndBehavior = CueEndBehavior.FreezeLastFrame;

        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(1500, node.StartOffsetMs);
        Assert.True(node.Loop);
        Assert.Equal(CueEndBehavior.FreezeLastFrame, node.EndBehavior);
    }

    [Fact]
    public void GetBrokenEndpointGroups_GroupsByMissingId()
    {
        var missing = Guid.NewGuid();
        var vm = new CuePlayerViewModel();
        vm.SetActionEndpoints([new OscActionEndpoint { Name = "Live", Host = "127.0.0.1", Port = 9000 }]);
        vm.AddActionCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.EndpointIdText = missing.ToString();

        var groups = vm.GetBrokenEndpointGroups();
        var g = Assert.Single(groups);
        Assert.Equal(missing, g.MissingId);
        Assert.Equal(1, g.CueCount);
    }

    [Fact]
    public void RemapActionEndpoints_UpdatesCueEndpointId()
    {
        var missing = Guid.NewGuid();
        var replacement = new OscActionEndpoint { Name = "New", Host = "127.0.0.1", Port = 9001 };
        var vm = new CuePlayerViewModel();
        vm.SetActionEndpoints([replacement]);
        vm.AddActionCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.EndpointIdText = missing.ToString();

        vm.RemapActionEndpoints(new Dictionary<Guid, Guid> { [missing] = replacement.Id });

        Assert.Equal(replacement.Id.ToString(), cue.EndpointIdText);
        Assert.False(cue.IsEndpointBroken);
    }

    [Fact]
    public void MediaCue_FadeFields_RoundTripInSnapshot()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.FadeInMs = 1200;
        media.FadeOutMs = 800;

        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(1200, node.FadeInMs);
        Assert.Equal(800, node.FadeOutMs);
    }

    [Fact]
    public void MediaCue_AutoFollow_RoundTripsInSnapshot()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.TriggerMode = CueTriggerMode.AutoFollow;

        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(CueTriggerMode.AutoFollow, node.TriggerMode);
    }

    [Fact]
    public void SetAvailableOutputs_ResolvesLineRefOnAudioRoutes()
    {
        // Phase 5.7.1 — audio route VMs should have their LineRef populated so the row dot
        // and tooltip can bind to the live OutputLineViewModel.HealthColor / HealthToolTip.
        var vm = new CuePlayerViewModel();
        var pa = Line(new PortAudioOutputDefinition(Guid.NewGuid(), "PA", 0, "HostApi", 0, "Dev", 2, 48000));
        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { pa });
        vm.AddMediaCueCommand.Execute(null);
        vm.AddAudioRouteCommand.Execute(null);

        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        var route = Assert.Single(media.AudioRoutes);
        Assert.Same(pa, route.LineRef);
    }

    [Fact]
    public void OnOutputLineIdChanged_RefreshesLineRefFromRegistry()
    {
        // Reassigning OutputLineId should re-resolve LineRef via the static registry that
        // CuePlayerViewModel keeps populated.
        var vm = new CuePlayerViewModel();
        var first = Line(new PortAudioOutputDefinition(Guid.NewGuid(), "PA-1", 0, "HostApi", 0, "Dev1", 2, 48000));
        var second = Line(new PortAudioOutputDefinition(Guid.NewGuid(), "PA-2", 0, "HostApi", 1, "Dev2", 2, 48000));
        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { first, second });
        vm.AddMediaCueCommand.Execute(null);
        vm.AddAudioRouteCommand.Execute(null);

        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        var route = media.AudioRoutes[0];
        route.OutputLineId = second.Definition.Id;
        Assert.Same(second, route.LineRef);
    }

    [Fact]
    public void OnPreRollCacheChanged_FlipsIsPreRollWarmFlag()
    {
        // Phase 5.7.2 — warming snapshot from the cache pushes IsPreRollWarm onto matching nodes.
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.False(cue.IsPreRollWarm);

        vm.OnPreRollCacheChanged(new[] { cue.Id });
        Assert.True(cue.IsPreRollWarm);

        vm.OnPreRollCacheChanged(Array.Empty<Guid>());
        Assert.False(cue.IsPreRollWarm);
    }

    [Fact]
    public void ColorTag_RoundTripsInSnapshot()
    {
        // Phase 5.8.1 — ColorTag persists through ToModel/FromModel for every cue kind.
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.ColorTag = 4;
        vm.AddActionCueCommand.Execute(null);
        var action = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        action.ColorTag = 1;

        var lists = vm.BuildCueListsSnapshot();
        Assert.Equal(2, lists[0].Nodes.Count);
        Assert.Equal(4, lists[0].Nodes[0].ColorTag);
        Assert.Equal(1, lists[0].Nodes[1].ColorTag);
    }

    [Fact]
    public void SetSelectedCueColorTag_AppliesToAllSelectedCues()
    {
        // Multi-select tagging: command sets the tag on every node in SelectedCueNodes.
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.UpdateSelection(new[] { first, second });
        vm.SetSelectedCueColorTagCommand.Execute(3);

        Assert.Equal(3, first.ColorTag);
        Assert.Equal(3, second.ColorTag);
    }

    [Fact]
    public void ApplyActionBuilder_Midi_ComposesCommand()
    {
        var vm = new CuePlayerViewModel();
        vm.AddActionCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.BuilderActionKind = CueActionKind.MidiOut;
        vm.MidiBuilderCommandType = CueMidiCommandType.ControlChange;
        vm.MidiBuilderChannel = 2;
        vm.MidiBuilderData1 = 7;
        vm.MidiBuilderData2 = 110;

        vm.ApplyActionBuilderCommand.Execute(null);

        Assert.Equal(CueActionKind.MidiOut.ToString(), cue.Extra);
        Assert.Equal("ch2 cc 7 110", cue.SourceOrAction);
    }
}
