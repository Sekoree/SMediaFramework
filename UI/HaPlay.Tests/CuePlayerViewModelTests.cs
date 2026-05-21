using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlayerViewModelTests
{
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
    public void AddAndRemoveVirtualOutput_UpdatesCueMediaRoutes()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddRouteConnectionCommand.Execute(null);
        Assert.Single(media.RouteConnections);

        vm.SelectedVirtualOutput = vm.VisibleVirtualOutputs[0];
        vm.RemoveVirtualOutputCommand.Execute(null);

        Assert.Single(vm.VisibleVirtualOutputs);
        Assert.Empty(media.RouteConnections);
        Assert.DoesNotContain(1, media.VirtualOutputChannels);
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
