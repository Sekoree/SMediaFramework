using HaPlay.Models;
using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <c>MediaPlayerShowMapper.ToShowDocument</c> — the deck→ShowDocument mapping on the
/// ShowSession path: composition canvas sizing and subtitle-track selection. Audio routing is covered by
/// <see cref="MediaPlayerDeckAudioRoutingTests"/>.</summary>
public sealed class MediaPlayerShowMapperTests
{
    [Fact]
    public void AudioOnly_HasNoCompositionAndNoSubtitles()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/song.flac", hasVideo: false,
            subtitles: [new CueSubtitleSelection { Path = "/m/song.srt" }]);

        Assert.Empty(doc.Compositions);
        // Even though a subtitle was selected, an audio-only source has no canvas to draw it on.
        Assert.Null(Assert.Single(doc.Clips).Subtitles);
        Assert.Null(Assert.Single(doc.Clips).CompositionId);
    }

    [Fact]
    public void Video_CarriesCanvasSizeOntoComposition()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/movie.mkv", hasVideo: true,
            canvasWidth: 3840, canvasHeight: 2160);

        var comp = Assert.Single(doc.Compositions);
        Assert.Equal(3840, comp.Width);
        Assert.Equal(2160, comp.Height);
        Assert.Equal(MediaPlayerShowMapper.PlayerCompositionId, Assert.Single(doc.Clips).CompositionId);
    }

    [Fact]
    public void EmbeddedSubtitle_MapsToStreamIndex()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/movie.mkv", hasVideo: true,
            subtitles: [new CueSubtitleSelection { StreamIndex = 3, Label = "English" }]);

        var sub = Assert.Single(Assert.Single(doc.Clips).Subtitles!);
        Assert.Equal(3, sub.StreamIndex);
        Assert.Null(sub.Path);
    }

    [Fact]
    public void SidecarSubtitle_MapsToPath_WithSentinelStreamIndex()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/movie.mkv", hasVideo: true,
            subtitles: [new CueSubtitleSelection { Path = "/m/movie.en.srt" }]);

        var sub = Assert.Single(Assert.Single(doc.Clips).Subtitles!);
        Assert.Equal("/m/movie.en.srt", sub.Path);
        Assert.Equal(-1, sub.StreamIndex); // sidecar → the file's default/only track
    }

    [Fact]
    public void MultipleSelections_PreserveOrder_EmptyOnesDropped()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/movie.mkv", hasVideo: true,
            subtitles:
            [
                new CueSubtitleSelection { StreamIndex = 2 },
                new CueSubtitleSelection(), // neither stream nor path → dropped
                new CueSubtitleSelection { Path = "/m/movie.jp.ass" },
            ]);

        var subs = Assert.Single(doc.Clips).Subtitles!;
        Assert.Equal(2, subs.Count);
        Assert.Equal(2, subs[0].StreamIndex);
        Assert.Equal("/m/movie.jp.ass", subs[1].Path);
    }

    [Fact]
    public void NoSubtitles_LeavesSubtitlesNull()
    {
        var doc = MediaPlayerShowMapper.ToShowDocument("/m/movie.mkv", hasVideo: true);
        Assert.Null(Assert.Single(doc.Clips).Subtitles);
    }
}
