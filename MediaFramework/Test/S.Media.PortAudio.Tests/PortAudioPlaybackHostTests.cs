using System.Diagnostics;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.PortAudio;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioPlaybackHostTests
{
    public PortAudioPlaybackHostTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void TryCreatePortAudioMain_WiresSourceAndSink()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp_host_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            using var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            using var host = PortAudioPlaybackHost.TryCreatePortAudioMain(dec, 480, null, _ => { });
            Assert.NotNull(host);
            Assert.Same(dec, host.Container);
            Assert.False(string.IsNullOrEmpty(host.SourceId));
            Assert.False(string.IsNullOrEmpty(host.PrimarySinkId));
            Assert.Same(host.Player.Clock, host.Player.Timeline);
        }
        finally
        {
            try { File.Delete(path); }
#if DEBUG
            catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(PortAudioPlaybackHostTests)}: temp media delete"); }
#else
            catch { /* ignored */ }
#endif
        }
    }

    [Fact]
    public void TryCreatePortAudioMain_CallerDisposesPlayer_PlayerDisposeThenHostDispose_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp_caller_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            using var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            var host = PortAudioPlaybackHost.TryCreatePortAudioMain(
                dec,
                480,
                null,
                _ => { },
                PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer);
            if (host is null)
                return;
            host.Player.Dispose();
            host.Dispose();
            host.Dispose();
        }
        finally
        {
            try { File.Delete(path); }
#if DEBUG
            catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(PortAudioPlaybackHostTests)}: temp media delete"); }
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
