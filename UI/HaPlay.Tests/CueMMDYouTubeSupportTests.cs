using System.Text.Json;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Gates for MMD + YouTube cues being first-class in the cue player: the standalone cue-list JSON
/// context must round-trip their sources (it listed every other PlaylistItem subtype), and setting
/// one as a cue's media source must raise the drawer-gating capability flags (SourceHasVideo /
/// SourceHasAudio) — without them the Video/Audio tabs never show, so no placement or routing could
/// be authored ("cue player cannot play MMD/YouTube").
/// </summary>
public sealed class CueMMDYouTubeSupportTests
{
    [Fact]
    public void CueListJson_RoundTrips_MMDAndYouTubeSources()
    {
        var cueList = new CueList
        {
            Name = "MMD show",
            Nodes =
            {
                new MediaCueNode
                {
                    Label = "Dance",
                    Source = new MMDPlaylistItem("/models/miku.pmx")
                    {
                        MotionPath = "/motions/rolling-girl.vmd",
                        RenderWidth = 1920,
                        RenderHeight = 1080,
                        CameraFovDeg = 45,
                    },
                },
                new MediaCueNode
                {
                    Label = "Interlude",
                    Source = new YouTubePlaylistItem("qdXcG-Fg2Dk")
                    {
                        Title = "Interlude video",
                        VideoStreamDescriptor = "1080p|vp9|webm",
                        AudioStreamDescriptor = "opus|webm|en",
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(cueList, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;

        var mmd = Assert.IsType<MMDPlaylistItem>(Assert.IsType<MediaCueNode>(loaded.Nodes[0]).Source);
        Assert.Equal("/motions/rolling-girl.vmd", mmd.MotionPath);
        Assert.Equal(45, mmd.CameraFovDeg);

        var yt = Assert.IsType<YouTubePlaylistItem>(Assert.IsType<MediaCueNode>(loaded.Nodes[1]).Source);
        Assert.Equal("qdXcG-Fg2Dk", yt.VideoId);
        Assert.Equal("1080p|vp9|webm", yt.VideoStreamDescriptor);
    }

    [Fact]
    public void MMDCueSource_RaisesVideoCapability_NoAudio()
    {
        var node = new CueNodeViewModel(CueNodeKind.Media)
        {
            MediaSourceItem = new MMDPlaylistItem("/models/miku.pmx")
            {
                RenderWidth = 1280,
                RenderHeight = 720,
            },
        };

        Assert.True(node.SourceHasVideo);
        Assert.False(node.SourceHasAudio);
        Assert.Equal(0, node.SourceAudioChannels);
        Assert.Equal(1280, node.SourceVideoWidth);
        Assert.Equal(720, node.SourceVideoHeight);
        Assert.Equal(30, node.SourceFrameRateNum);
        Assert.Equal(1, node.SourceFrameRateDen);
    }

    [Fact]
    public void YouTubeCueSource_RaisesAudioAndVideoCapabilities()
    {
        var node = new CueNodeViewModel(CueNodeKind.Media)
        {
            MediaSourceItem = new YouTubePlaylistItem("abc123"),
        };
        Assert.True(node.SourceHasVideo);
        Assert.True(node.SourceHasAudio);
        Assert.True(node.SourceAudioChannels >= 2);

        var audioOnly = new CueNodeViewModel(CueNodeKind.Media)
        {
            MediaSourceItem = new YouTubePlaylistItem("abc123") { AudioOnly = true },
        };
        Assert.False(audioOnly.SourceHasVideo);
        Assert.True(audioOnly.SourceHasAudio);
    }
}
