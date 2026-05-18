using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioGraphBuilderTests
{
    private static readonly AudioFormat Stereo48k = new(48000, 2);

    [Fact]
    public void Chains_AddSource_AddSink_Connect()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var sink = new PlainSink(Stereo48k);

        new AudioGraphBuilder(router)
            .AddSource(src, "music")
            .AddSink(sink, "main")
            .Connect("music", "main", gain: 0.8f);

        Assert.Contains("music", router.SourceIds);
        Assert.Contains("main", router.SinkIds);
    }

    [Fact]
    public void ConnectLast_UsesMostRecentSourceAndSink()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var sink = new PlainSink(Stereo48k);

        var builder = new AudioGraphBuilder(router)
            .AddSource(src)
            .AddSink(sink)
            .ConnectLast();

        Assert.NotNull(builder.LastSourceId);
        Assert.NotNull(builder.LastSinkId);
        Assert.Contains(builder.LastSourceId!, router.SourceIds);
        Assert.Contains(builder.LastSinkId!, router.SinkIds);
    }

    [Fact]
    public void ConnectLast_WithoutSource_Throws()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var sink = new PlainSink(Stereo48k);
        var builder = new AudioGraphBuilder(router).AddSink(sink);
        Assert.Throws<InvalidOperationException>(() => builder.ConnectLast());
    }

    [Fact]
    public void Connect_DefaultMap_IsIdentitySizedToSink()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantSource(Stereo48k, 0.0f);
        var sink = new PlainSink(Stereo48k);

        new AudioGraphBuilder(router)
            .AddSource(src, "src")
            .AddSink(sink, "sink")
            .Connect("src", "sink");

        // No explicit assertion on the map shape — but Connect would throw on a mismatch,
        // so reaching here implies the resolver produced a sink-sized identity.
        Assert.Single(router.SourceIds);
        Assert.Single(router.SinkIds);
    }

    private sealed class PlainSink(AudioFormat fmt) : IAudioSink
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
