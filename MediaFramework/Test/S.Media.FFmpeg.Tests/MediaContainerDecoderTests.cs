using System.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public class MediaContainerDecoderTests
{
    public MediaContainerDecoderTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void Open_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => MediaContainerDecoder.Open("/nonexistent/missing-media.bin"));
    }

    [Fact]
    public void Open_Default_SharedDemux_ReadsAudioAndVideo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_shared_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path);
            Assert.True(c.UsesSharedDemux);
            Assert.Null(c.LegacyAudio);
            Assert.Null(c.LegacyVideo);

            var scratch = new float[c.Audio.Format.Channels * 512];
            var n = c.Audio.ReadInto(scratch);
            Assert.True(n > 0);

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            vf.Dispose();
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void Open_WithDecoderOptions_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_opts_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions
            {
                TryHardwareAcceleration = true,
                DecoderThreadCount = 2,
            });
            Assert.True(c.UsesSharedDemux);
            var scratch = new float[c.Audio.Format.Channels * 256];
            Assert.True(c.Audio.ReadInto(scratch) > 0);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void Open_SoftwareOnlyVideoOptions_StillSharedDemux()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_sw_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            Assert.True(c.UsesSharedDemux);
            Assert.True(c.Video.TryReadNextFrame(out var vf));
            vf.Dispose();
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignored */ }
        }
    }

    private static bool TryGenerateAudioVideo(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                    "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
                    "-shortest",
                    "-c:a", "aac",
                    "-c:v", "libx264",
                    "-pix_fmt", "yuv420p",
                    "-loglevel", "error",
                    path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(20000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
