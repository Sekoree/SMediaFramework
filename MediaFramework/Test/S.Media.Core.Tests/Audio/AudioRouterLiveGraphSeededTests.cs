using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Seeded <see cref="AudioRouter"/> integration tests (real run loop + <see cref="SinkPump"/>).
/// Each iteration uses distinct sources so routes do not replace each other on the same (source, sink) pair.
/// </summary>
public sealed class AudioRouterLiveGraphSeededTests
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);
    private const int ChunkSamples = 64;

    private static readonly ChannelMap[] StereoToStereoMaps =
    [
        ChannelMap.Identity(2),
        new ChannelMap([1, 0]),
        new ChannelMap([-1, 0]),
        new ChannelMap([1, -1]),
        new ChannelMap([0, 0]),
        new ChannelMap([1, 1]),
        new ChannelMap([-1, -1]),
    ];

    [Fact]
    public void RunLoop_SeededRandomDistinctSourceRoutes_FirstChunkMatchesScalarReference()
    {
        var rnd = new Random(577_215);
        Span<int> pickScratch = stackalloc int[4];
        for (var iter = 0; iter < 32; iter++)
        {
            var nSrc = rnd.Next(1, 5);
            var nRoutes = rnd.Next(1, nSrc + 1);
            var srcL = new float[nSrc];
            var srcR = new float[nSrc];
            for (var i = 0; i < nSrc; i++)
            {
                srcL[i] = (float)(rnd.NextDouble() * 4 - 2);
                srcR[i] = (float)(rnd.NextDouble() * 4 - 2);
            }

            var pick = pickScratch[..nSrc];
            for (var i = 0; i < nSrc; i++) pick[i] = i;
            FisherYatesPartial(rnd, pick, nSrc);
            var routeSrcIdx = new int[nRoutes];
            for (var r = 0; r < nRoutes; r++)
                routeSrcIdx[r] = pick[r];

            var routes = new (int SrcIdx, ChannelMap Map, float Gain)[nRoutes];
            for (var r = 0; r < nRoutes; r++)
            {
                routes[r] = (
                    routeSrcIdx[r],
                    StereoToStereoMaps[rnd.Next(StereoToStereoMaps.Length)],
                    (float)(rnd.NextDouble() * 1.8 - 0.2));
            }

            var srcChunks = new float[nSrc][];
            for (var i = 0; i < nSrc; i++)
                FillStereoChunk(srcChunks[i] = new float[ChunkSamples * 2], srcL[i], srcR[i], ChunkSamples);

            var expected = new float[ChunkSamples * 2];
            foreach (var (srcIdx, map, gain) in routes)
                AccumulateSteadyRoute(srcChunks[srcIdx], 2, expected, 2, map, gain, ChunkSamples);

            using var router = new AudioRouter(SampleRate, chunkSamples: ChunkSamples);
            var sources = new string[nSrc];
            for (var i = 0; i < nSrc; i++)
            {
                var li = srcL[i];
                var ri = srcR[i];
                sources[i] = router.AddSource(new TestSource(Stereo, c => c == 0 ? li : ri), $"s{i}");
            }

            var sink = new TestSink(Stereo);
            router.AddSink(sink, "out");
            foreach (var (srcIdx, map, gain) in routes)
                router.AddRoute(sources[srcIdx], "out", map, gain);

            router.Start();
            WaitForChunks(router, 2);
            router.Stop();

            Assert.True(sink.Captured.Count > 0, $"iter {iter}: no chunks captured");
            var captured = sink.Captured[0];
            for (var i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], captured[i], precision: 4);
        }
    }

    private static void FisherYatesPartial(Random rnd, Span<int> indices, int n)
    {
        for (var i = n - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
    }

    private static void FillStereoChunk(Span<float> dst, float l, float r, int samplesPerChannel)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            dst[s * 2] = l;
            dst[s * 2 + 1] = r;
        }
    }

    private static void AccumulateSteadyRoute(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        ChannelMap map, float gain, int samplesPerChannel)
    {
        var routing = map.AsSpan();
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * dstChannels;
            for (var oc = 0; oc < dstChannels; oc++)
            {
                var ic = routing[oc];
                if (ic >= 0)
                    dst[dstBase + oc] += src[srcBase + ic] * gain;
            }
        }
    }

    private static void WaitForChunks(AudioRouter router, long count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (router.ChunksProduced < count && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
    }

    private sealed class TestSource(AudioFormat fmt, Func<int, float> perChannelValue) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> dst)
        {
            var samplesPerChannel = dst.Length / Format.Channels;
            for (var s = 0; s < samplesPerChannel; s++)
            for (var c = 0; c < Format.Channels; c++)
                dst[s * Format.Channels + c] = perChannelValue(c);
            return samplesPerChannel * Format.Channels;
        }
    }

    private sealed class TestSink(AudioFormat fmt) : IAudioSink
    {
        private readonly Lock _gate = new();
        private readonly List<float[]> _captured = [];

        public AudioFormat Format { get; } = fmt;

        public IReadOnlyList<float[]> Captured
        {
            get { lock (_gate) return _captured.ToArray(); }
        }

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            lock (_gate) _captured.Add(packedSamples.ToArray());
        }
    }
}
