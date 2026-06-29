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

        // Live + image sources resolve to a path / scheme URI.
        Assert.Equal("ndi://STUDIO (CAM 2)", doc.Clips.Single(c => c.CueId == camB.Id.ToString()).MediaPath);
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
                    RotationDegrees = 15,
                },
            },
        };

        var clip = Assert.Single(HaPlayShowMapper.ToShowDocument(new CueList { Nodes = { cue } }).Clips);
        Assert.Equal(compId.ToString(), clip.CompositionId);
        Assert.Equal(2, clip.LayerIndex);
        Assert.NotNull(clip.Placement);
        Assert.Equal(0.25, clip.Placement!.DestX, 3);
        Assert.Equal(0.5, clip.Placement.DestWidth, 3);
        Assert.Equal(0.5, clip.Placement.Opacity, 3);
        Assert.Equal(15, clip.Placement.RotationDegrees, 3);
        Assert.Equal("Letterbox", clip.Placement.Fit); // framework MapFit lowercases → Contain
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
}
