using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterSourceGatingTests
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void RunLoop_UnroutedSource_IsNotConsumedUntilRouted()
    {
        // Regression for the source-drain bug: registering a source must not consume it. The router
        // only reads a source once a route targets it — so a cue/soundboard can load a clip and not
        // lose its audio before firing/routing it.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new CountingSource(Stereo);
        r.AddSource(src, "src");
        r.AddOutput(new NullOutput(Stereo), "out");
        // Intentionally no route from "src" → "out" yet.

        r.Start();
        Thread.Sleep(80); // several ~10 ms chunk periods
        Assert.True(r.IsRunning, "router should keep running while waiting for a route, not auto-stop");
        Assert.Equal(0, src.ReadCalls); // unrouted → never read

        r.AddRoute("src", "out", ChannelMap.Identity(2));
        var afterRouteBaseline = src.ReadCalls;
        Thread.Sleep(80);
        Assert.True(src.ReadCalls > afterRouteBaseline, "source should be consumed once routed");

        r.Stop();
    }

    private sealed class CountingSource(AudioFormat fmt) : IAudioSource
    {
        private int _reads;
        public int ReadCalls => Volatile.Read(ref _reads);
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> dst)
        {
            Interlocked.Increment(ref _reads);
            dst.Clear();
            return dst.Length;
        }
    }

    private sealed class NullOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }
}
