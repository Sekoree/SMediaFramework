using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class MediaPlayerControllerTests
{
    [Fact]
    public void Controller_TracksTransportStateAndSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_controller_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            using var graph = MediaGraphBuilder.File(path).Build();
            using var controller = new MediaPlayerController(graph);

            Assert.Equal(MediaPlayerControllerState.Ready, controller.State);
            var ready = controller.GetSnapshot();
            Assert.Equal(MediaPlayerControllerState.Ready, ready.State);
            Assert.Equal(MediaGraphTopology.FilePlayback, ready.Topology);
            Assert.NotNull(ready.Health.Video);
            Assert.NotNull(ready.Health.AudioRouter);

            controller.Play();
            Assert.Equal(MediaPlayerControllerState.Playing, controller.State);

            controller.Pause();
            Assert.Equal(MediaPlayerControllerState.Paused, controller.State);

            controller.Stop();
            Assert.Equal(MediaPlayerControllerState.Stopped, controller.State);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Controller_CloseDisposesGraphAndRejectsTransport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_controller_close_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            var graph = MediaGraphBuilder.File(path).Build();
            using var controller = new MediaPlayerController(graph);

            controller.Close();

            Assert.Equal(MediaPlayerControllerState.Disposed, controller.State);
            Assert.True(graph.Player.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => controller.Play());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Controller_StoresAdvancedPlaybackPlans()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_controller_plans_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            using var controller = ProductApiSamples.CreateVlcStyleFileController(path);
            controller.SelectTracks(new MediaTrackSelection(AudioTrack: 1, VideoTrack: 0, SubtitleTrack: 2));
            controller.SetSubtitlePlan(new SubtitlePlan(Enabled: true, Language: "en"));
            controller.SetPlaybackRate(new PlaybackRatePlan(1.25, AudioTimeStretch: true));
            controller.SetAbRepeat(new AbRepeatPlan(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
            controller.SetPlaylist([new PlaylistItem("one", path, Gapless: true)]);
            controller.RequestFrameSnapshot(new FrameSnapshotRequest(TimeSpan.FromMilliseconds(500), "frame.png"));
            controller.RequestScrubExtraction(new ScrubExtractionRequest(TimeSpan.FromMilliseconds(250), IncludeWaveform: true));
            controller.ReportDeviceHotplug(new DeviceHotplugEvent("speaker", "audio", Connected: true, DateTimeOffset.UtcNow));

            var snapshot = controller.GetSnapshot();
            Assert.Equal(1, snapshot.TrackSelection.AudioTrack);
            Assert.Equal("en", snapshot.SubtitlePlan!.Language);
            Assert.Equal(1.25, snapshot.PlaybackRate.Rate);
            Assert.True(snapshot.AbRepeat!.Enabled);
            Assert.Single(snapshot.Playlist);
            Assert.Single(snapshot.DeviceEvents);
            Assert.Equal("frame.png", controller.LastFrameSnapshotRequest!.OutputPath);
            Assert.True(controller.LastScrubExtractionRequest!.IncludeWaveform);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static byte[] CreateWavBytes()
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int samples = 4800;
        var dataBytes = samples * channels * bitsPerSample / 8;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bitsPerSample / 8);
        bw.Write((short)(channels * bitsPerSample / 8));
        bw.Write(bitsPerSample);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        for (var i = 0; i < samples; i++)
            bw.Write((short)0);
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
