using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Source.MMD;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// VM-level gates for the common per-item properties dialog: edits must replace the working item
/// (records) while preserving <see cref="PlaylistItem.Id"/>, and the Details/Tracks/Scene state must
/// re-derive from the replaced item. Probes are injected so no FFmpeg natives are needed.
/// </summary>
public sealed class MediaPropertiesDialogViewModelTests
{
    private static MediaStreamInfo Stream(
        int index, MediaStreamKind kind, string codec, int channels = 0, int sampleRate = 0,
        int width = 0, int height = 0, long bitRate = 0) =>
        new(index, kind, codec, Language: null, Title: null, channels, sampleRate, width, height,
            kind == MediaStreamKind.Video ? new Rational(30, 1) : new Rational(0, 1),
            IsDefault: false, IsForced: false, IsAttachedPicture: false, IsDecodable: true)
        {
            BitRate = bitRate,
        };

    private static MediaPropertiesDialogViewModel CreateVm(PlaylistItem item) =>
        new(item) { FileExists = _ => true };

    [Fact]
    public void AudioTrackSelection_ReplacesFileItem_KeepingIdAndOtherFields()
    {
        var item = new FilePlaylistItem("/media/show.mkv")
        {
            Subtitles = [new CueSubtitleSelection { StreamIndex = 3 }],
        };
        var vm = CreateVm(item);
        vm.PublishAudioTracks(
        [
            Stream(1, MediaStreamKind.Audio, "aac", channels: 2, sampleRate: 48000),
            Stream(2, MediaStreamKind.Audio, "ac3", channels: 6, sampleRate: 48000),
        ]);

        // Automatic + 2 tracks; current selection is Automatic (item has no explicit index).
        Assert.Equal(3, vm.AudioTrackOptions.Count);
        Assert.True(vm.HasMultipleAudioTracks);
        Assert.Null(vm.SelectedAudioTrack!.Index);

        vm.SelectedAudioTrack = vm.AudioTrackOptions[2];

        var updated = Assert.IsType<FilePlaylistItem>(vm.BuildResult());
        Assert.Equal(2, updated.AudioTrackIndex);
        Assert.Equal(item.Id, updated.Id);
        Assert.Single(updated.Subtitles); // unrelated fields survive the record replace

        vm.SelectedAudioTrack = vm.AudioTrackOptions[0];
        Assert.Null(Assert.IsType<FilePlaylistItem>(vm.BuildResult()).AudioTrackIndex);
    }

    [Fact]
    public void PublishAudioTracks_PreselectsTheItemsExplicitTrack()
    {
        var vm = CreateVm(new FilePlaylistItem("/media/show.mkv") { AudioTrackIndex = 2 });
        vm.PublishAudioTracks(
        [
            Stream(1, MediaStreamKind.Audio, "aac", channels: 2, sampleRate: 48000),
            Stream(2, MediaStreamKind.Audio, "ac3", channels: 6, sampleRate: 48000),
        ]);

        Assert.Equal(2, vm.SelectedAudioTrack!.Index);
        // Preselection must not count as an edit.
        Assert.Equal(2, Assert.IsType<FilePlaylistItem>(vm.BuildResult()).AudioTrackIndex);
    }

    [Fact]
    public void ApplySubtitles_UpdatesItemAndSummary()
    {
        var vm = CreateVm(new FilePlaylistItem("/media/show.mkv"));
        Assert.Equal(HaPlay.Resources.Strings.MediaPropertiesSubtitlesNone, vm.SubtitleSummary);

        vm.ApplySubtitles([new CueSubtitleSelection { StreamIndex = 2 }, new CueSubtitleSelection { StreamIndex = 4 }]);

        Assert.Equal(2, Assert.IsType<FilePlaylistItem>(vm.BuildResult()).Subtitles.Count);
        Assert.Contains("2", vm.SubtitleSummary);
    }

    [Fact]
    public void ReplaceItem_AcceptsSameId_RejectsForeignId()
    {
        var original = new MMDPlaylistItem("/models/miku.pmx") { RenderWidth = 1280, RenderHeight = 720 };
        var vm = CreateVm(original);

        // A stale result from another item must not clobber the working copy.
        vm.ReplaceItem(new MMDPlaylistItem("/models/other.pmx"));
        Assert.Same(original, vm.BuildResult());

        var edited = original with { RenderWidth = 1920, RenderHeight = 1080 };
        vm.ReplaceItem(edited);
        Assert.Same(edited, vm.BuildResult());
        Assert.Contains(vm.SceneRows, r => r.Value.Contains("1920×1080"));
    }

    [Fact]
    public void PublishContainerInfo_BuildsDetailAndStreamRows()
    {
        var vm = CreateVm(new FilePlaylistItem("/media/show.mkv"));
        vm.PublishContainerInfo(new MediaContainerInfo(
            "matroska,webm",
            TimeSpan.FromSeconds(213),
            4_200_000,
            110 * 1024 * 1024,
            [
                Stream(0, MediaStreamKind.Video, "h264", width: 1920, height: 1080, bitRate: 3_800_000),
                Stream(1, MediaStreamKind.Audio, "aac", channels: 2, sampleRate: 48000, bitRate: 192_000),
            ]));

        Assert.Contains(vm.DetailRows, r => r.Value == "matroska,webm");
        Assert.Contains(vm.DetailRows, r => r.Value == "3:33");
        Assert.Contains(vm.DetailRows, r => r.Value == "4.2 Mbit/s");
        Assert.Contains(vm.DetailRows, r => r.Value == "110 MiB");
        Assert.Contains(vm.DetailRows, r => r.Value == "1920×1080");
        Assert.Contains(vm.DetailRows, r => r.Value == "30 fps");
        Assert.True(vm.HasStreamLines);
        Assert.Equal(2, vm.StreamLines.Count);
        Assert.Contains("192 kbit/s", vm.StreamLines[1]);
    }

    [Fact]
    public void TabVisibility_FollowsItemKind()
    {
        Assert.True(CreateVm(new FilePlaylistItem("/a.mkv")).IsFileItem);
        Assert.True(CreateVm(new MMDPlaylistItem("/m.pmx")).IsMMDItem);
        Assert.True(CreateVm(new YouTubePlaylistItem("abc123")).IsYouTubeItem);

        var vm = CreateVm(new TextPlaylistItem());
        Assert.False(vm.IsFileItem);
        Assert.False(vm.IsMMDItem);
        Assert.False(vm.IsYouTubeItem);
    }

    [Fact]
    public void BakeStatus_TracksSceneState()
    {
        var previousCacheDir = MMDPhysicsBakeCache.CacheDirectory;
        MMDPhysicsBakeCache.CacheDirectory = Directory.CreateTempSubdirectory("bake-test-").FullName;
        try
        {
            var noPhysics = CreateVm(new MMDPlaylistItem("/m.pmx") { Physics = false, MotionPath = "/m.vmd" });
            Assert.Equal(HaPlay.Resources.Strings.MediaPropertiesBakeStatusDisabled, noPhysics.BakeStatus);
            Assert.False(noPhysics.CanBakePhysics);

            var noMotion = CreateVm(new MMDPlaylistItem("/m.pmx"));
            Assert.Equal(HaPlay.Resources.Strings.MediaPropertiesBakeStatusNoMotion, noMotion.BakeStatus);
            Assert.False(noMotion.CanBakePhysics);

            var bakeable = CreateVm(new MMDPlaylistItem("/m.pmx") { MotionPath = "/m.vmd" });
            Assert.Equal(HaPlay.Resources.Strings.MediaPropertiesBakeStatusNotCached, bakeable.BakeStatus);
            Assert.True(bakeable.CanBakePhysics);

            // Files missing on disk → the bake button stays disabled even for a physics+motion scene.
            var missingFiles = new MediaPropertiesDialogViewModel(
                new MMDPlaylistItem("/m.pmx") { MotionPath = "/m.vmd" }) { FileExists = _ => false };
            Assert.False(missingFiles.CanBakePhysics);
        }
        finally
        {
            try { Directory.Delete(MMDPhysicsBakeCache.CacheDirectory, recursive: true); } catch { /* best effort */ }
            MMDPhysicsBakeCache.CacheDirectory = previousCacheDir;
        }
    }

    [Fact]
    public void YouTubeRows_ShowDescriptorsAndCacheState()
    {
        var vm = CreateVm(new YouTubePlaylistItem("qdXcG-Fg2Dk")
        {
            Title = "Test video",
            Author = "Uploader",
            VideoStreamDescriptor = "1080p|vp9|webm",
            AudioStreamDescriptor = "opus|webm|en",
        });

        Assert.Contains(vm.YouTubeRows, r => r.Value == "qdXcG-Fg2Dk");
        Assert.Contains(vm.YouTubeRows, r => r.Value == "1080p|vp9|webm");
        Assert.Contains(vm.YouTubeRows, r => r.Value == "opus|webm|en");
        Assert.Contains(vm.YouTubeRows, r => r.Value == "Uploader");
    }

    [Fact]
    public void FormatHelpers_ProduceHumanReadableValues()
    {
        Assert.Equal("1:02:03", MediaPropertiesDialogViewModel.FormatDuration(new TimeSpan(1, 2, 3)));
        Assert.Equal("0:59", MediaPropertiesDialogViewModel.FormatDuration(TimeSpan.FromSeconds(59)));
        Assert.Equal("4.2 Mbit/s", MediaPropertiesDialogViewModel.FormatBitRate(4_200_000));
        Assert.Equal("192 kbit/s", MediaPropertiesDialogViewModel.FormatBitRate(192_000));
        Assert.Equal("512 bit/s", MediaPropertiesDialogViewModel.FormatBitRate(512));
        Assert.Equal("1.5 GiB", MediaPropertiesDialogViewModel.FormatSize((long)(1.5 * 1024 * 1024 * 1024)));
        Assert.Equal("110 MiB", MediaPropertiesDialogViewModel.FormatSize(110L * 1024 * 1024));
        Assert.Equal("640 B", MediaPropertiesDialogViewModel.FormatSize(640));
    }
}
