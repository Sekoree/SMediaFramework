using System;
using System.Linq;
using System.Threading.Tasks;
using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Registry;
using S.Media.Session;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <see cref="HaPlayShowMapper"/> — the GUI <c>CueList</c> → framework <c>ShowDocument</c>
/// bridge for the Phase 8a convergence slice.</summary>
public class ShowDocumentMapperTests
{
    [Fact]
    public void EffectiveOutputMappings_UsesEditorImplicitTileBeforeFirstSave()
    {
        var compositionId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var cueList = new CueList
        {
            Compositions =
            {
                new CueComposition { Id = compositionId, Width = 1920, Height = 1080 },
            },
            VideoOutputs =
            {
                new CueVideoOutputBinding
                {
                    Id = bindingId,
                    CompositionId = compositionId,
                    OutputLineId = lineId,
                    Mapping = null,
                    MappingEnabled = true,
                },
            },
        };
        OutputDefinition output = new LocalVideoOutputDefinition(
            lineId, "Program", VideoOutputEngine.SdlOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 1280, WindowHeight: 720);

        var mappings = HaPlayShowMapper.ResolveEffectiveVideoOutputMappings(cueList, [output]);

        var mapping = Assert.IsType<CueOutputMapping>(mappings[bindingId]);
        Assert.Equal((1280, 720), (mapping.OutputWidth, mapping.OutputHeight));
        var section = Assert.Single(mapping.Sections);
        Assert.Equal((0d, 0d), (section.SrcX, section.SrcY));
        Assert.Equal(2d / 3d, section.SrcWidth, precision: 6);
        Assert.Equal(2d / 3d, section.SrcHeight, precision: 6);
        Assert.Equal((1280d, 720d), (section.DestWidth, section.DestHeight));
    }

    [Fact]
    public void EffectiveOutputMappings_DisabledBindingRemainsRaw()
    {
        var compositionId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var cueList = new CueList
        {
            Compositions = { new CueComposition { Id = compositionId, Width = 1920, Height = 1080 } },
            VideoOutputs =
            {
                new CueVideoOutputBinding
                {
                    Id = bindingId, CompositionId = compositionId, OutputLineId = lineId,
                    MappingEnabled = false,
                },
            },
        };
        OutputDefinition output = new LocalVideoOutputDefinition(
            lineId, "Program", VideoOutputEngine.SdlOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 1280, WindowHeight: 720);

        var mappings = HaPlayShowMapper.ResolveEffectiveVideoOutputMappings(cueList, [output]);

        Assert.Null(mappings[bindingId]);
    }

    [Fact]
    public void MapsCueTree_FlattensGroups_Clips_And_Compositions()
    {
        var compId = Guid.NewGuid();

        var intro = new MediaCueNode
        {
            Label = "Intro",
            PreWaitMs = 250,
            Source = new FilePlaylistItem("/media/intro.mp4"),
            StartOffsetMs = 500,
            EndOffsetMs = 1000,
            FadeInMs = 300,
            FadeOutMs = 750,
            AudioTrackIndex = 2,
            EndBehavior = CueEndBehavior.FreezeLastFrame,
            VideoPlacements = { new CueVideoPlacement { CompositionId = compId, LayerIndex = 1 } },
        };
        var slideA = new MediaCueNode { Label = "Slide A", Source = new ImagePlaylistItem("/media/a.png") };
        var camB = new MediaCueNode { Label = "Cam B", Source = new NDIInputPlaylistItem("STUDIO (CAM 2)") };
        var group = new CueGroupNode { Label = "Act 1", Children = { slideA, camB } };

        var cueList = new CueList
        {
            Name = "Show",
            Nodes = { intro, group },
            Compositions =
            {
                new CueComposition
                {
                    Id = compId,
                    Name = "Main",
                    Width = 1920,
                    Height = 1080,
                    FrameRateNum = 30,
                    FrameRateDen = 1,
                    VideoFxEnabled = true,
                    VideoFx = new CueOutputMapping
                    {
                        OutputWidth = 3840,
                        OutputHeight = 1080,
                        Sections =
                        {
                            new CueOutputMappingSection
                            {
                                Name = "Left", // framework section has no Name — dropped gracefully
                                Enabled = true,
                                SrcX = 0, SrcY = 0, SrcWidth = 0.5, SrcHeight = 1.0,
                                DestX = 0, DestY = 0, DestWidth = 1920, DestHeight = 1080,
                                Brightness = 0.9,
                                MeshColumns = 2,
                                MeshRows = 2,
                                MeshPoints = new() { new(0, 0), new(1, 0), new(0, 1), new(1, 1) },
                            },
                        },
                    },
                },
            },
        };

        var doc = HaPlayShowMapper.ToShowDocument(cueList);

        // Cues: flattened in document order, renumbered 1..N; grouped cues carry the group id.
        Assert.Equal(3, doc.Cues.Count);
        Assert.Equal(new[] { 1, 2, 3 }, doc.Cues.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { "Intro", "Slide A", "Cam B" }, doc.Cues.Select(c => c.Label).ToArray());
        Assert.Null(doc.Cues[0].GroupId);
        Assert.Equal(group.Id.ToString(), doc.Cues[1].GroupId);
        Assert.Equal(group.Id.ToString(), doc.Cues[2].GroupId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), doc.Cues[0].PreWait);

        // Clips: one per resolvable media cue, with playback params + composition placement.
        Assert.Equal(3, doc.Clips.Count);
        var introClip = doc.Clips.Single(c => c.CueId == intro.Id.ToString());
        Assert.Equal("/media/intro.mp4", introClip.MediaPath);
        Assert.Equal(compId.ToString(), introClip.CompositionId);
        Assert.Equal(1, introClip.LayerIndex);
        Assert.Equal(2, introClip.AudioStreamIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(500), introClip.StartOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), introClip.EndOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(300), introClip.FadeIn);
        Assert.Equal(TimeSpan.FromMilliseconds(750), introClip.FadeOut);
        Assert.Equal(ClipEndBehavior.FreezeLastFrame, introClip.EndBehavior);

        // Live + image sources resolve to a path / scheme URI. The NDI URI is the option-carrying descriptor
        // form (shared with the deck) — assert via the provider's parser so option order stays free.
        var camUri = doc.Clips.Single(c => c.CueId == camB.Id.ToString()).MediaPath;
        var camDescriptor = S.Media.NDI.NDIDecoderProvider.ParseSourceUri(camUri);
        Assert.Equal("STUDIO (CAM 2)", camDescriptor.SourceName);
        Assert.True(camDescriptor.ReceiveAudio);
        Assert.True(camDescriptor.ReceiveVideo);
        Assert.Equal("/media/a.png", doc.Clips.Single(c => c.CueId == slideA.Id.ToString()).MediaPath);

        // Composition + output-mapping warp section carried through (1:1, minus Name/corner-pin).
        var comp = Assert.Single(doc.Compositions);
        Assert.Equal("Main", comp.Name);
        Assert.Equal(1920, comp.Width);
        Assert.NotNull(comp.OutputMapping);
        Assert.Equal(3840, comp.OutputMapping!.OutputWidth);
        var section = Assert.Single(comp.OutputMapping.Sections);
        Assert.Equal(0.9, section.Brightness, 3);
        Assert.Equal(2, section.MeshColumns);
        Assert.Equal(4, section.MeshPoints!.Count);
        Assert.Equal(1.0, section.MeshPoints[3].X, 3);
    }

    [Fact]
    public void NestedGroups_CollapseIntoOutermostGroupId()
    {
        // A top-level group is one transport/clock unit; nested subgroups must not split their cues
        // into a separate SessionClock — every descendant carries the OUTERMOST group's id.
        var deep = new MediaCueNode { Label = "Deep", Source = new FilePlaylistItem("/m/deep.mp4") };
        var mid = new MediaCueNode { Label = "Mid", Source = new FilePlaylistItem("/m/mid.mp4") };
        var inner = new CueGroupNode { Label = "Inner", Children = { deep } };
        var outer = new CueGroupNode { Label = "Outer", Children = { mid, inner } };
        var loose = new MediaCueNode { Label = "Loose", Source = new FilePlaylistItem("/m/loose.mp4") };

        var doc = HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { outer, loose } });

        Assert.Equal(new[] { "Mid", "Deep", "Loose" }, doc.Cues.Select(c => c.Label).ToArray());
        // Both the direct child (Mid) and the nested grandchild (Deep) share the OUTER group's id.
        Assert.Equal(outer.Id.ToString(), doc.Cues[0].GroupId);
        Assert.Equal(outer.Id.ToString(), doc.Cues[1].GroupId);
        Assert.NotEqual(inner.Id.ToString(), doc.Cues[1].GroupId);
        // A top-level media cue outside any group stays ungrouped.
        Assert.Null(doc.Cues[2].GroupId);
    }

    [Fact]
    public void MultiplePlacements_MapToPrimaryPlusExtras_OrderedByLayer()
    {
        var compA = Guid.NewGuid();
        var compB = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Label = "PiP",
            Source = new FilePlaylistItem("/m/v.mp4"),
            VideoPlacements =
            {
                // Deliberately out of layer order + one unbound (empty composition) placement that must be dropped.
                new CueVideoPlacement { CompositionId = compB, LayerIndex = 5, Opacity = 0.5 },
                new CueVideoPlacement { CompositionId = Guid.Empty, LayerIndex = 0 },
                new CueVideoPlacement { CompositionId = compA, LayerIndex = 1 },
            },
        };

        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }).Clips);

        // Primary = lowest layer index (compA @1); the higher layer (compB @5) rides along in ExtraPlacements.
        Assert.Equal(compA.ToString(), clip.CompositionId);
        Assert.Equal(1, clip.LayerIndex);
        var extra = Assert.Single(clip.ExtraPlacements!);
        Assert.Equal(compB.ToString(), extra.CompositionId);
        Assert.Equal(5, extra.LayerIndex);
        Assert.Equal(0.5, extra.Placement!.Opacity, 3);

        // GetPlacements() presents the full, layer-ordered fan-out set (unbound placement excluded).
        var all = clip.GetPlacements();
        Assert.Equal(new[] { compA.ToString(), compB.ToString() }, all.Select(p => p.CompositionId).ToArray());
    }

    [Fact]
    public void LoopEndBehavior_SetsLoopFlag()
    {
        var cue = new MediaCueNode
        {
            Label = "BG",
            Source = new FilePlaylistItem("/m/bg.mp4"),
            EndBehavior = CueEndBehavior.Loop,
        };

        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }).Clips);
        Assert.True(clip.Loop);
        Assert.Equal(ClipEndBehavior.Loop, clip.EndBehavior);
    }

    [Fact]
    public void MediaCue_WithoutSource_ProducesCueButNoClip()
    {
        var cue = new MediaCueNode { Label = "Empty", Source = null };

        var doc = HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } });

        Assert.Single(doc.Cues);
        Assert.Empty(doc.Clips);
    }

    [Fact]
    public void MapsVideoPlacement_Appearance()
    {
        var compId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Label = "Placed",
            Source = new FilePlaylistItem("/m/v.mp4"),
            VideoPlacements =
            {
                new CueVideoPlacement
                {
                    CompositionId = compId,
                    LayerIndex = 2,
                    Position = CueLayerPosition.Letterbox,
                    Opacity = 0.5,
                    DestX = 0.25, DestY = 0.1, DestWidth = 0.5, DestHeight = 0.4,
                    CropLeft = 0.05,
                    RotationDegrees = 15,
                },
            },
        };

        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }).Clips);
        Assert.Equal(compId.ToString(), clip.CompositionId);
        Assert.Equal(2, clip.LayerIndex);
        Assert.NotNull(clip.Placement);
        Assert.Equal(0.25, clip.Placement!.DestX, 3);
        Assert.Equal(0.5, clip.Placement.DestY, 3); // UI top-left 0.1 -> compositor bottom-left 1-.1-.4
        Assert.Equal(0.5, clip.Placement.DestWidth, 3);
        Assert.Equal(0.5, clip.Placement.Opacity, 3);
        Assert.Equal(0.05, clip.Placement.CropLeft, 3);
        Assert.Equal(15, clip.Placement.RotationDegrees, 3);
        Assert.Equal("Letterbox", clip.Placement.Fit); // framework MapFit lowercases → Contain
    }

    [Fact]
    public void MapsPerCueAudioRoutes_ToClipAudioRoutes_WithDeviceAndChannelMap()
    {
        var lineId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Label = "Routed",
            Source = new FilePlaylistItem("/m/a.wav"),
            AudioRoutes =
            {
                // Cue routes use the UI/persistence convention: output channels are 1-based.
                new CueAudioRoute { SourceChannel = 0, OutputLineId = lineId, OutputChannel = 1, GainDb = 0 },
                new CueAudioRoute { SourceChannel = 1, OutputLineId = lineId, OutputChannel = 2, GainDb = 0 },
                new CueAudioRoute { SourceChannel = 0, OutputLineId = lineId, OutputChannel = 3, Muted = true }, // dropped
                new CueAudioRoute { SourceChannel = 0, OutputLineId = lineId, OutputChannel = 0, GainDb = 0 }, // invalid/dropped
            },
        };
        var output = new PortAudioOutputDefinition(
            Id: lineId, DisplayName: "Main Out", HostApiIndex: 0, HostApiName: "JACK",
            GlobalDeviceIndex: 7, DeviceName: "system", ChannelCount: 2, SampleRate: 48_000);

        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }, [output]).Clips);

        var route = Assert.Single(clip.AudioRoutes!);
        Assert.Equal("7", route.DeviceId);                  // EffectiveAudioBackendDeviceId → GlobalDeviceIndex
        Assert.Equal(new[] { 0, 1 }, route.ChannelMatrix);  // identity stereo; muted/invalid routes are dropped
        Assert.Equal(1f, route.Gain);                       // 0 dB → linear 1.0
        Assert.Equal(48_000, route.SampleRate);              // output/device rate, not the media source rate
    }

    [Fact]
    public void NoAudioRoutes_MapsToExplicitSilence()
    {
        var cue = new MediaCueNode { Label = "Plain", Source = new FilePlaylistItem("/m/a.wav") };
        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }).Clips);
        Assert.NotNull(clip.AudioRoutes);
        Assert.Empty(clip.AudioRoutes); // HaPlay never assumes a default device; routing is operator-authored.
    }

    [Fact]
    public void NotifyNaturalEnd_SetForFileCues_NotForHeldOrLiveSources()
    {
        // Bare plain-Stop FILE cues must fire cue auto-follow at EOF (the session's NotifyNaturalEnd monitor);
        // held (image/text) and live sources hold/run until the operator moves on, so they must NOT opt in.
        ShowClipBinding Map(PlaylistItem source) =>
            Assert.Single(HaPlayShowMapper.ToShowDocument(
                new CueList { Nodes = { new MediaCueNode { Label = "n", Source = source, DurationMs = 1000 } } }).Clips);

        Assert.True(Map(new FilePlaylistItem("/m/a.wav")).NotifyNaturalEnd);
        Assert.False(Map(new ImagePlaylistItem("/m/a.png")).NotifyNaturalEnd);
        Assert.False(Map(new TextPlaylistItem { Text = "t" }).NotifyNaturalEnd);
        Assert.False(Map(new NDIInputPlaylistItem("cam")).NotifyNaturalEnd);
        // YouTube plays a finite cached file; an MMD scene is finite exactly when it has a motion.
        Assert.True(Map(new YouTubePlaylistItem("abc123")).NotifyNaturalEnd);
        Assert.True(Map(new MmdPlaylistItem("/m/miku.pmx") { MotionPath = "/m/dance.vmd" }).NotifyNaturalEnd);
        Assert.False(Map(new MmdPlaylistItem("/m/miku.pmx")).NotifyNaturalEnd);
    }

    [Fact]
    public void MmdAndYouTubeCues_MapToTheirDescriptorUris()
    {
        // The cue path must produce the SAME provider URIs as the deck (HaPlayPlaybackHelpers) so a
        // cue-fired item keeps its per-item options — this was the "cue player cannot play MMD/YouTube"
        // gap's mapping leg.
        ShowClipBinding Map(PlaylistItem source) =>
            Assert.Single(HaPlayShowMapper.ToShowDocument(
                new CueList { Nodes = { new MediaCueNode { Label = "n", Source = source } } }).Clips);

        var mmd = Map(new MmdPlaylistItem("/m/miku.pmx") { MotionPath = "/m/dance.vmd", RenderWidth = 1920 });
        Assert.StartsWith("mmd://", mmd.MediaPath);
        Assert.Contains("dance.vmd", mmd.MediaPath);

        var yt = Map(new YouTubePlaylistItem("qdXcG-Fg2Dk") { VideoStreamDescriptor = "1080p|vp9|webm" });
        Assert.StartsWith("youtube://", yt.MediaPath);
        Assert.Contains("qdXcG-Fg2Dk", yt.MediaPath);
    }

    /// <summary>End-to-end: the mapper's output is a valid <see cref="ShowDocument"/> that a real
    /// <see cref="ShowSession"/> loads, exposing the cues in order with their groups (the 8a dispatch seam).</summary>
    [Fact]
    public async Task MappedCueList_LoadsIntoShowSession_WithCuesInOrderAndGroups()
    {
        var a = new MediaCueNode { Label = "A", Source = new FilePlaylistItem("/m/a.mp4") };
        var b = new MediaCueNode { Label = "B", Source = new FilePlaylistItem("/m/b.mp4") };
        var group = new CueGroupNode { Label = "G", Children = { b } };
        var doc = HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { a, group } });

        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));
        session.LoadDocument(doc);

        var cues = await session.GetCueDefinitionsAsync();
        Assert.Equal(new[] { "A", "B" }, cues.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { 1, 2 }, cues.Select(c => c.Number).ToArray());
        Assert.Null(cues[0].GroupId);
        Assert.Equal(group.Id.ToString(), cues[1].GroupId);

        // the mapped document also round-trips through the source-gen JSON (it is persistable).
        Assert.Equal(doc.Cues.Count, ShowDocument.FromJson(doc.ToJson()).Cues.Count);
    }

    [Fact]
    public void MediaPlayerShowMapper_FileWithVideo_BuildsOneCueShowOnAComposition()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument(
            "/m/clip.mp4", hasVideo: true, audioRoutes: [new ShowClipAudioRoute(DeviceId: "hw:0")]);

        var cue = Assert.Single(doc.Cues);
        Assert.Equal(MediaPlayerShowMapper.PlayerCueId, cue.Id);
        var clip = Assert.Single(doc.Clips);
        Assert.Equal("/m/clip.mp4", clip.MediaPath);
        Assert.Equal(MediaPlayerShowMapper.PlayerCompositionId, clip.CompositionId);
        Assert.Equal("hw:0", Assert.Single(clip.AudioRoutes!).DeviceId);
        Assert.Single(doc.Compositions);
    }

    [Fact]
    public void MediaPlayerShowMapper_AudioOnly_HasNoComposition()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/song.wav", hasVideo: false);

        Assert.Empty(doc.Compositions);
        Assert.Null(Assert.Single(doc.Clips).CompositionId);
    }
}
