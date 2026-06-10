using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using Xunit;

namespace S.Media.Playback.Tests;

/// <summary>A1 (HotPath review 2026-06-10): one-call output wiring on <see cref="MediaPlayer"/>.</summary>
public sealed class MediaPlayerAttachOutputTests
{
    public MediaPlayerAttachOutputTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void AttachAudioOutput_RegistersOutputAndRoute()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        Assert.True(
            MediaPlayer.OpenLive(audio, videoSource: null)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;

        var outId = player.AttachAudioOutput(new DiscardingAudioOutput(new AudioFormat(48_000, 2)), "att_a");

        Assert.Equal("att_a", outId);
        var router = player.AudioRouter!;
        Assert.Contains(outId, router.OutputIds);
        Assert.Contains(router.Routes, r => r.SourceId == player.AudioSourceId && r.OutputId == outId);
    }

    [Fact]
    public void AttachAudioOutput_WithoutAudio_Throws()
    {
        using var video = new SolidVideoSource(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audioSource: null, video)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;

        Assert.Throws<InvalidOperationException>(() =>
            player.AttachAudioOutput(new DiscardingAudioOutput(new AudioFormat(48_000, 2))));
    }

    [Fact]
    public void AttachAudioOutput_RouteFailure_RollsBackOutputRegistration()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        Assert.True(
            MediaPlayer.OpenLive(audio, videoSource: null)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;

        // A map demanding more input channels than the source has makes AddRoute throw after the
        // output was registered — the helper must remove the output again.
        var sixIn = new int[2];
        sixIn[0] = 5;
        sixIn[1] = 5;
        Assert.Throws<InvalidOperationException>(() =>
            player.AttachAudioOutput(
                new DiscardingAudioOutput(new AudioFormat(48_000, 2)), "att_bad", new ChannelMap(sixIn)));
        Assert.DoesNotContain("att_bad", player.AudioRouter!.OutputIds);
    }

    [Fact]
    public void AttachVideoOutput_PermissiveOutput_AttachesAsBranch()
    {
        using var video = new SolidVideoSource(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audioSource: null, video)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;

        // R2: a permissive (empty AcceptedPixelFormats) output is valid as a fan-out branch.
        var outId = player.AttachVideoOutput(new DiscardingVideoOutput(), "att_v", synchronous: true);

        Assert.Equal("att_v", outId);
        Assert.Contains("att_v", player.VideoRouter.GetRegisteredOutputIds());
        Assert.True(player.VideoRouter.RemoveOutput(outId));
    }

    /// <summary>R3 (HotPath review 2026-06-10): live attach/detach churn on a playing graph must
    /// not fault the routers or stop the transport (the framework's live re-routing promise).</summary>
    [Fact]
    public void AttachDetachChurn_OnPlayingLiveGraph_StaysHealthy()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        using var video = new SolidVideoSource(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audio, video)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;

        Exception? fault = null;
        player.AudioRouter!.Faulted += (_, e) => fault = e.Exception;
        player.Play();
        try
        {
            for (var i = 0; i < 25; i++)
            {
                var aId = player.AttachAudioOutput(
                    DiscardingAudioOutput.ForRouter(player.AudioRouter), $"churn_a{i}");
                var vId = player.AttachVideoOutput(new DiscardingVideoOutput(), $"churn_v{i}", synchronous: true);
                Assert.True(player.AudioRouter.RemoveOutput(aId));
                Assert.True(player.VideoRouter.TryRemoveRoute(player.VideoRouterInputId, vId, out _));
                Assert.True(player.VideoRouter.RemoveOutput(vId));
            }

            Assert.Null(fault);
            Assert.True(player.AudioRouter.IsRunning);
        }
        finally
        {
            player.Pause();
        }
    }

    private sealed class SilenceSource(AudioFormat format) : IAudioSource, IDisposable
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;
        public bool Disposed { get; private set; }
        public int ReadInto(Span<float> dst)
        {
            dst.Clear();
            return dst.Length;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SolidVideoSource(VideoFormat fmt) : IVideoSource, IDisposable
    {
        public VideoFormat Format { get; } = fmt;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public void SelectOutputFormat(PixelFormat format) { }
        public void Dispose() { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(TimeSpan.Zero, Format,
                new byte[Format.Width * Format.Height * 4], Format.Width * 4, release: null);
            return true;
        }
    }
}
