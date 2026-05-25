using S.Media.Core.Audio;
using S.Media.FFmpeg;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class MediaContainerDecoderStreamAvioTests : IDisposable
{
    public MediaContainerDecoderStreamAvioTests() => FFmpegRuntime.EnsureInitialized();

    public void Dispose() { }

    [Fact]
    public void OpenStream_MemoryWav_ReadsAudioWithoutTempSpool()
    {
        var before = CountTempSpoolFiles();
        var bytes = MediaContainerDecoderOpenInputTests.CreateWavBytes();
        using var stream = new MemoryStream(bytes);
        using var decoder = MediaContainerDecoder.OpenStream(stream, isSeekable: true, probeHintName: "clip.wav");

        Assert.True(decoder.HasAudio);
        var buffer = new float[decoder.Audio.Format.Channels * 256];
        Assert.True(decoder.Audio.ReadInto(buffer) > 0);

        var after = CountTempSpoolFiles();
        Assert.Equal(before, after);
    }

    [Fact]
    public void OpenStream_ForwardOnly_ReadsToExhaustion()
    {
        var bytes = MediaContainerDecoderOpenInputTests.CreateWavBytes();
        using var inner = new MemoryStream(bytes);
        using var forward = new ForwardOnlyStream(inner);
        using var decoder = MediaContainerDecoder.OpenStream(forward, isSeekable: false, probeHintName: "clip.wav");

        Assert.True(decoder.HasAudio);
        var buffer = new float[decoder.Audio.Format.Channels * 4096];
        var total = 0;
        int read;
        while ((read = decoder.Audio.ReadInto(buffer)) > 0)
            total += read;
        Assert.True(total > 0);
        Assert.True(decoder.Audio.IsExhausted);
    }

    [Fact]
    public void OpenStream_NonSeekable_SeekPresentation_Throws()
    {
        var bytes = MediaContainerDecoderOpenInputTests.CreateWavBytes();
        using var inner = new MemoryStream(bytes);
        using var forward = new ForwardOnlyStream(inner);
        using var decoder = MediaContainerDecoder.OpenStream(forward, isSeekable: false, probeHintName: "clip.wav");

        Assert.Throws<NotSupportedException>(() => decoder.SeekPresentation(TimeSpan.Zero));
    }

    [Fact]
    public void OpenStreamSpooled_StillCreatesTempFile()
    {
        var before = CountTempSpoolFiles();
        var bytes = MediaContainerDecoderOpenInputTests.CreateWavBytes();
        using var stream = new MemoryStream(bytes);
        using var decoder = MediaContainerDecoder.OpenStreamSpooled(stream, "clip.wav");
        Assert.True(decoder.HasAudio);
        var after = CountTempSpoolFiles();
        Assert.True(after >= before);
    }

    [Fact]
    public void OpenStream_RouterPlayback_Exhausts()
    {
        var bytes = MediaContainerDecoderOpenInputTests.CreateWavBytes();
        using var stream = new MemoryStream(bytes);
        using var decoder = MediaContainerDecoder.OpenStream(stream, probeHintName: "clip.wav");
        using var router = new AudioRouter(decoder.Audio.Format.SampleRate);
        var output = new NullAudioOutput(decoder.Audio.Format);
        router.AddSource(decoder.Audio, autoResample: true);
        router.AddOutput(output);
        router.RouteLast();
        router.Play();
        Thread.Sleep(250);
        router.Stop();
        Assert.True(router.ChunksProduced > 0);
    }

    private static int CountTempSpoolFiles()
    {
        try
        {
            return Directory.GetFiles(Path.GetTempPath(), "mf_stream_*").Length;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NullAudioOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format => format;
        public void Submit(ReadOnlySpan<float> samples) { }
    }
}
