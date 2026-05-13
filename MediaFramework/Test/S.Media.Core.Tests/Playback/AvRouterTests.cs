using System.Diagnostics;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.Core.Tests.Video;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.Core.Tests.Playback;

public sealed class AvRouterTests
{
    public AvRouterTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void Ctor_NullContainer_Throws()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var sink = new FakeVideoSink([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, sink, clock);
        var session = new MediaPlaybackSession(video, clock);

        Assert.Throws<ArgumentNullException>(() => new AvRouter(null!, session));
    }

    [Fact]
    public void Ctor_NullSession_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"avrouter_null_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            Assert.Throws<ArgumentNullException>(() => new AvRouter(c, null!));
        }
        finally
        {
            try { File.Delete(path); }
#if DEBUG
            catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(AvRouterTests)}: temp media delete"); }
#else
            catch { /* ignored */ }
#endif
        }
    }

    [Fact]
    public void SeekCoordinated_WhenAudioNull_UpdatesMediaClock()
    {
        var path = Path.Combine(Path.GetTempPath(), $"avrouter_seek_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            using var clock = new MediaClock();
            var sink = new FakeVideoSink(c.Video.NativePixelFormats.ToArray());
            using var video = new VideoPlayer(c.Video, sink, clock);
            var session = new MediaPlaybackSession(video, clock);
            var router = new AvRouter(c, session);

            router.Play();
            sink.WaitForConfigured();
            Thread.Sleep(120);
            router.Pause();

            var target = TimeSpan.FromMilliseconds(250);
            router.SeekCoordinated(target);

            Assert.False(video.IsRunning);
            var deltaMs = Math.Abs((clock.CurrentPosition - target).TotalMilliseconds);
            Assert.True(deltaMs < 2.0, $"clock at {clock.CurrentPosition}, expected ~{target}");
        }
        finally
        {
            try { File.Delete(path); }
#if DEBUG
            catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(AvRouterTests)}: temp media delete"); }
#else
            catch { /* ignored */ }
#endif
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
}
