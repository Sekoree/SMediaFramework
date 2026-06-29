using System.Text.Json;
using HaPlay.Models;
using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class SubtitleSelectionTests
{
    [Fact]
    public void MediaCueNode_Subtitles_RoundTripThroughSourceGenSerialization()
    {
        var cue = new MediaCueNode
        {
            Subtitles =
            [
                new CueSubtitleSelection
                {
                    StreamIndex = 7, Label = "eng", FontFamily = "Noto Sans", FontScale = 1.25, Alignment = 2,
                },
                new CueSubtitleSelection { Path = "/subs/commentary.ass" },
            ],
        };

        var json = JsonSerializer.Serialize(cue, CueListJsonContext.Default.MediaCueNode);
        var back = JsonSerializer.Deserialize(json, CueListJsonContext.Default.MediaCueNode)!;

        Assert.Equal(2, back.Subtitles.Count);
        Assert.Equal(7, back.Subtitles[0].StreamIndex);
        Assert.True(back.Subtitles[0].IsEmbedded);
        Assert.Equal("Noto Sans", back.Subtitles[0].FontFamily);
        Assert.Equal(1.25, back.Subtitles[0].FontScale);
        Assert.Equal(2, back.Subtitles[0].Alignment);
        Assert.Equal("/subs/commentary.ass", back.Subtitles[1].Path);
        Assert.False(back.Subtitles[1].IsEmbedded);
    }

    [Fact]
    public void MediaCueNode_NoSubtitles_DefaultsToEmpty()
    {
        var back = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new MediaCueNode(), CueListJsonContext.Default.MediaCueNode),
            CueListJsonContext.Default.MediaCueNode)!;
        Assert.Empty(back.Subtitles);
    }

    [Fact]
    public void SubtitleTrackProbe_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(SubtitleTrackProbe.List("/no/such/file.mkv"));
        Assert.Empty(SubtitleTrackProbe.List(""));
    }
}
