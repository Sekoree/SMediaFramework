using S.Media.FFmpeg;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class MediaContainerDecoderOpenInputTests
{
    public MediaContainerDecoderOpenInputTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void OpenUri_FileUri_ReadsAudio()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_uri_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            using var decoder = MediaContainerDecoder.OpenUri(new Uri(path));

            Assert.True(decoder.HasAudio);
            var buffer = new float[decoder.Audio.Format.Channels * 128];
            Assert.True(decoder.Audio.ReadInto(buffer) > 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void OpenUri_RelativeUri_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            MediaContainerDecoder.OpenUri(new Uri("relative.wav", UriKind.Relative)));
    }

    [Fact]
    public void OpenStream_FiniteStream_ReadsAudio()
    {
        using var stream = new MemoryStream(CreateWavBytes());
        using var decoder = MediaContainerDecoder.OpenStream(stream, "clip.wav");

        Assert.True(decoder.HasAudio);
        var buffer = new float[decoder.Audio.Format.Channels * 128];
        Assert.True(decoder.Audio.ReadInto(buffer) > 0);
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
}
