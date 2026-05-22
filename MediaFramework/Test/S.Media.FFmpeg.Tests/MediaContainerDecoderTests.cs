using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
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
            Assert.InRange(c.Duration.TotalSeconds, 0.8, 1.2);

            var scratch = new float[c.Audio.Format.Channels * 512];
            var n = c.Audio.ReadInto(scratch);
            Assert.True(n > 0);

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            vf.Dispose();
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(MediaContainerDecoderTests)}: temp media delete");
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
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(MediaContainerDecoderTests)}: temp media delete");
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
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(MediaContainerDecoderTests)}: temp media delete");
        }
    }

    [Fact]
    public void FlushCodecPipelines_AfterReads_ContinuesReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_flush_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            c.SeekPresentation(TimeSpan.Zero);
            Assert.True(c.Video.TryReadNextFrame(out var v1));
            var t1 = v1.PresentationTime;
            v1.Dispose();

            c.FlushCodecPipelines();

            Assert.True(c.Video.TryReadNextFrame(out var v2));
            try
            {
                Assert.True(v2.PresentationTime >= t1);
            }
            finally
            {
                v2.Dispose();
            }

            var scratch = new float[c.Audio.Format.Channels * 512];
            Assert.True(c.Audio.ReadInto(scratch) > 0);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(MediaContainerDecoderTests)}: temp media delete");
        }
    }

    [Fact]
    public void Open_VideoOnly_Duration_UsesVideoOrContainerDuration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_video_only_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateVideoOnly(path)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

            Assert.False(c.HasAudio);
            Assert.True(c.HasVideo);
            Assert.InRange(c.Duration.TotalSeconds, 0.8, 1.2);

            var seekableVideo = Assert.IsAssignableFrom<ISeekableSource>(c.Video);
            Assert.Equal(c.Duration, seekableVideo.Duration);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(MediaContainerDecoderTests)}: temp video-only delete");
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
                    "-g", "1",
                    "-keyint_min", "1",
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

    private static bool TryGenerateVideoOnly(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
                    "-c:v", "libx264",
                    "-g", "1",
                    "-keyint_min", "1",
                    "-pix_fmt", "yuv420p",
                    "-an",
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
