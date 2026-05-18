using System.Diagnostics;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class MediaContainerPlaybackBundleTests
{
    public MediaContainerPlaybackBundleTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void SmokeToolDefaultOwnership_matches_video_router_and_freerun_presence()
    {
        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(hasVideoRouter: false, hasFreerunMediaClock: false));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.VideoRouter,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(hasVideoRouter: true, hasFreerunMediaClock: false));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(hasVideoRouter: false, hasFreerunMediaClock: true));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.VideoRouter | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(hasVideoRouter: true, hasFreerunMediaClock: true));
    }

    [Fact]
    public void DefaultBundledHostOwnership_matches_SmokeToolDefaultOwnership()
    {
        foreach (var vr in new[] { false, true })
        foreach (var fr in new[] { false, true })
        foreach (var ap in new[] { false, true })
        {
            Assert.Equal(
                MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(vr, fr, ap),
                MediaContainerPlaybackBundle.DefaultBundledHostOwnership(vr, fr, ap));
        }
    }

    [Fact]
    public void SmokeToolDefaultOwnership_includes_AudioPlayer_when_true()
    {
        var audio = MediaContainerPlaybackBundleOwnedParts.AudioPlayer;
        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer | audio,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(false, false, hasAudioPlayer: true));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.VideoRouter | audio,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(true, false, hasAudioPlayer: true));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock | audio,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(false, true, hasAudioPlayer: true));

        Assert.Equal(
            MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
            | MediaContainerPlaybackBundleOwnedParts.VideoRouter | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock | audio,
            MediaContainerPlaybackBundle.SmokeToolDefaultOwnership(true, true, hasAudioPlayer: true));
    }

    [Fact]
    public void Ctor_OwnsAudioPlayerButAudioNull_ThrowsArgumentException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mega_audioflag_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            try
            {
                using var clock = new MediaClock();
                var sink = new DropVideoSink(dec.Video.NativePixelFormats.ToArray());
                using var video = new VideoPlayer(dec.Video, sink, clock);
                var ex = Assert.Throws<ArgumentException>(() => _ = new MediaContainerPlaybackBundle(
                    dec,
                    video,
                    clock,
                    audio: null,
                    videoRouter: null,
                    freerunClockToDispose: null,
                    MediaContainerPlaybackBundleOwnedParts.AudioPlayer));
                Assert.Contains("audio", ex.ParamName ?? "", StringComparison.Ordinal);
            }
            finally
            {
                dec.Dispose();
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Ctor_OwnsVideoRouterButRouterNull_ThrowsArgumentException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mega_routerflag_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            try
            {
                using var clock = new MediaClock();
                var sink = new DropVideoSink(dec.Video.NativePixelFormats.ToArray());
                using var video = new VideoPlayer(dec.Video, sink, clock);
                var ex = Assert.Throws<ArgumentException>(() => _ = new MediaContainerPlaybackBundle(
                    dec,
                    video,
                    clock,
                    audio: null,
                    videoRouter: null,
                    freerunClockToDispose: null,
                    MediaContainerPlaybackBundleOwnedParts.VideoRouter));
                Assert.Contains("videoRouter", ex.ParamName ?? "", StringComparison.Ordinal);
            }
            finally
            {
                dec.Dispose();
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Ctor_OwnsFreerunClockButDisposalTargetNull_ThrowsArgumentException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mega_clockflag_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            try
            {
                using var clock = new MediaClock();
                var sink = new DropVideoSink(dec.Video.NativePixelFormats.ToArray());
                using var video = new VideoPlayer(dec.Video, sink, clock);
                var ex = Assert.Throws<ArgumentException>(() => _ = new MediaContainerPlaybackBundle(
                    dec,
                    video,
                    clock,
                    audio: null,
                    videoRouter: null,
                    freerunClockToDispose: null,
                    MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock));
                Assert.Contains("freerunClockToDispose", ex.ParamName ?? "", StringComparison.Ordinal);
            }
            finally
            {
                dec.Dispose();
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Dispose_OwnsDecoderVideoClock_CanCallTwice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mega_dispose_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            var clock = new MediaClock();
            var sink = new DropVideoSink(dec.Video.NativePixelFormats.ToArray());
            var video = new VideoPlayer(dec.Video, sink, clock);
            var mega = new MediaContainerPlaybackBundle(
                dec,
                video,
                clock,
                audio: null,
                videoRouter: null,
                freerunClockToDispose: clock,
                MediaContainerPlaybackBundleOwnedParts.Decoder
                    | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
                    | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock);

            mega.Dispose();
            mega.Dispose();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Dispose_WithVideoRouter_DisposesWithoutThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mega_vr_{Guid.NewGuid():N}.mp4");
        if (!TryGenerateAudioVideo(path))
            return;
        try
        {
            var dec = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            var clock = new MediaClock();
            var sink = new DropVideoSink(dec.Video.NativePixelFormats.ToArray());
            var router = new VideoRouter(null);
            var outId = router.AddOutput(sink, "o", disposeSinkOnRouterDispose: true);
            var vin = router.AddInput(outId);
            var video = new VideoPlayer(dec.Video, vin.Sink, clock);
            using var mega = new MediaContainerPlaybackBundle(
                dec,
                video,
                clock,
                audio: null,
                videoRouter: router,
                freerunClockToDispose: clock,
                MediaContainerPlaybackBundleOwnedParts.Decoder
                    | MediaContainerPlaybackBundleOwnedParts.VideoPlayer
                    | MediaContainerPlaybackBundleOwnedParts.VideoRouter
                    | MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock);

            mega.Dispose();
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"{nameof(MediaContainerPlaybackBundleTests)}: temp media delete");
        }
#else
        catch { /* ignored */ }
#endif
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

    private sealed class DropVideoSink : IVideoSink
    {
        private readonly PixelFormat[] _accepted;

        public DropVideoSink(PixelFormat[] accepted) => _accepted = accepted;

        public VideoFormat Format { get; private set; }

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame f) => f.Dispose();
    }
}
