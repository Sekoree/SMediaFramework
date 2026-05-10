using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

public sealed class AudioFileDecoderTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const double DurationSeconds = 1.0;
    private const double Frequency = 440.0;

    private readonly string _wavPath;

    public AudioFileDecoderTests()
    {
        _wavPath = Path.Combine(Path.GetTempPath(), $"sm_decoder_test_{Guid.NewGuid():N}.wav");
        WriteSineWav(_wavPath, SampleRate, Channels, Frequency, DurationSeconds);
    }

    public void Dispose()
    {
        try { File.Delete(_wavPath); } catch { /* ignored */ }
    }

    [Fact]
    public void Open_ReportsExpectedFormat()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);

        Assert.Equal(new AudioFormat(SampleRate, Channels), decoder.Format);
        Assert.InRange(decoder.Duration.TotalSeconds, 0.95, 1.05);
        Assert.False(string.IsNullOrEmpty(decoder.CodecName));
    }

    [Fact]
    public void Open_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => AudioFileDecoder.Open("/no/such/file.wav"));
    }

    [Fact]
    public void ReadAllFrames_TotalsToInputSampleCount()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);

        long total = 0;
        while (decoder.TryReadNextFrame(out var frame))
        {
            Assert.Equal(decoder.Format, frame.Format);
            Assert.Equal(frame.SamplesPerChannel * Channels, frame.Samples.Length);
            total += frame.SamplesPerChannel;
        }

        Assert.True(decoder.IsAtEnd);
        var expected = (long)(SampleRate * DurationSeconds);
        Assert.InRange(total, expected - SampleRate / 100, expected + SampleRate / 100);
    }

    [Fact]
    public void DecodedFrame_ContainsSignal()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);

        Assert.True(decoder.TryReadNextFrame(out var frame));
        var peak = 0f;
        foreach (var s in frame.Samples.Span)
        {
            var a = MathF.Abs(s);
            if (a > peak) peak = a;
        }

        Assert.True(peak > 0.1f, $"expected non-trivial signal, peak amplitude was {peak}");
    }

    [Fact]
    public void Frames_OwnTheirBuffersAcrossReads()
    {
        // Memory<float> means a frame stays valid after the next TryReadNextFrame —
        // this is the contract that lets sources queue or hand off across threads.
        using var decoder = AudioFileDecoder.Open(_wavPath);

        Assert.True(decoder.TryReadNextFrame(out var first));
        var firstSnapshot = first.Samples.ToArray();
        Assert.True(decoder.TryReadNextFrame(out _));

        Assert.Equal(firstSnapshot, first.Samples.ToArray());
    }

    [Fact]
    public void Position_AdvancesWithFrames()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);

        Assert.True(decoder.TryReadNextFrame(out _));
        var after1 = decoder.Position;
        Assert.True(decoder.TryReadNextFrame(out _));
        var after2 = decoder.Position;

        Assert.True(after1 > TimeSpan.Zero);
        Assert.True(after2 > after1);
    }

    [Fact]
    public void Seek_RewindsAndAllowsReread()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);

        while (decoder.TryReadNextFrame(out _)) { }
        Assert.True(decoder.IsAtEnd);

        decoder.Seek(TimeSpan.Zero);
        Assert.False(decoder.IsAtEnd);
        Assert.True(decoder.TryReadNextFrame(out var frame));
        Assert.True(frame.SamplesPerChannel > 0);
    }

    [Fact]
    public void Seek_NegativeThrows()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Seek(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ReadInto_FillsBufferAndContainsSignal()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);
        var dst = new float[480 * Channels]; // 10 ms @ 48 kHz

        var written = decoder.ReadInto(dst);
        Assert.Equal(dst.Length, written);

        var peak = 0f;
        foreach (var s in dst) peak = MathF.Max(peak, MathF.Abs(s));
        Assert.True(peak > 0.1f, $"expected non-trivial signal, peak {peak}");
    }

    [Fact]
    public void ReadInto_AccumulatesToFullDuration()
    {
        // Pulling the file out via the IAudioSource path should yield (within
        // codec rounding) the same total sample count as the WAV input.
        using var decoder = AudioFileDecoder.Open(_wavPath);
        var dst = new float[1024 * Channels];
        long total = 0;
        while (true)
        {
            var written = decoder.ReadInto(dst);
            total += written / Channels;
            if (written < dst.Length && decoder.IsExhausted) break;
            if (written == 0 && decoder.IsExhausted) break;
        }
        var expected = (long)(SampleRate * DurationSeconds);
        Assert.InRange(total, expected - SampleRate / 100, expected + SampleRate / 100);
        Assert.True(decoder.IsExhausted);
    }

    [Fact]
    public void ReadInto_AfterSeek_ResumesFromNewPosition()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);
        var dst = new float[480 * Channels];

        // Drain everything via ReadInto.
        while (decoder.ReadInto(dst) == dst.Length) { }
        Assert.True(decoder.IsExhausted);

        decoder.Seek(TimeSpan.Zero);
        Assert.False(decoder.IsExhausted);

        var written = decoder.ReadInto(dst);
        Assert.Equal(dst.Length, written);
    }

    [Fact]
    public void ReadInto_ZeroLengthBuffer_ReturnsZero()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);
        var written = decoder.ReadInto(Array.Empty<float>());
        Assert.Equal(0, written);
    }

    [Fact]
    public void ReadInto_LengthNotMultipleOfChannels_Throws()
    {
        using var decoder = AudioFileDecoder.Open(_wavPath);
        Assert.Throws<ArgumentException>(() => decoder.ReadInto(new float[Channels + 1]));
    }

    [Fact]
    public void Dispose_IsIdempotentAndBlocksFurtherReads()
    {
        var decoder = AudioFileDecoder.Open(_wavPath);
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.TryReadNextFrame(out _));
    }

    private static void WriteSineWav(string path, int sampleRate, int channels, double frequency, double durationSeconds)
    {
        var sampleCount = (int)(sampleRate * durationSeconds);
        var dataSize = sampleCount * channels * sizeof(short);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);

        bw.Write("fmt "u8);
        bw.Write(16);                              // fmt chunk size
        bw.Write((short)1);                        // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * sizeof(short)); // byte rate
        bw.Write((short)(channels * sizeof(short)));     // block align
        bw.Write((short)16);                       // bits per sample

        bw.Write("data"u8);
        bw.Write(dataSize);

        var twoPiF = 2.0 * Math.PI * frequency;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(twoPiF * i / sampleRate) * 0.5 * short.MaxValue);
            for (var c = 0; c < channels; c++) bw.Write(sample);
        }
    }
}
