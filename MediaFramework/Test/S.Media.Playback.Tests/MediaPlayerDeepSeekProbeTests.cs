using System.Diagnostics;
using S.Media.Core.Playback;
using S.Media.FFmpeg;
using S.Media.Playback;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Playback.Tests;

public sealed class MediaPlayerDeepSeekProbeTests
{
    private const string ProbeLogPath = "/tmp/mf_player_seek_probe.log";
    private readonly ITestOutputHelper _output;

    public MediaPlayerDeepSeekProbeTests(ITestOutputHelper output)
    {
        _output = output;
        FFmpegRuntime.EnsureInitialized();
    }

    [Fact]
    public void ProvidedMovie_HeadlessPlayerDeepSeekProbe_WhenPresent()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MFPLAYER_RUN_PROVIDED_MOVIE_PROBE"), "1", StringComparison.Ordinal))
            return;

        var path = Environment.GetEnvironmentVariable("MFPLAYER_PROBE_MEDIA")
                   ?? "/home/sekoree/Videos/THE IDOLM@STER MOVIE.mkv";
        if (!File.Exists(path))
            return;

        File.WriteAllText(ProbeLogPath, $"probe start {DateTimeOffset.Now:O}{Environment.NewLine}");
        var target = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(50);
        var options = new MediaPlayerOpenOptions(
            TryHardwareAcceleration: true,
            IncludeAudioRouter: true,
            AudioPacketQueueDepth: 720,
            VideoPacketQueueDepth: 512,
            FileReadBufferBytes: 4 * 1024 * 1024,
            FileVideoDecodeQueueCapacity: 16);

        ProbeLog("open begin");
        Assert.True(
            MediaPlayer.OpenFile(path).WithOptions(options).TryBuild(out var player, out var error),
            error);
        using (player)
        {
            ProbeLog($"open done duration={player!.Duration} video={player.Video.Format}");
            var seek = Stopwatch.StartNew();
            ProbeLog($"seek begin target={target}");
            player.SeekCoordinated(target, CancellationToken.None, PauseFlushPolicy.SkipFlush);
            seek.Stop();
            ProbeLog($"seek done ms={seek.Elapsed.TotalMilliseconds:F1}");

            ProbeLog("play begin");
            player.Play();
            Thread.Sleep(TimeSpan.FromSeconds(8));
            ProbeLog("pause begin");
            player.Pause(CancellationToken.None, PauseFlushPolicy.SkipFlush);
            ProbeLog("pause done");

            var m = player.GetMetrics();
            var line =
                $"clock={m.Clock.CurrentPosition} master={m.Clock.MasterTypeName} decoded={m.Video?.DecodedCount ?? 0} displayed={m.Video?.DisplayedCount ?? 0} dropLate={m.Video?.DroppedLate ?? 0} dropDrain={m.Video?.DroppedDrain ?? 0} audioChunks={m.AudioRouter?.ChunksProduced ?? 0} audioDrop={m.AudioRouter?.TotalDropped ?? 0}";
            _output.WriteLine(line);
            ProbeLog(line);
        }
    }

    private static void ProbeLog(string line) =>
        File.AppendAllText(ProbeLogPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
}
