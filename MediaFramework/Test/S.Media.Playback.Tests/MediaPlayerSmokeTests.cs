using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.SkiaSharp;
using Xunit;

namespace S.Media.Playback.Tests;

/// <summary>Phase 13 public-surface smoke tests for <see cref="MediaPlayer"/>.</summary>
public sealed class MediaPlayerSmokeTests
{
    public MediaPlayerSmokeTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void OpenAvFile_play_two_seconds_seek_play_two_seconds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_av_{Guid.NewGuid():N}.mp4");
        if (!MediaPlayerSmokeTestHelpers.TryGenerateAudioVideo(path, durationSec: 6))
            return;

        try
        {
            Assert.True(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err), err);
            using var player = p!;
            player.Play();
            Thread.Sleep(2000);
            player.Seek(TimeSpan.FromSeconds(1));
            Thread.Sleep(2000);
            var m = player.GetMetrics();
            Assert.NotNull(m.Video);
            Assert.True(m.Video.DisplayedCount > 0);
            Assert.True(m.Clock.CurrentPosition >= TimeSpan.FromSeconds(0.5));
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public void OpenAudioOnlyFile_play_until_source_exhausted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_audio_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, MediaPlayerSmokeTestHelpers.CreateWavBytes(durationSeconds: 1.2));
        try
        {
            Assert.True(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err), err);
            using var player = p!;
            player.Play();
            Assert.True(
                MediaPlayerSmokeTestHelpers.WaitUntil(
                    () => player.Decoder.Audio.IsExhausted,
                    TimeSpan.FromSeconds(8)),
                "audio source did not exhaust");
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public void OpenImage_hold_last_frame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_img_{Guid.NewGuid():N}.png");
        MediaPlayerSmokeTestHelpers.WriteMinimalPng(path);
        MediaFrameworkRuntime.Init().UseFFmpeg().UseSkiaSharpImages();
        try
        {
            var img = VideoSource.OpenImage(path);
            Assert.True(
                MediaPlayer.OpenLive(audioSource: null, img)
                    .WithOptions(o => o with { IncludeAudioRouter = false })
                    .WithDisposeSourcesOnPlayerDispose(true)
                    .TryBuild(out var p, out var err),
                err);
            using var player = p!;
            player.Video.HoldLastFrameAtEnd = true;
            player.Play();
            Thread.Sleep(800);
            var m = player.GetMetrics();
            Assert.True(
                (m.Video?.DisplayedCount ?? 0) > 0 || player.Video.HeldFrameSubmitCount > 0,
                "expected at least one displayed or held frame");
        }
        finally
        {
            MediaFrameworkRuntime.Shutdown();
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public void OpenUri_file_scheme_opens_player()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_uri_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, MediaPlayerSmokeTestHelpers.CreateWavBytes());
        try
        {
            Assert.True(MediaPlayer.OpenUri(new Uri(path)).TryBuild(out var p, out var err), err);
            using var player = p!;
            Assert.True(player.Decoder.HasAudio);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public void OpenStream_memory_wav_no_temp_spool()
    {
        var before = MediaPlayerSmokeTestHelpers.CountTempSpoolFiles();
        using var stream = new MemoryStream(MediaPlayerSmokeTestHelpers.CreateWavBytes());
        var options = MediaPlayerOpenOptions.Default with { StreamIsSeekable = true, SpoolStreamToDisk = false };
        Assert.True(
            MediaPlayer.OpenStream(stream)
                .WithInputName("clip.wav")
                .WithOptions(options)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;
        player.Play();
        Thread.Sleep(120);
        var after = MediaPlayerSmokeTestHelpers.CountTempSpoolFiles();
        Assert.Equal(before, after);
    }

    [Fact]
    public void OpenLive_mock_sources_playback_advances()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        using var video = new SolidVideoSource(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(10, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audio, video)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);
        using var player = p!;
        player.Play();
        Thread.Sleep(200);
        Assert.True(player.AudioRouter!.ChunksProduced > 0);
        Assert.True(player.GetMetrics().Video?.DisplayedCount > 0);
    }

    [Fact]
    public void Dispose_DisposesRegisteredOwnedCompanion()
    {
        // P2-18: WithPortAudio (and any builder companion) hands its host to the player so disposing
        // the player alone tears it down — the simple "open with audio" path can't leak the host.
        // Verifies the ownership hook independent of PortAudio hardware.
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        using var video = new SolidVideoSource(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(10, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audio, video)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);

        var companion = new DisposeFlag();
        p!.RegisterOwnedCompanion(companion);
        Assert.False(companion.Disposed);

        p.Dispose();
        Assert.True(companion.Disposed, "player must dispose its registered owned companion");
    }

    private sealed class DisposeFlag : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void MidPlay_audio_output_swap_routes_to_second_output()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_swap_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, MediaPlayerSmokeTestHelpers.CreateWavBytes(durationSeconds: 1.0));
        try
        {
            Assert.True(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err), err);
            using var player = p!;
            var router = player.AudioRouter!;
            var fmt = player.Decoder.Audio.Format;
            var out1Id = router.AddOutput(new PlainOutput(new AudioFormat(fmt.SampleRate, fmt.Channels)));
            router.Route(player.AudioSourceId!, out1Id);
            player.Play();
            Thread.Sleep(100);
            var out2Id = router.AddOutput(new PlainOutput(new AudioFormat(fmt.SampleRate, fmt.Channels)));
            router.Route(player.AudioSourceId!, out2Id);
            Assert.True(router.RemoveRoute(player.AudioSourceId!, out1Id));
            Thread.Sleep(200);
            Assert.True(router.ChunksProduced > 0);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public void GetMetrics_advances_during_playback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_smoke_metrics_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, MediaPlayerSmokeTestHelpers.CreateWavBytes(durationSeconds: 0.5));
        try
        {
            Assert.True(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err), err);
            using var player = p!;
            var before = player.GetMetrics();
            player.Play();
            Thread.Sleep(250);
            var after = player.GetMetrics();
            Assert.True(
                after.AudioRouter!.ChunksProduced > before.AudioRouter!.ChunksProduced
                || after.Clock.CurrentPosition > before.Clock.CurrentPosition);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    private sealed class PlainOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> samples) { }
    }

    private sealed class SilenceSource(AudioFormat format) : IAudioSource, IDisposable
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;
        public bool Disposed { get; private set; }

        public int ReadInto(Span<float> destination)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            destination.Clear();
            return destination.Length;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SolidVideoSource : IVideoSource, IDisposable
    {
        private readonly VideoFormat _format;
        private int _reads;
        private readonly byte[] _plane;

        public SolidVideoSource(VideoFormat format)
        {
            _format = format;
            _plane = new byte[format.Width * 4 * format.Height];
        }

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats => [_format.PixelFormat];
        public bool IsExhausted => _reads >= 30;
        public bool Disposed { get; private set; }

        public void SelectOutputFormat(PixelFormat format) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (IsExhausted)
            {
                frame = null!;
                return false;
            }

            _reads++;
            var pts = TimeSpan.FromMilliseconds(_reads * (1000.0 * _format.FrameRate.Denominator / _format.FrameRate.Numerator));
            frame = new VideoFrame(pts, _format, _plane, _format.Width * 4);
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
