using S.Media.Core.Audio;
using S.Media.FFmpeg;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class MediaPlayerTests
{
    public MediaPlayerTests() => FFmpegRuntime.EnsureInitialized();
    [Fact]
    public async Task OpenFileBuilder_OpenAsync_returns_player()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_open_async_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            using var player = await MediaPlayer.OpenFile(path).OpenAsync();
            Assert.NotNull(player.AudioRouter);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void OpenFileBuilder_WithOptions_mutate_works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_open_mut_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            Assert.True(
                MediaPlayer.OpenFile(path)
                    .WithOptions(o => o with { AudioChunkSamples = 960 })
                    .TryBuild(out var p, out var err),
                err);
            using var player = p!;
            Assert.Equal(960, player.AudioRouter!.ChunkSamples);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void MediaPlayerOpenOptions_Default_uses_constructor_defaults()
    {
        var options = MediaPlayerOpenOptions.Default;

        Assert.True(options.TryHardwareAcceleration);
        Assert.True(options.IncludeAudioRouter);
        Assert.Equal(480, options.AudioChunkSamples);
        Assert.Equal(0, options.AudioPacketQueueDepth);
        Assert.Equal(0, options.VideoPacketQueueDepth);

        var parameterless = new MediaPlayerOpenOptions();
        Assert.Equal(options, parameterless);
    }

    [Fact]
    public void OpenFileBuilder_missing_file_returns_false()
    {
        var path = "/nonexistent/path/that/does/not/exist-" + Guid.NewGuid();
        Assert.False(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err));
        Assert.Null(p);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void OpenFileBuilder_with_options_missing_file_returns_false()
    {
        var path = "/nonexistent/path/that/does/not/exist-" + Guid.NewGuid();
        Assert.False(MediaPlayer.OpenFile(path).WithOptions(MediaPlayerOpenOptions.Default).TryBuild(out var p, out var err));
        Assert.Null(p);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void TryOpenUri_file_uri_returns_player()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_player_uri_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            Assert.True(MediaPlayer.OpenUri(new Uri(path)).TryBuild(out var p, out var err), err);
            using var player = p;
            Assert.NotNull(player);
            Assert.True(player.Decoder.HasAudio);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void TryOpen_wav_play_pause_advances_audio_router()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_player_play_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            Assert.True(MediaPlayer.OpenFile(path).TryBuild(out var p, out var err), err);
            using var player = p!;
            Assert.NotNull(player.AudioRouter);
            player.Play();
            Thread.Sleep(120);
            player.Pause();
            Assert.True(player.AudioRouter!.ChunksProduced > 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void TryOpenStream_finite_stream_returns_player()
    {
        using var stream = new MemoryStream(CreateWavBytes());

        Assert.True(MediaPlayer.OpenStream(stream).WithInputName("clip.wav").TryBuild(out var p, out var err), err);
        using var player = p;
        Assert.NotNull(player);
        Assert.True(player.Decoder.HasAudio);
    }

    [Fact]
    public void TryOpenLive_audio_only_returns_live_player()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));

        Assert.True(
            MediaPlayer.OpenLive(audio, videoSource: null)
                .WithDisposeSourcesOnPlayerDispose(false)
                .TryBuild(out var p, out var err),
            err);

        using var player = p;
        Assert.NotNull(player);
        Assert.True(player.IsLive);
        Assert.False(player.HasContainerDecoder);
        Assert.NotNull(player.AudioRouter);
        Assert.NotNull(player.AudioClock);
        Assert.NotNull(player.AudioSourceId);
        Assert.Throws<InvalidOperationException>(() => { _ = player.Decoder; });
        Assert.Throws<InvalidOperationException>(() => { _ = player.Bundle; });
        Assert.Throws<InvalidOperationException>(() => { _ = player.Session; });
        Assert.False(audio.Disposed);
    }

    [Fact]
    public void TryOpenLive_owned_audio_source_disposes_with_player()
    {
        var audio = new SilenceSource(new AudioFormat(48_000, 2));

        Assert.True(
            MediaPlayer.OpenLive(audio, videoSource: null)
                .WithDisposeSourcesOnPlayerDispose(true)
                .TryBuild(out var p, out var err),
            err);

        p!.Dispose();
        Assert.True(audio.Disposed);
    }

    [Fact]
    public void TryOpenLive_without_sources_returns_false()
    {
        Assert.False(
            MediaPlayer.OpenLive(audioSource: null, videoSource: null).TryBuild(out var p, out var err));

        Assert.Null(p);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TryOpenLive_audio_only_without_audio_router_returns_false()
    {
        using var audio = new SilenceSource(new AudioFormat(48_000, 2));
        var options = MediaPlayerOpenOptions.Default with { IncludeAudioRouter = false };

        Assert.False(
            MediaPlayer.OpenLive(audio, videoSource: null)
                .WithOptions(options)
                .TryBuild(out var p, out var err));

        Assert.Null(p);
        Assert.False(string.IsNullOrWhiteSpace(err));
        Assert.False(audio.Disposed);
    }

    [Fact]
    public void PlaybackHud_FormatLine_matches_expected_tokens_for_fixed_snapshot()
    {
        var snap = new PlaybackHudSnapshot(
            ClockPosition: new TimeSpan(0, 1, 2, 3, 400),
            VideoPts: new TimeSpan(0, 0, 0, 5, 123),
            AudioHeard: new TimeSpan(0, 0, 0, 4, 0),
            AudioDeckDecode: new TimeSpan(0, 0, 0, 4, 500),
            DisplayedCount: 100,
            DecodedCount: 120,
            VFpsEstimate: 29.5,
            NominalFpsLabel: "30Hz",
            DroppedLate: 1,
            DroppedDrain: 2,
            GlDroppedNewer: 3,
            NDIVidDr: 4,
            NDIVidQ: 5,
            PaUnd: 6,
            PaDr: 7,
            PumpDr: 8,
            NDIAuDr: 9,
            NDIMonitorTail: "  ndiRx2 P0V1 tallyΔ1");

        var line = PlaybackHud.FormatLine(snap);

        Assert.Equal(
            "clock 01:02:03.400  vPTS 00:05.123  aHeard 00:04.000  aDec 00:04.500  " +
            "show 100/120  vFps~29.5  nom 30Hz  mux shared  vLate 1  vDrn 2  " +
            "glDr 3  ndiVidDr 4  ndiVidQ 5  paUnd 6  paDr 7  pumpDr 8  ndiAuDr 9  ndiRx2 P0V1 tallyΔ1",
            line);
    }

    [Fact]
    public void PlaybackHud_FormatClock_uses_total_hours_and_fixed_width_fields()
    {
        Assert.Equal("01:02:03.456", PlaybackHud.FormatClock(new TimeSpan(0, 1, 2, 3, 456)));
        Assert.Equal("25:00:00.000", PlaybackHud.FormatClock(new TimeSpan(1, 1, 0, 0, 0)));
    }

    private static byte[] CreateWavBytes()
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double durationSeconds = 0.1;
        const double frequency = 440.0;

        var sampleCount = (int)(sampleRate * durationSeconds);
        var dataBytes = sampleCount * channels * (bitsPerSample / 8);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.25);
            bw.Write(sample);
        }

        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* ignored */ }
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
}
