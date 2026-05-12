using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Long-run / seek stress for <see cref="MediaContainerDecoder"/> (single <c>AVFormatContext</c>,
/// threaded demux). Skips cleanly when <c>ffmpeg</c> is unavailable (same pattern as
/// <see cref="MediaContainerDecoderTests"/>). Uses single-threaded A/V pulls only (no concurrent
/// decode from two threads — avoids demux/queue edge cases in CI).
/// Set environment variable <c>RUN_MEDIA_SOAK=1</c> to multiply seek/drain rounds for a longer
/// local stress pass (still bounded; not a multi-hour soak).
/// </summary>
public sealed class MediaContainerDecoderSoakTests : IDisposable
{
    private const int AudioHz = 48_000;
    private const int VideoWidth = 320;
    private const int VideoHeight = 240;
    private const int VideoFps = 24;
    private const int DurationSeconds = 5;

    /// <summary>Heavier pass for manual / nightly machines (more seek rounds).</summary>
    private static bool LongSoak =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_MEDIA_SOAK"), "1", StringComparison.Ordinal);

    private static int SoakRounds => LongSoak ? 64 : 8;

    private static readonly VideoDecoderOpenOptions SoftwareOnly = new() { TryHardwareAcceleration = false };

    private readonly string? _mediaPath;

    public MediaContainerDecoderSoakTests()
    {
        FFmpegRuntime.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), $"mc_soak_{Guid.NewGuid():N}.mp4");
        if (TryGenerateSoakMedia(path))
            _mediaPath = path;
        else
            _mediaPath = null;
    }

    public void Dispose()
    {
        if (_mediaPath is not null)
        {
            try { File.Delete(_mediaPath); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void SharedDemux_Soak_RandomSeeksInterleavedDrainAndSequentialPlayThrough_NoThrow()
    {
        if (_mediaPath is null)
            return;

        var duration = ProbeDurationViaOpen(_mediaPath);
        Assert.True(duration > TimeSpan.FromSeconds(2), $"expected multi-second synthetic clip, got {duration}");

        var rnd = new Random(2_718_281);

        // --- sequential: seeks + interleaved / full drains (single-threaded pulls) ---
        for (var round = 0; round < SoakRounds; round++)
        {
            using var c = MediaContainerDecoder.Open(_mediaPath, SoftwareOnly);
            var audio = c.Audio;
            var video = c.Video;

            var seekTo = round == 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(rnd.NextDouble() * Math.Max(1, duration.TotalMilliseconds * 0.92));

            c.SeekPresentation(seekTo);

            if (round % 3 == 0)
                DrainInterleaved(audio, video, rnd);
            else
            {
                DrainAudioToEof(audio);
                DrainVideoToEof(video);
            }

            Assert.True(audio.IsExhausted, $"round {round}: audio not exhausted after drain");
            Assert.True(video.IsExhausted, $"round {round}: video not exhausted after drain");
        }
    }

    private static TimeSpan ProbeDurationViaOpen(string path)
    {
        using var c = MediaContainerDecoder.Open(path, SoftwareOnly);
        return ((ISeekableSource)c.Audio).Duration;
    }

    private static void DrainAudioToEof(IAudioSource audio)
    {
        var scratch = new float[8192];
        while (!audio.IsExhausted)
            audio.ReadInto(scratch);
    }

    private static void DrainVideoToEof(IVideoSource video)
    {
        while (video.TryReadNextFrame(out var f))
            f.Dispose();
    }

    private static void DrainInterleaved(IAudioSource audio, IVideoSource video, Random rnd)
    {
        var scratch = new float[16_384];
        // Bounded partial pulls then full drain — exercises interleaved access without
        // tight RNG loops that can starve one stream.
        for (var i = 0; i < 6 && !audio.IsExhausted; i++)
        {
            var need = rnd.Next(audio.Format.Channels, Math.Min(8000, scratch.Length));
            need -= need % audio.Format.Channels;
            if (need > 0)
                audio.ReadInto(scratch.AsSpan(0, need));
        }

        for (var n = 0; n < 12 && video.TryReadNextFrame(out var f); n++)
            f.Dispose();

        DrainAudioToEof(audio);
        DrainVideoToEof(video);
    }

    private static bool TryGenerateSoakMedia(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi", "-i", $"sine=frequency=660:sample_rate={AudioHz}:duration={DurationSeconds}",
                    "-f", "lavfi", "-i", $"testsrc=size={VideoWidth}x{VideoHeight}:rate={VideoFps}:duration={DurationSeconds}",
                    "-shortest",
                    "-c:a", "aac",
                    "-b:a", "128k",
                    "-c:v", "libx264",
                    "-pix_fmt", "yuv420p",
                    "-g", "48",
                    "-keyint_min", "48",
                    "-sc_threshold", "0",
                    "-loglevel", "error",
                    path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(90_000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 10_000;
        }
        catch
        {
            return false;
        }
    }
}
