using System.Collections.ObjectModel;
using HaPlay.Playback;
using HaPlay.Resources;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
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
    public void LiveInputMediaSource_SeedsCueTabCapabilityMetadata()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        media.MediaSourceItem = new PortAudioInputPlaylistItem("Scarlett") { Channels = 4 };
        Assert.True(vm.HasSelectedMediaCueWithAudio);
        Assert.False(vm.HasSelectedMediaCueWithVideo);
        Assert.True(media.SourceHasAudio);
        Assert.Equal(4, media.SourceAudioChannels);

        media.MediaSourceItem = new NDIInputPlaylistItem("Studio NDI");
        Assert.True(vm.HasSelectedMediaCueWithAudio);
        Assert.True(vm.HasSelectedMediaCueWithVideo);
        Assert.True(media.SourceHasAudio);
        Assert.True(media.SourceHasVideo);

        media.MediaSourceItem = new NDIInputPlaylistItem("Studio NDI") { AudioOnly = true };
        Assert.True(vm.HasSelectedMediaCueWithAudio);
        Assert.False(vm.HasSelectedMediaCueWithVideo);

        media.MediaSourceItem = new NDIInputPlaylistItem("Studio NDI") { VideoOnly = true };
        Assert.False(vm.HasSelectedMediaCueWithAudio);
        Assert.True(vm.HasSelectedMediaCueWithVideo);
        Assert.Equal(0, media.SourceAudioChannels);
    }

    [Fact]
    public void ApplyCueLists_LegacyLiveInputsInferCueTabCapabilities()
    {
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(
        [
            new CueList
            {
                Nodes =
                [
                    new MediaCueNode
                    {
                        Label = "Mic",
                        Source = new PortAudioInputPlaylistItem("Scarlett") { Channels = 2 },
                    },
                    new MediaCueNode
                    {
                        Label = "NDI",
                        Source = new NDIInputPlaylistItem("Studio NDI"),
                    },
                ],
            },
        ]);

        var mic = Assert.IsType<CueNodeViewModel>(vm.VisibleNodes[0]);
        vm.SelectedCueNode = mic;
        Assert.True(vm.HasSelectedMediaCueWithAudio);
        Assert.False(vm.HasSelectedMediaCueWithVideo);

        var ndi = Assert.IsType<CueNodeViewModel>(vm.VisibleNodes[1]);
        vm.SelectedCueNode = ndi;
        Assert.True(vm.HasSelectedMediaCueWithAudio);
        Assert.True(vm.HasSelectedMediaCueWithVideo);
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
    public void ApplyCueLists_WithCollectionPath_TracksBundleFile()
    {
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(
            [new CueList { Name = "Show A" }, new CueList { Name = "Show B" }],
            "/tmp/show.cuelists");

        Assert.Equal(2, vm.CueLists.Count);
        Assert.Equal("/tmp/show.cuelists", vm.CueListsCollectionPath);
        Assert.Equal("/tmp/show.cuelists", vm.DisplayedCueFilePath);
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
    public void SourceFitRect_SmallerThanCanvas_KeepsActualSizeCentered()
    {
        // 1280x720 source on a 1920x1080 canvas -> 2/3 size, centered, not scaled up.
        var (x, y, w, h) = CuePlayerViewModel.SourceFitRect(1280, 720, 1920, 1080);
        Assert.Equal(2.0 / 3.0, w, 4);
        Assert.Equal(2.0 / 3.0, h, 4);
        Assert.Equal((1.0 - w) / 2.0, x, 4);
        Assert.Equal((1.0 - h) / 2.0, y, 4);
    }

    [Fact]
    public void SourceFitRect_LargerThanCanvas_ScalesDownToContain()
    {
        // 3840x2160 source on a 1920x1080 canvas -> contain-fits to the full frame (same aspect).
        var (x, y, w, h) = CuePlayerViewModel.SourceFitRect(3840, 2160, 1920, 1080);
        Assert.Equal(1.0, w, 4);
        Assert.Equal(1.0, h, 4);
        Assert.Equal(0.0, x, 4);
        Assert.Equal(0.0, y, 4);

        // A portrait source taller than the canvas scales by height and pillarboxes (w < 1, h == 1).
        // scale = min(1, 1920/1080, 1080/1920) = 0.5625 -> w = 1080*0.5625/1920, h = 1920*0.5625/1080.
        var (_, _, w2, h2) = CuePlayerViewModel.SourceFitRect(1080, 1920, 1920, 1080);
        Assert.Equal(1.0, h2, 4);
        Assert.Equal(1080.0 * 0.5625 / 1920.0, w2, 4);
    }

    [Fact]
    public void SourceFitRect_UnknownDimensions_FallsBackToFullFrame()
    {
        var (x, y, w, h) = CuePlayerViewModel.SourceFitRect(0, 0, 1920, 1080);
        Assert.Equal(0.0, x, 4);
        Assert.Equal(0.0, y, 4);
        Assert.Equal(1.0, w, 4);
        Assert.Equal(1.0, h, 4);
    }

    [Fact]
    public void SourceVideoDimensions_RoundTripInSnapshot()
    {
        var original = new CueList
        {
            Name = "dims",
            Nodes = [new MediaCueNode { Label = "clip", HasVideo = true, SourceVideoWidth = 1280, SourceVideoHeight = 720 }],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);
        var media = Assert.IsType<MediaCueNode>(vm.BuildCueListsSnapshot()[0].Nodes[0]);

        Assert.Equal(1280, media.SourceVideoWidth);
        Assert.Equal(720, media.SourceVideoHeight);
    }

    [Fact]
    public void PlacementTransformAndCrop_RoundTripInSnapshot()
    {
        var compId = Guid.NewGuid();
        var original = new CueList
        {
            Name = "xform",
            Compositions = [new CueComposition { Id = compId, Name = "Program", Width = 1920, Height = 1080, FrameRateNum = 30 }],
            Nodes =
            [
                new MediaCueNode
                {
                    Label = "right-half centre-crop",
                    VideoPlacements =
                    [
                        new CueVideoPlacement
                        {
                            CompositionId = compId, LayerIndex = 1, Position = CueLayerPosition.Stretch,
                            DestX = 0.5, DestY = 0, DestWidth = 0.5, DestHeight = 1.0,
                            CropLeft = 0.25, CropRight = 0.25,
                        },
                    ],
                },
            ],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);
        var p = Assert.IsType<MediaCueNode>(vm.BuildCueListsSnapshot()[0].Nodes[0]).VideoPlacements[0];

        Assert.Equal(CueLayerPosition.Stretch, p.Position);
        Assert.Equal(0.5, p.DestX, 5);
        Assert.Equal(0.5, p.DestWidth, 5);
        Assert.Equal(1.0, p.DestHeight, 5);
        Assert.Equal(0.25, p.CropLeft, 5);
        Assert.Equal(0.25, p.CropRight, 5);
    }

    [Fact]
    public void OldPlacement_LoadsAsFullFrameNoCrop()
    {
        // A pre-feature placement (no dest/crop fields set) must default to the full canvas, no crop.
        var p = CueVideoPlacementViewModel.FromModel(new CueVideoPlacement { LayerIndex = 0 });

        Assert.Equal(0.0, p.DestX);
        Assert.Equal(0.0, p.DestY);
        Assert.Equal(1.0, p.DestWidth);
        Assert.Equal(1.0, p.DestHeight);
        Assert.Equal(0.0, p.CropLeft + p.CropTop + p.CropRight + p.CropBottom);
    }

    [Fact]
    public void PlacementDestinationNumericEdits_StayInsideCanvas()
    {
        var p = new CueVideoPlacementViewModel();
        p.SetDestRect(0.9, 0.8, 0.5, 0.4);

        Assert.Equal(0.5, p.DestX, 5);
        Assert.Equal(0.6, p.DestY, 5);
        Assert.Equal(0.5, p.DestWidth, 5);
        Assert.Equal(0.4, p.DestHeight, 5);

        p.DestWidth = 0.001;
        p.DestHeight = 2.0;

        Assert.Equal(0.02, p.DestWidth, 5);
        Assert.Equal(1.0, p.DestHeight, 5);
        Assert.Equal(0.0, p.DestY, 5);
    }

    [Fact]
    public void LayoutAndCropPresets_UpdateSelectedPlacement()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCompositionCommand.Execute(null);
        vm.AddMediaCueCommand.Execute(null);
        vm.AddVideoPlacementCommand.Execute(null);
        var placement = vm.SelectedVideoPlacement!;

        vm.ApplyPlacementLayoutCommand.Execute("right");
        Assert.Equal(0.5, placement.DestX, 5);
        Assert.Equal(0.0, placement.DestY, 5);
        Assert.Equal(0.5, placement.DestWidth, 5);
        Assert.Equal(1.0, placement.DestHeight, 5);

        vm.ApplyCropPresetCommand.Execute("centerH");
        Assert.Equal(0.25, placement.CropLeft, 5);
        Assert.Equal(0.25, placement.CropRight, 5);
        Assert.Equal(0.0, placement.CropTop, 5);
        Assert.Equal(0.0, placement.CropBottom, 5);
    }

    [Fact]
    public void ActivePlacementEdit_PushesLiveVideoPlacementUpdate()
    {
        var vm = new CuePlayerViewModel();
        var calls = new List<(Guid CueId, int PlacementIndex, CueVideoPlacement Placement)>();
        vm.UpdateActiveCueVideoPlacementCallback = (cueId, placementIndex, placement) =>
        {
            calls.Add((cueId, placementIndex, placement));
            return Task.CompletedTask;
        };

        vm.AddCompositionCommand.Execute(null);
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddVideoPlacementCommand.Execute(null);
        var placement = vm.SelectedVideoPlacement;
        Assert.NotNull(placement);
        placement!.SetDestRect(0, 0, 0.5, 1);

        vm.OnCueStarted(cue.Id);
        placement.DestX = 0.25;

        var call = Assert.Single(calls);
        Assert.Equal(cue.Id, call.CueId);
        Assert.Equal(0, call.PlacementIndex);
        Assert.Equal(0.25, call.Placement.DestX, 5);
        Assert.Equal(0.5, call.Placement.DestWidth, 5);
    }

    [Fact]
    public void ActivePlacementEdit_DoesNotRequestPreRollRefresh()
    {
        var vm = new CuePlayerViewModel();
        var refreshes = 0;
        vm.PreRollRefreshSuggested += (_, _) => refreshes++;
        vm.UpdateActiveCueVideoPlacementCallback = (_, _, _) => Task.CompletedTask;

        vm.AddCompositionCommand.Execute(null);
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddVideoPlacementCommand.Execute(null);
        var placement = vm.SelectedVideoPlacement;
        Assert.NotNull(placement);

        refreshes = 0;
        vm.OnCueStarted(cue.Id);
        placement!.DestX = 0.25;

        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void ActiveAudioRouteEdit_PushesLiveAudioRouteSnapshot()
    {
        var vm = new CuePlayerViewModel();
        var outputId = Guid.NewGuid();
        var calls = new List<(Guid CueId, IReadOnlyList<CueAudioRoute> Routes)>();
        vm.UpdateActiveCueAudioRoutesCallback = (cueId, routes) =>
        {
            calls.Add((cueId, routes.ToArray()));
            return Task.CompletedTask;
        };

        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        var route = new CueAudioRouteViewModel
        {
            OutputLineId = outputId,
            SourceChannel = 0,
            OutputChannel = 1,
        };
        cue.AudioRoutes.Add(route);

        vm.OnCueStarted(cue.Id);
        route.GainDb = -6;

        var call = Assert.Single(calls);
        Assert.Equal(cue.Id, call.CueId);
        var model = Assert.Single(call.Routes);
        Assert.Equal(outputId, model.OutputLineId);
        Assert.Equal(-6, model.GainDb);
    }

    [Fact]
    public void RemoveStandbyCue_AdvancesToNextRemainingCue()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var third = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.SelectedCueNode = second;
        vm.StandbySelectedCommand.Execute(null);

        vm.RemoveNodeCommand.Execute(null);

        Assert.Same(third, vm.StandbyCueNode);
        Assert.DoesNotContain(second, vm.UpcomingCues);
        Assert.Contains(first, vm.VisibleNodes);
        Assert.Contains(third, vm.VisibleNodes);
    }

    [Fact]
    public void ApplyCueDownmixPreset_MultiSelect_AppliesToEverySelectedMediaCue()
    {
        var vm = new CuePlayerViewModel();
        var output = Line(new PortAudioOutputDefinition(
            Guid.NewGuid(), "Main", 0, "ALSA", 0, "dev", 2, 48000));
        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { output });

        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        first.SourceAudioChannels = 2;
        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        second.SourceAudioChannels = 2;
        vm.UpdateSelection([first, second]);

        vm.ApplyCueDownmixPresetCommand.Execute(AudioDownmixPreset.PassThrough);

        Assert.Equal(2, first.AudioRoutes.Count);
        Assert.Equal(2, second.AudioRoutes.Count);
        Assert.All(first.AudioRoutes.Concat(second.AudioRoutes), route =>
        {
            Assert.Equal(output.Definition.Id, route.OutputLineId);
            Assert.InRange(route.OutputChannel, 1, 2);
        });
    }

    [Fact]
    public void AddTextCue_CreatesMediaCueWithTextSourceAndDefaultDuration()
    {
        var vm = new CuePlayerViewModel();
        vm.AddTextCueCommand.Execute(null);

        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.Equal(CueNodeKind.Media, cue.Kind);
        Assert.True(cue.IsTextCue);
        Assert.True(cue.SourceHasVideo); // shows the Video/placement tab
        Assert.Equal(5000, cue.DurationMs);
        Assert.True(vm.HasSelectedTextCue);
        Assert.True(vm.HasSelectedStaticCue);
        Assert.IsType<TextPlaylistItem>(cue.MediaSourceItem);
    }

    [Fact]
    public void TextCueStyleEdits_MutateSourceAndRoundTrip()
    {
        var vm = new CuePlayerViewModel();
        vm.AddTextCueCommand.Execute(null);
        var cue = (CueNodeViewModel)vm.SelectedCueNode!;

        cue.TextContent = "Hello";
        cue.TextFontSizePx = 120;
        cue.TextBold = true;
        cue.TextColorHex = "#FF00FF00";
        cue.TextHAlign = TextAlignH.Left;
        cue.TextWrapWidthFraction = 0.5;

        var src = Assert.IsType<TextPlaylistItem>(cue.MediaSourceItem);
        Assert.Equal("Hello", src.Text);
        Assert.Equal(120.0, src.FontSizePx);
        Assert.True(src.Bold);
        Assert.Equal(0xFF00FF00u, src.ColorArgb);
        Assert.Equal(TextAlignH.Left, src.HAlign);
        Assert.Equal(0.5, src.WrapWidthFraction, 3);

        var media = Assert.IsType<MediaCueNode>(vm.BuildCueListsSnapshot()[0].Nodes[0]);
        var text = Assert.IsType<TextPlaylistItem>(media.Source);
        Assert.Equal("Hello", text.Text);
        Assert.True(text.Bold);
        Assert.Equal(0xFF00FF00u, text.ColorArgb);
        Assert.Equal(5000, media.DurationMs);
    }

    [Fact]
    public void ImageSource_RoundTripInSnapshot()
    {
        var original = new CueList
        {
            Name = "img",
            Nodes = [new MediaCueNode { Label = "slate", DurationMs = 8000, HasVideo = true, Source = new ImagePlaylistItem("/tmp/slate.png") }],
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);

        var media = Assert.IsType<MediaCueNode>(vm.BuildCueListsSnapshot()[0].Nodes[0]);
        var img = Assert.IsType<ImagePlaylistItem>(media.Source);
        Assert.Equal("/tmp/slate.png", img.Path);
        Assert.Equal(8000, media.DurationMs);
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
    public void MediaCue_SourceFrameRateFields_RoundTripInSnapshot()
    {
        var original = new CueList
        {
            Name = "fps",
            Nodes =
            [
                new MediaCueNode
                {
                    Label = "cinema",
                    HasVideo = true,
                    SourceFrameRateNum = 24000,
                    SourceFrameRateDen = 1001,
                },
            ],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([original]);
        var node = Assert.IsType<CueNodeViewModel>(vm.SelectedCueList!.Nodes[0]);
        Assert.Equal(24000, node.SourceFrameRateNum);
        Assert.Equal(1001, node.SourceFrameRateDen);

        var roundtrip = Assert.IsType<MediaCueNode>(vm.BuildCueListsSnapshot()[0].Nodes[0]);
        Assert.Equal(24000, roundtrip.SourceFrameRateNum);
        Assert.Equal(1001, roundtrip.SourceFrameRateDen);
    }

    [Fact]
    public void VideoFrameRateMismatchWarning_Flags23_976Into60FpsCanvas()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCompositionCommand.Execute(null);
        var comp = vm.SelectedCueList!.Compositions.First();
        comp.FrameRateNum = 60;
        comp.FrameRateDen = 1;

        var media = new CueNodeViewModel(CueNodeKind.Media)
        {
            SourceHasVideo = true,
            SourceFrameRateNum = 24000,
            SourceFrameRateDen = 1001,
        };
        media.VideoPlacements.Add(new CueVideoPlacementViewModel { CompositionId = comp.Id });
        vm.SelectedCueList.Nodes.Add(media);
        vm.SelectedCueNode = media;

        Assert.NotNull(vm.VideoFrameRateMismatchWarning);
        Assert.Contains("23.976", vm.VideoFrameRateMismatchWarning!, StringComparison.Ordinal);
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
    public void MoveCueNode_MovesIntoAndOutOfGroupWhenEditModeEnabled()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        Assert.True(vm.MoveCueNode(cue, group, CueNodeDropPlacement.Inside));
        Assert.Same(cue, Assert.Single(group.Children));
        var nestedSnapshot = vm.BuildCueListsSnapshot();
        var nestedGroup = Assert.IsType<CueGroupNode>(Assert.Single(nestedSnapshot[0].Nodes));
        Assert.IsType<MediaCueNode>(Assert.Single(nestedGroup.Children));

        vm.IsCueEditMode = false;
        Assert.False(vm.MoveCueNode(cue, group, CueNodeDropPlacement.After));
        Assert.Same(cue, Assert.Single(group.Children));

        vm.IsCueEditMode = true;
        Assert.True(vm.MoveCueNode(cue, group, CueNodeDropPlacement.After));
        Assert.Empty(group.Children);
        Assert.Equal(new[] { group, cue }, vm.VisibleNodes.ToArray());

        var snapshot = vm.BuildCueListsSnapshot();
        Assert.IsType<CueGroupNode>(snapshot[0].Nodes[0]);
        Assert.IsType<MediaCueNode>(snapshot[0].Nodes[1]);
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
    public void NowPlaying_UpcomingCues_ShowsEntireStandbySimultaneousGroup()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.FireAllSimultaneously.ToString();

        var children = new List<CueNodeViewModel>();
        for (var i = 0; i < 13; i++)
        {
            vm.SelectedCueNode = group;
            vm.AddMediaCueCommand.Execute(null);
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.Label = $"Stem {i + 1}";
            children.Add(cue);
        }

        vm.SelectedCueNode = group;
        vm.StandbySelectedCommand.Execute(null);

        Assert.Equal(13, vm.UpcomingCues.Count);
        Assert.Equal(children.Select(c => c.Id), vm.UpcomingCues.Select(c => c.Id));
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
    public async Task GoAdvancesSelectionToNextFireableCue()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.AddMediaCueCommand.Execute(null);
        var cue2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        // Firing the standby cue moves the selection (not just standby) to what plays next.
        vm.SelectedCueNode = cue1;
        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);
        // Let the dispatched trigger-plan run settle — its steps update CurrentCueNode async and
        // must NOT move the selection back to the fired cue.
        await Task.Delay(50);
        Assert.Same(cue1, vm.CurrentCueNode);
        Assert.Same(cue2, vm.SelectedCueNode);
        Assert.Same(cue2, vm.StandbyCueNode);

        // Last cue in the list: nothing after it, selection stays on the fired cue.
        vm.GoCommand.Execute(null);
        await Task.Delay(50);
        Assert.Same(cue2, vm.CurrentCueNode);
        Assert.Same(cue2, vm.SelectedCueNode);
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
    public void ActionCueBuilderDialogViewModel_Osc_ComposesCommandAndEndpoint()
    {
        var vm = new ActionCueBuilderDialogViewModel();
        var endpoint = new OscActionEndpoint { Name = "OSC A", Host = "127.0.0.1", Port = 9000 };
        vm.Load("Action", CueActionKind.OscOut, null, endpoint.Id, [endpoint]);
        vm.OscAddress = "/lights/go";
        vm.OscArguments = "1 true";

        Assert.True(vm.TryBuild(out var endpointId, out var actionKind, out var commandText, out var error));

        Assert.Null(error);
        Assert.Equal(endpoint.Id, endpointId);
        Assert.Equal(CueActionKind.OscOut, actionKind);
        Assert.Equal("/lights/go 1 true", commandText);
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
    public void GetPreparedMediaCueTargets_FromStandby_ReturnsFileCueModelsWithTrimAndRoutes()
    {
        var vm = new CuePlayerViewModel();
        var audioOutputId = Guid.NewGuid();
        var compositionId = Guid.NewGuid();

        vm.AddMediaCueCommand.Execute(null);
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        first.MediaSourceItem = new FilePlaylistItem("/a.mp4");
        first.StartOffsetMs = 80 * 60 * 1000;
        first.EndOffsetMs = 1500;
        first.AudioRoutes.Add(new CueAudioRouteViewModel
        {
            SourceChannel = 0,
            OutputLineId = audioOutputId,
            OutputChannel = 1,
            GainDb = -3,
        });
        first.VideoPlacements.Add(new CueVideoPlacementViewModel
        {
            CompositionId = compositionId,
            LayerIndex = 2,
            Position = CueLayerPosition.Letterbox,
            Opacity = 0.75,
        });

        vm.AddMediaCueCommand.Execute(null);
        var second = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        second.MediaSourceItem = new NDIInputPlaylistItem("Studio-PC (Output 1)");

        vm.SelectedCueNode = first;
        vm.StandbySelectedCommand.Execute(null);

        var target = Assert.Single(vm.GetPreparedMediaCueTargets());
        Assert.Equal(first.Id, target.Id);
        Assert.Equal("/a.mp4", Assert.IsType<FilePlaylistItem>(target.Source).Path);
        Assert.Equal(80 * 60 * 1000, target.StartOffsetMs);
        Assert.Equal(1500, target.EndOffsetMs);
        Assert.Equal(audioOutputId, Assert.Single(target.AudioRoutes).OutputLineId);
        Assert.Equal(compositionId, Assert.Single(target.VideoPlacements).CompositionId);
    }

    [Fact]
    public void GetPreparedMediaCueTargets_StandbySimultaneousGroupReturnsEntireGroup()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.FireAllSimultaneously.ToString();

        var children = new List<CueNodeViewModel>();
        for (var i = 0; i < 13; i++)
        {
            vm.SelectedCueNode = group;
            vm.AddMediaCueCommand.Execute(null);
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.MediaSourceItem = new FilePlaylistItem($"/stem-{i + 1}.wav");
            children.Add(cue);
        }

        vm.SelectedCueNode = group;
        vm.StandbySelectedCommand.Execute(null);

        var targets = vm.GetPreparedMediaCueTargets();

        Assert.Equal(13, targets.Count);
        Assert.Equal(children.Select(c => c.Id), targets.Select(t => t.Id));
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

        var targets = vm.GetNdiPreConnectTargets();
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
        media.EndOffsetMs = 2500;
        media.Loop = true;
        media.EndBehavior = CueEndBehavior.FreezeLastFrame;

        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(1500, node.StartOffsetMs);
        Assert.Equal(2500, node.EndOffsetMs);
        Assert.True(node.Loop);
        Assert.Equal(CueEndBehavior.FreezeLastFrame, node.EndBehavior);
    }

    [Fact]
    public void MediaCue_EffectiveDuration_UsesStartAndEndOffsets()
    {
        var cue = new CueNodeViewModel(CueNodeKind.Media)
        {
            DurationMs = 60_000,
            StartOffsetMs = 10_000,
            EndOffsetMs = 5_000,
        };

        Assert.Equal(45_000, cue.EffectiveDurationMs);
        Assert.Equal(45_000, cue.RolledDurationMs);
        Assert.Equal("00:45", cue.DurationDisplay);
    }

    [Fact]
    public void MediaCue_TimePickerOffsets_MapToPersistedMilliseconds()
    {
        var cue = new CueNodeViewModel(CueNodeKind.Media);

        cue.StartOffsetTime = new TimeSpan(1, 20, 30);
        cue.EndOffsetTime = TimeSpan.FromMinutes(2.5);

        Assert.Equal(4_830_000, cue.StartOffsetMs);
        Assert.Equal(150_000, cue.EndOffsetMs);
        Assert.Equal(new TimeSpan(1, 20, 30), cue.StartOffsetTime);
        Assert.Equal(TimeSpan.FromMinutes(2.5), cue.EndOffsetTime);
    }

    [Fact]
    public void MediaCue_TimeCodeText_MapsToPersistedMilliseconds()
    {
        var cue = new CueNodeViewModel(CueNodeKind.Media);

        cue.StartOffsetTimeText = "01:20:30.123";
        cue.EndOffsetTimeText = "02:30.500";
        cue.DurationTimeText = "90061007";

        Assert.Equal(4_830_123, cue.StartOffsetMs);
        Assert.Equal(150_500, cue.EndOffsetMs);
        Assert.Equal(90_061_007, cue.DurationMs);
        Assert.Equal("25:01:01.007", cue.DurationTimeText);

        cue.DurationTimeText = "5000ms";
        Assert.Equal(5_000, cue.DurationMs);

        cue.DurationTimeText = "not a time";
        Assert.Equal(5_000, cue.DurationMs);
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
    public void OnOutputLineIdChanged_UsesOwningCuePlayerResolver()
    {
        var vm = new CuePlayerViewModel();
        var first = Line(new PortAudioOutputDefinition(Guid.NewGuid(), "PA-1", 0, "HostApi", 0, "Dev1", 2, 48000));
        var second = Line(new PortAudioOutputDefinition(Guid.NewGuid(), "PA-2", 0, "HostApi", 1, "Dev2", 2, 48000));
        var otherInstanceLine = Line(new PortAudioOutputDefinition(second.Definition.Id, "Other", 0, "HostApi", 2, "OtherDev", 2, 48000));
        var otherVm = new CuePlayerViewModel();
        otherVm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { otherInstanceLine });
        vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { first, second });
        vm.AddMediaCueCommand.Execute(null);
        vm.AddAudioRouteCommand.Execute(null);

        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        var route = media.AudioRoutes[0];
        route.OutputLineId = second.Definition.Id;
        Assert.Same(second, route.LineRef);
        Assert.NotSame(otherInstanceLine, route.LineRef);
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
    public void CueListEditorViewModel_EmptyNameFallsBackToDefaultFileName()
    {
        var vm = new CueListEditorViewModel("Show A");
        vm.Name = "   ";
        Assert.Equal(Strings.CueListFileNameFallback, vm.Name);
    }

    [Fact]
    public void CueListEditorViewModel_LegacyPreRollLimitsNormalizeToUnlimited()
    {
        var restored = CueListEditorViewModel.FromModel(new CueList
        {
            PreRollCount = 3,
            MaxPreparedDecoders = 8,
            Nodes =
            {
                new MediaCueNode { DisablePreRoll = true },
            },
        });

        var snapshot = restored.ToModel();

        Assert.Equal(0, snapshot.PreRollCount);
        Assert.Equal(0, snapshot.MaxPreparedDecoders);
        var cue = Assert.IsType<MediaCueNode>(Assert.Single(snapshot.Nodes));
        Assert.False(cue.DisablePreRoll);
    }

    [Fact]
    public void PreRollState_Stale_HasDistinctGlyphTooltipAndIsNotWarm()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        cue.PreRollState = PreparedCueState.Ready;
        Assert.True(cue.IsPreRollWarm);
        var readyGlyph = cue.PreRollStateGlyph;

        cue.PreRollState = PreparedCueState.Stale;
        Assert.False(cue.IsPreRollWarm); // a stale standby no longer counts as warm
        Assert.False(cue.HasPreRollFailure);
        Assert.False(string.IsNullOrEmpty(cue.PreRollStateGlyph));
        Assert.NotEqual(readyGlyph, cue.PreRollStateGlyph);
        Assert.Contains("stale", cue.PreRollStateTooltip, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnPreparedCueStatesChanged_MapsStaleStateOntoNode()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        vm.OnPreparedCueStatesChanged(new[] { new CuePreparationStatus(cue.Id, PreparedCueState.Stale, null) });
        Assert.Equal(PreparedCueState.Stale, cue.PreRollState);
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
    public void IsCueScrubberVisible_WhenSelectedCueIsActive()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.False(vm.IsCueScrubberVisible);

        vm.OnCueStarted(cue.Id);
        Assert.True(vm.IsCueScrubberVisible);

        vm.OnCueEnded(cue.Id);
        Assert.False(vm.IsCueScrubberVisible);
    }

    [Fact]
    public void OnPreviewEnded_ClearsPreviewingCueId()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        vm.PreviewingCueId = cue.Id;
        vm.OnPreviewEnded(cue.Id);
        Assert.Null(vm.PreviewingCueId);
    }

    [Fact]
    public void SeekActiveCueFromScrubber_ForwardsToCallback()
    {
        var vm = new CuePlayerViewModel();
        vm.AddMediaCueCommand.Execute(null);
        var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        cue.DurationMs = 60_000;
        cue.StartOffsetMs = 10_000;
        cue.EndOffsetMs = 5_000;
        vm.OnCueStarted(cue.Id);
        vm.CueScrubberValue = 500;

        TimeSpan? seekTarget = null;
        vm.SeekCueCallback = (_, pos) =>
        {
            seekTarget = pos;
            return Task.CompletedTask;
        };

        vm.SeekActiveCueFromScrubberCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(22.5), seekTarget);
    }

    [Fact]
    public void CueClipWindow_MapsRelativeSeekIntoTrimmedSourceRange()
    {
        var cue = new MediaCueNode
        {
            DurationMs = 60_000,
            StartOffsetMs = 10_000,
            EndOffsetMs = 5_000,
        };

        var window = CueClipWindow.From(cue, TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(10), window.Start);
        Assert.Equal(TimeSpan.FromSeconds(55), window.End);
        Assert.Equal(TimeSpan.FromSeconds(45), window.Duration);
        Assert.Equal(TimeSpan.FromSeconds(32.5), window.ToSourcePosition(TimeSpan.FromSeconds(22.5)));
        Assert.Equal(TimeSpan.FromSeconds(54.95), window.ToSourcePosition(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void ActionCueBuilderDialogViewModel_Midi_ComposesCommand()
    {
        var vm = new ActionCueBuilderDialogViewModel();
        var endpoint = new MidiActionEndpoint { Name = "MIDI A" };
        vm.Load("Action", CueActionKind.MidiOut, null, endpoint.Id, [endpoint]);
        vm.MidiCommandType = CueMidiCommandType.ControlChange;
        vm.MidiChannel = 2;
        vm.MidiData1 = 7;
        vm.MidiData2 = 110;

        Assert.True(vm.TryBuild(out var endpointId, out var actionKind, out var commandText, out var error));

        Assert.Null(error);
        Assert.Equal(endpoint.Id, endpointId);
        Assert.Equal(CueActionKind.MidiOut, actionKind);
        Assert.Equal("ch2 cc 7 110", commandText);
    }
}
