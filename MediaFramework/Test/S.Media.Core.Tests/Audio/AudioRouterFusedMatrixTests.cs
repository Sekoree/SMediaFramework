using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Fused matrix mixing (perf: one dense pass instead of one full-buffer pass per matrix cell).
/// The kernels must be numerically equivalent to the per-cell ApplyRoute path they replace -
/// settled and ramping - and the end-to-end ApplyMatrix flow must still mix through the router.
/// </summary>
public class AudioRouterFusedMatrixTests
{
    private const int SrcChannels = 6;
    private const int DstChannels = 4;
    private const int Samples = 480;

    private static float[] MakeSource()
    {
        var src = new float[Samples * SrcChannels];
        var rng = new Random(42);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)(rng.NextDouble() * 2 - 1);
        return src;
    }

    private static float[] MakeGains(Random rng)
    {
        var gains = new float[SrcChannels * DstChannels]; // [dst * S + src]
        for (var i = 0; i < gains.Length; i++)
            gains[i] = rng.NextDouble() < 0.3 ? 0f : (float)rng.NextDouble();
        return gains;
    }

    private static ChannelMap SingleCell(int src, int dst)
    {
        Span<int> map = stackalloc int[DstChannels];
        map.Fill(ChannelMap.Silence);
        map[dst] = src;
        return new ChannelMap(map);
    }

    [Fact]
    public void FusedSettled_MatchesPerCellApplyRoute()
    {
        var src = MakeSource();
        var gains = MakeGains(new Random(7));

        var perCell = new float[Samples * DstChannels];
        for (var d = 0; d < DstChannels; d++)
        {
            for (var s = 0; s < SrcChannels; s++)
            {
                var gain = gains[d * SrcChannels + s];
                if (gain == 0f)
                    continue;
                AudioRouter.ApplyRoute(src, SrcChannels, perCell, DstChannels,
                    SingleCell(s, d), gain, gain, Samples);
            }
        }

        var fused = new float[Samples * DstChannels];
        AudioRouter.ApplyFusedMatrixSettled(src, SrcChannels, fused, DstChannels, gains, Samples);

        for (var i = 0; i < fused.Length; i++)
            Assert.Equal(perCell[i], fused[i], 1e-4f);
    }

    [Fact]
    public void FusedRamp_MatchesPerCellApplyRouteRamp()
    {
        var src = MakeSource();
        var rng = new Random(11);
        var from = MakeGains(rng);
        var to = MakeGains(rng);

        var perCell = new float[Samples * DstChannels];
        for (var d = 0; d < DstChannels; d++)
        {
            for (var s = 0; s < SrcChannels; s++)
            {
                var index = d * SrcChannels + s;
                if (from[index] == 0f && to[index] == 0f)
                    continue;
                AudioRouter.ApplyRoute(src, SrcChannels, perCell, DstChannels,
                    SingleCell(s, d), from[index], to[index], Samples);
            }
        }

        var fused = new float[Samples * DstChannels];
        AudioRouter.ApplyFusedMatrixRamp(src, SrcChannels, fused, DstChannels, from, to, Samples);

        for (var i = 0; i < fused.Length; i++)
            Assert.Equal(perCell[i], fused[i], 1e-4f);
    }

    [Fact]
    public void TryGetSingleCell_DetectsShapeCorrectly()
    {
        Assert.True(AudioRouter.TryGetSingleCell(SingleCell(2, 1), out var src, out var dst));
        Assert.Equal(2, src);
        Assert.Equal(1, dst);
        Assert.False(AudioRouter.TryGetSingleCell(ChannelMap.Identity(2), out _, out _));
        Assert.False(AudioRouter.TryGetSingleCell(new ChannelMap([-1, -1]), out _, out _));
    }

    [Fact]
    public void ApplyMatrix_EndToEnd_MixesThroughFusedPath()
    {
        using var router = new AudioRouter(48000);
        var source = new ConstantSource(new AudioFormat(48000, 2));
        var output = new CapturingOutput(new AudioFormat(48000, 2));
        var srcId = router.AddSource(source);
        var outId = router.AddOutput(output);

        // Dense 2x2 (4 cells - meets the fusion threshold): every output channel gets 0.5 of
        // each source channel, so steady state is 0.5 + 0.5 = 1.0 on both channels.
        router.ApplyMatrix(srcId, outId, new float[,] { { 0.5f, 0.5f }, { 0.5f, 0.5f } });
        router.Play();
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && output.MaxSample < 0.95f)
                Thread.Sleep(10);
        }
        finally
        {
            router.Stop();
        }

        // The first chunks fade in from silence; steady state must reach the dense mix value.
        Assert.InRange(output.MaxSample, 0.95f, 1.05f);
    }

    [Fact]
    public void DuplicateCellRoutes_SumAdditivelyThroughFusedPath()
    {
        using var router = new AudioRouter(48000);
        var source = new ConstantSource(new AudioFormat(48000, 2));
        var output = new CapturingOutput(new AudioFormat(48000, 2));
        var srcId = router.AddSource(source);
        var outId = router.AddOutput(output);

        // Four single-cell routes (meets the fusion threshold) with TWO routes on the same cell
        // (0→0). AddRoute documents additive per-cell contributions, so channel 0 must reach
        // 0.3 + 0.4 + 0.2 (from 1→0) = 0.9 - a fused gather that ASSIGNS instead of summing
        // regresses this to 0.6.
        Span<int> cell00 = [0, ChannelMap.Silence];
        Span<int> cell10 = [1, ChannelMap.Silence];
        Span<int> cell11 = [ChannelMap.Silence, 1];
        router.AddRoute(srcId, outId, "a", new ChannelMap(cell00), 0.3f);
        router.AddRoute(srcId, outId, "b", new ChannelMap(cell00), 0.4f);
        router.AddRoute(srcId, outId, "c", new ChannelMap(cell10), 0.2f);
        router.AddRoute(srcId, outId, "d", new ChannelMap(cell11), 0.5f);
        router.Play();
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && output.MaxSample < 0.85f)
                Thread.Sleep(10);
        }
        finally
        {
            router.Stop();
        }

        Assert.InRange(output.MaxSample, 0.85f, 0.95f);
    }

    private sealed class ConstantSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format => fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst)
        {
            dst.Fill(1f);
            return dst.Length;
        }
    }

    private sealed class CapturingOutput(AudioFormat fmt) : IAudioOutput
    {
        private float _maxSample;
        public AudioFormat Format => fmt;
        public float MaxSample => Volatile.Read(ref _maxSample);
        public void Submit(ReadOnlySpan<float> samples)
        {
            var max = Volatile.Read(ref _maxSample);
            foreach (var sample in samples)
            {
                if (sample > max)
                    max = sample;
            }
            Volatile.Write(ref _maxSample, max);
        }
    }
}
