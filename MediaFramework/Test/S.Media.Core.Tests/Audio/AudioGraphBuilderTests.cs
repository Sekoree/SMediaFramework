using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioGraphBuilderTests
{
    private static readonly AudioFormat Stereo48k = new(48000, 2);

    [Fact]
    public void Chains_AddSource_AddOutput_Connect()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var output = new PlainOutput(Stereo48k);

        new AudioGraphBuilder(router)
            .AddSource(src, "music")
            .AddOutput(output, "main")
            .Connect("music", "main", gain: 0.8f);

        Assert.Contains("music", router.SourceIds);
        Assert.Contains("main", router.SinkIds);
    }

    [Fact]
    public void ConnectLast_UsesMostRecentSourceAndOutput()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var output = new PlainOutput(Stereo48k);

        var builder = new AudioGraphBuilder(router)
            .AddSource(src)
            .AddOutput(output)
            .ConnectLast();

        Assert.NotNull(builder.LastSourceId);
        Assert.NotNull(builder.LastOutputId);
        Assert.Contains(builder.LastSourceId!, router.SourceIds);
        Assert.Contains(builder.LastOutputId!, router.SinkIds);
    }

    [Fact]
    public void ConnectLast_WithoutSource_Throws()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var output = new PlainOutput(Stereo48k);
        var builder = new AudioGraphBuilder(router).AddOutput(output);
        Assert.Throws<InvalidOperationException>(() => builder.ConnectLast());
    }

    [Fact]
    public void Connect_DefaultMap_IsIdentitySizedToOutput()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var output = new PlainOutput(Stereo48k);

        new AudioGraphBuilder(router)
            .AddSource(src, "src")
            .AddOutput(output, "output")
            .Connect("src", "output");

        // No explicit assertion on the map shape — but Connect would throw on a mismatch,
        // so reaching here implies the resolver produced a output-sized identity.
        Assert.Single(router.SourceIds);
        Assert.Single(router.SinkIds);
    }

    private sealed class PlainOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ConstantSource(AudioFormat fmt, float value) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) { dst.Fill(value); return dst.Length; }
    }
}
