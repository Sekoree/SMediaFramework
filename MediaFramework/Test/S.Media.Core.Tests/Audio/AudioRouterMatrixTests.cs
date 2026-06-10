using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>A2 (HotPath review 2026-06-10): per-cell matrix reconciliation on the router and the
/// standard layout preset matrices.</summary>
public sealed class AudioRouterMatrixTests
{
    [Fact]
    public void ApplyMatrix_InstallsOneRoutePerNonZeroCell()
    {
        using var router = new AudioRouter(48000);
        var srcId = router.AddSource(new ConstantSource(new AudioFormat(48000, 6)));
        var outId = router.AddOutput(new CapturingOutput(new AudioFormat(48000, 2)));

        router.ApplyMatrix(srcId, outId, AudioChannelLayoutPresets.Downmix(6, 2));

        // 5.1→2.0: FL→L, FR→R, FC→L, FC→R, BL→L, BR→R = 6 non-zero cells.
        Assert.Equal(6, router.Routes.Count);
    }

    [Fact]
    public void ApplyMatrix_Reapply_UpdatesRemovesAndAdds_WithoutTouchingForeignRoutes()
    {
        using var router = new AudioRouter(48000);
        var srcId = router.AddSource(new ConstantSource(new AudioFormat(48000, 2)));
        var outId = router.AddOutput(new CapturingOutput(new AudioFormat(48000, 2)));

        // A manually registered route for the same pair must survive matrix reconciliation.
        var manualRouteId = router.Route(srcId, outId, gain: 0.1f);

        var identity = new float[2, 2];
        identity[0, 0] = 1f;
        identity[1, 1] = 1f;
        router.ApplyMatrix(srcId, outId, identity);
        Assert.Equal(3, router.Routes.Count); // manual + 2 cells

        var swap = new float[2, 2];
        swap[0, 1] = 0.5f;
        swap[1, 0] = 0.25f;
        router.ApplyMatrix(srcId, outId, swap);

        var routes = router.Routes;
        Assert.Equal(3, routes.Count); // manual + 2 swapped cells (identity cells removed)
        Assert.Contains(routes, r => r.RouteId == manualRouteId);
        Assert.Contains(routes, r => r.RouteId.EndsWith("#0:1", StringComparison.Ordinal) && r.GainSlot.Target == 0.5f);
        Assert.Contains(routes, r => r.RouteId.EndsWith("#1:0", StringComparison.Ordinal) && r.GainSlot.Target == 0.25f);

        Assert.Equal(2, router.RemoveMatrix(srcId, outId));
        Assert.Single(router.Routes);
    }

    [Fact]
    public void ApplyMatrix_DimensionMismatch_Throws()
    {
        using var router = new AudioRouter(48000);
        var srcId = router.AddSource(new ConstantSource(new AudioFormat(48000, 2)));
        var outId = router.AddOutput(new CapturingOutput(new AudioFormat(48000, 2)));

        Assert.Throws<ArgumentException>(() => router.ApplyMatrix(srcId, outId, new float[6, 2]));
    }

    [Fact]
    public void ApplyMatrix_PrefixCollisionWithForeignPair_Throws()
    {
        using var router = new AudioRouter(48000);
        var srcId = router.AddSource(new ConstantSource(new AudioFormat(48000, 2)));
        var outA = router.AddOutput(new CapturingOutput(new AudioFormat(48000, 2)));
        var outB = router.AddOutput(new CapturingOutput(new AudioFormat(48000, 2)));

        var identity = AudioChannelLayoutPresets.Passthrough(2);
        router.ApplyMatrix(srcId, outA, identity, routeIdPrefix: "shared");
        Assert.Throws<ArgumentException>(() => router.ApplyMatrix(srcId, outB, identity, routeIdPrefix: "shared"));
    }

    [Fact]
    public void ApplyMatrix_EndToEnd_SwapMatrixSwapsChannels()
    {
        using var router = new AudioRouter(48000, chunkSamples: 480);
        var srcId = router.AddSource(new ConstantSource(new AudioFormat(48000, 2), ch0: 0.5f, ch1: 0.25f));
        var capture = new CapturingOutput(new AudioFormat(48000, 2));
        var outId = router.AddOutput(capture);

        var swap = new float[2, 2];
        swap[0, 1] = 1f;
        swap[1, 0] = 1f;
        router.ApplyMatrix(srcId, outId, swap);

        router.Play();
        try
        {
            // First chunk fades in from silence (new cells start at gain 0); wait for steady chunks.
            Assert.True(capture.WaitForChunks(5, TimeSpan.FromSeconds(2)), "router produced too few chunks");
        }
        finally
        {
            router.Stop();
        }

        var chunk = capture.LastChunk();
        Assert.Equal(0.25f, chunk[0], 3); // out L = src ch1
        Assert.Equal(0.5f, chunk[1], 3);  // out R = src ch0
    }

    [Fact]
    public void Presets_Downmix51ToStereo_NormalizedColumnsSumToOne()
    {
        var m = AudioChannelLayoutPresets.Downmix(6, 2);
        for (var d = 0; d < 2; d++)
        {
            var sum = 0f;
            for (var s = 0; s < 6; s++)
                sum += Math.Abs(m[s, d]);
            Assert.Equal(1f, sum, 3);
        }

        // LFE (index 3) is dropped.
        Assert.Equal(0f, m[3, 0]);
        Assert.Equal(0f, m[3, 1]);
        // Relative balance survives normalization: FC sits −3 dB under FL.
        Assert.Equal(0.70710678f, m[2, 0] / m[0, 0], 4);
    }

    [Fact]
    public void Presets_SupportMatrix()
    {
        Assert.True(AudioChannelLayoutPresets.TryGetDownmix(1, 2, out _));
        Assert.True(AudioChannelLayoutPresets.TryGetDownmix(2, 1, out _));
        Assert.True(AudioChannelLayoutPresets.TryGetDownmix(2, 8, out _));
        Assert.True(AudioChannelLayoutPresets.TryGetDownmix(8, 2, out _));
        Assert.True(AudioChannelLayoutPresets.TryGetDownmix(8, 6, out _));
        Assert.False(AudioChannelLayoutPresets.TryGetDownmix(6, 4, out _));
        Assert.False(AudioChannelLayoutPresets.TryGetDownmix(0, 2, out _));

        var identity = AudioChannelLayoutPresets.Passthrough(4);
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
                Assert.Equal(i == j ? 1f : 0f, identity[i, j]);
        }
    }

    /// <summary>Emits a constant value per channel forever (never exhausts).</summary>
    private sealed class ConstantSource(AudioFormat fmt, float ch0 = 0.5f, float ch1 = 0.25f) : IAudioSource
    {
        public AudioFormat Format => fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst)
        {
            for (var i = 0; i < dst.Length; i++)
            {
                var ch = i % fmt.Channels;
                dst[i] = ch == 0 ? ch0 : ch == 1 ? ch1 : 0f;
            }

            return dst.Length;
        }
    }

    private sealed class CapturingOutput(AudioFormat fmt) : IAudioOutput
    {
        private readonly Lock _gate = new();
        private float[] _last = [];
        private int _chunks;

        public AudioFormat Format => fmt;

        public void Submit(ReadOnlySpan<float> samples)
        {
            lock (_gate)
            {
                if (_last.Length != samples.Length)
                    _last = new float[samples.Length];
                samples.CopyTo(_last);
                _chunks++;
            }
        }

        public bool WaitForChunks(int count, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                lock (_gate)
                {
                    if (_chunks >= count)
                        return true;
                }

                Thread.Sleep(5);
            }

            return false;
        }

        public float[] LastChunk()
        {
            lock (_gate)
                return (float[])_last.Clone();
        }
    }
}
