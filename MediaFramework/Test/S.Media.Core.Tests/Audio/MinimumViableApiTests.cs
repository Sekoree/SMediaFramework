using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class MinimumViableApiTests : IDisposable
{
    private readonly string _wavPath;

    public MinimumViableApiTests()
    {
        _wavPath = Path.Combine(Path.GetTempPath(), $"sm_mvp_{Guid.NewGuid():N}.wav");
        SineWav.Write(_wavPath, sampleRate: 48000, channels: 2, frequencyHz: 440, durationSeconds: 0.25);
        MediaFrameworkRuntime.Init().UseFFmpeg();
    }

    public void Dispose()
    {
        MediaFrameworkRuntime.Shutdown();
        if (File.Exists(_wavPath))
            File.Delete(_wavPath);
    }

    [Fact]
    public void AudioSource_OpenFile_ReturnsReadableSource()
    {
        var clip = AudioSource.OpenFile(_wavPath);
        try
        {
            Assert.Equal(48000, clip.Format.SampleRate);
            var buf = new float[clip.Format.Channels * 480];
            var n = clip.ReadInto(buf);
            Assert.True(n > 0);
        }
        finally
        {
            DisposeIfNeeded(clip);
        }
    }

    [Fact]
    public void MediaContainer_OpenFile_ExposesAudioAndVideoTracks()
    {
        using var media = MediaContainer.OpenFile(_wavPath);
        Assert.True(media.HasAudio);
        var buf = new float[media.Audio.Format.Channels * 480];
        Assert.True(media.Audio.ReadInto(buf) > 0);
    }

    [Fact]
    public void SixLineShape_RouteAndPlay()
    {
        using var router = new AudioRouter(48000);
        var output = new PlainOutput(new AudioFormat(48000, 2));
        var clip = AudioSource.OpenFile(_wavPath);
        try
        {
            router.AddSource(clip, autoResample: true);
            router.AddOutput(output);
            router.RouteLast();
            router.Play();

            Thread.Sleep(150);
            router.Stop();
            Assert.True(router.ChunksProduced > 0);
        }
        finally
        {
            DisposeIfNeeded(clip);
        }
    }

    [Fact]
    public void AudioClip_VoicePlaysThroughRouter()
    {
        var pcm = AudioClip.OpenFile(_wavPath);
        using var router = new AudioRouter(pcm.Format.SampleRate);
        var output = new PlainOutput(pcm.Format);
        var player = new AudioClipPlayer(pcm);

        var outId = router.AddOutput(output);
        player.Fire(router, outId);
        router.Play();
        Thread.Sleep(200);
        router.Stop();
        Assert.True(router.ChunksProduced > 0);
    }

    private static void DisposeIfNeeded(IAudioSource source)
    {
        if (source is IDisposable d)
            d.Dispose();
    }

    private sealed class PlainOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
    }
}

internal static class SineWav
{
    public static void Write(string path, int sampleRate, int channels, double frequencyHz, double durationSeconds)
    {
        var sampleCount = (int)(sampleRate * durationSeconds);
        var dataBytes = sampleCount * channels * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * short.MaxValue * 0.25);
            for (var ch = 0; ch < channels; ch++)
                bw.Write(sample);
        }
    }
}
