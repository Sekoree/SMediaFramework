using System.Diagnostics;
using S.Media.Core.Audio;
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
    public void TryCreatePortAudioMain_WiresSourceAndOutput()
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
            Assert.False(string.IsNullOrEmpty(host.PrimaryOutputId));
            Assert.Contains(host.SourceId, host.Router.SourceIds);
            Assert.Contains(host.PrimaryOutputId, host.Router.OutputIds);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(PortAudioPlaybackHostTests)}: temp media delete");
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
            host.Router.Dispose();
            host.Clock.Dispose();
            host.Dispose();
            host.Dispose();
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), $"{nameof(PortAudioPlaybackHostTests)}: temp media delete");
        }
    }

    [Fact]
    public void RollbackPartialWire_ForTryCreate_RemovesGraphDisposesOutputAndRouter()
    {
        var router = new AudioRouter(48_000, chunkSamples: 480);
        var sourceId = router.AddSource(new SilenceSource(new AudioFormat(48_000, 2)));
        var output = new DisposableOutput(new AudioFormat(48_000, 2));
        var outputId = router.AddOutput(output);
        router.Connect(sourceId, outputId);

        PortAudioPlaybackHost.RollbackPartialWireForTests(
            router,
            output,
            sourceId,
            outputId,
            disposeRouter: true);

        Assert.True(output.Disposed);
        Assert.Throws<ObjectDisposedException>(() => router.AddOutput(new DisposableOutput(new AudioFormat(48_000, 2))));
    }

    [Fact]
    public void RollbackPartialWire_ForExistingRouter_RemovesOutputButKeepsRouterAndSource()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var sourceId = router.AddSource(new SilenceSource(new AudioFormat(48_000, 2)));
        var output = new DisposableOutput(new AudioFormat(48_000, 2));
        var outputId = router.AddOutput(output);
        router.Connect(sourceId, outputId);

        PortAudioPlaybackHost.RollbackPartialWireForTests(
            router,
            output,
            sourceId: null,
            sinkMain: outputId,
            disposeRouter: false);

        Assert.True(output.Disposed);
        Assert.Contains(sourceId, router.SourceIds);
        Assert.DoesNotContain(outputId, router.OutputIds);
        var replacement = router.AddOutput(new DisposableOutput(new AudioFormat(48_000, 2)));
        Assert.Contains(replacement, router.OutputIds);
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

    private sealed class SilenceSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst)
        {
            dst.Clear();
            return dst.Length;
        }
    }

    private sealed class DisposableOutput(AudioFormat format) : IAudioOutput, IDisposable
    {
        public AudioFormat Format { get; } = format;
        public bool Disposed { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public void Dispose() => Disposed = true;
    }
}
