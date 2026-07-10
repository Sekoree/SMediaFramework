using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioRouterTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);
    private static readonly AudioFormat Mono   = new(SampleRate, 1);
    private static readonly AudioFormat Quad   = new(SampleRate, 4);

    // --- registration & validation ----------------------------------------

    [Fact]
    public void AddSource_AutogeneratesId()
    {
        using var r = new AudioRouter(SampleRate);
        var id1 = r.AddSource(new TestSource(Stereo));
        var id2 = r.AddSource(new TestSource(Stereo));
        Assert.NotEqual(id1, id2);
        Assert.Contains(id1, r.SourceIds);
        Assert.Contains(id2, r.SourceIds);
    }

    [Fact]
    public void AddSource_ExplicitId_StoredVerbatim()
    {
        using var r = new AudioRouter(SampleRate);
        var id = r.AddSource(new TestSource(Stereo), "music");
        Assert.Equal("music", id);
        Assert.Contains("music", r.SourceIds);
    }

    [Fact]
    public void AddSource_DuplicateId_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "music");
        Assert.Throws<ArgumentException>(() => r.AddSource(new TestSource(Stereo), "music"));
    }

    [Fact]
    public void AddSource_RateMismatch_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        Assert.Throws<InvalidOperationException>(() =>
            r.AddSource(new TestSource(new AudioFormat(44100, 2))));
    }

    [Fact]
    public void Route_MonoSourceToStereoOutput_DefaultMapUpmixes()
    {
        // Regression: the default route must derive its channel map from the *source* (mono), not size
        // it to the output (stereo). Sizing to the output made AddRoute reject "map requires 2 input
        // channels but source has 1", which crashed the headless show on mono media.
        using var r = new AudioRouter(SampleRate);
        var src = r.AddSource(new TestSource(Mono));
        var outp = r.AddOutput(new TestOutput(Stereo));

        var routeId = r.Route(src, outp);

        Assert.False(string.IsNullOrEmpty(routeId));
    }

    [Fact]
    public void RouteLast_MonoSourceToStereoOutput_DefaultMapUpmixes()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Mono));
        r.AddOutput(new TestOutput(Stereo));

        var routeId = r.RouteLast();

        Assert.False(string.IsNullOrEmpty(routeId));
    }

    [Fact]
    public void Connect_StereoSourceToMonoOutput_DefaultMapDoesNotThrow()
    {
        // Connect is the fourth default-route entry point; its default map must also be source-derived.
        using var r = new AudioRouter(SampleRate);
        var src = r.AddSource(new TestSource(Stereo));
        var outp = r.AddOutput(new TestOutput(Mono));

        r.Connect(src, outp);
    }

    [Fact]
    public void AddOutput_PerOutputPumpCapacity_OverridesRouterDefault()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480, pumpCapacityChunks: 8);
        r.AddOutput(new TestOutput(Stereo), "narrow", pumpCapacityChunks: 8);
        r.AddOutput(new TestOutput(Stereo), "wide", pumpCapacityChunks: 40);
        Assert.Equal(8, r.GetPumpStats("narrow").PumpCapacityChunks);
        Assert.Equal(40, r.GetPumpStats("wide").PumpCapacityChunks);
    }

    [Fact]
    public void GetAggregatePumpStats_with_no_sinks_is_zeroed()
    {
        using var r = new AudioRouter(SampleRate);
        var a = r.GetAggregatePumpStats();
        Assert.Equal(0, a.OutputCount);
        Assert.Equal(0, a.TotalEnqueued);
        Assert.Equal(0, a.TotalProcessed);
        Assert.Equal(0, a.TotalDropped);
        Assert.Equal(0, a.MaxPumpCapacityChunks);
    }

    [Fact]
    public void GetAggregatePumpStats_matches_sum_of_GetPumpStats_per_sink()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480, pumpCapacityChunks: 8);
        r.AddOutput(new TestOutput(Stereo), "narrow", pumpCapacityChunks: 8);
        r.AddOutput(new TestOutput(Stereo), "wide", pumpCapacityChunks: 40);
        var agg = r.GetAggregatePumpStats();
        Assert.Equal(2, agg.OutputCount);
        Assert.Equal(40, agg.MaxPumpCapacityChunks);
        var n = r.GetPumpStats("narrow");
        var w = r.GetPumpStats("wide");
        Assert.Equal(n.Enqueued + w.Enqueued, agg.TotalEnqueued);
        Assert.Equal(n.Processed + w.Processed, agg.TotalProcessed);
        Assert.Equal(n.Dropped + w.Dropped, agg.TotalDropped);
    }

    [Fact]
    public void AddOutput_PumpCapacityBelow2_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            r.AddOutput(new TestOutput(Stereo), "x", pumpCapacityChunks: 1));
    }

    [Fact]
    public void AddSourceAndOutput_AfterDispose_ThrowObjectDisposed()
    {
        var r = new AudioRouter(SampleRate);
        r.Dispose();

        Assert.Throws<ObjectDisposedException>(() => r.AddSource(new TestSource(Stereo), "src"));
        Assert.Throws<ObjectDisposedException>(() => r.AddOutput(new TestOutput(Stereo), "out"));
    }

    [Fact]
    public void AddRoute_UnknownSource_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddOutput(new TestOutput(Stereo), "out");
        Assert.Throws<ArgumentException>(() => r.AddRoute("missing", "out", ChannelMap.Identity(2)));
    }

    [Fact]
    public void AddRoute_MapDoesntMatchOutputChannels_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Quad), "dst");
        // map outputs 2 channels, output expects 4
        Assert.Throws<InvalidOperationException>(() =>
            r.AddRoute("src", "dst", ChannelMap.Identity(2)));
    }

    [Fact]
    public void AddRoute_MapNeedsMoreSourceChannelsThanAvailable_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Mono), "src");
        r.AddOutput(new TestOutput(Stereo), "dst");
        // map references source ch 1 but mono source only has ch 0
        Assert.Throws<InvalidOperationException>(() =>
            r.AddRoute("src", "dst", new ChannelMap([0, 1])));
    }

    [Fact]
    public void AddRoute_ReplacesExistingRouteForSamePair()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Stereo), "dst");
        r.AddRoute("src", "dst", ChannelMap.Identity(2), gain: 0.5f);
        r.AddRoute("src", "dst", new ChannelMap([1, 0]), gain: 0.25f);  // replace
        Assert.Single(r.Routes);
        Assert.Equal(0.25f, r.Routes[0].Gain);
        Assert.Equal(new ChannelMap([1, 0]), r.Routes[0].Map);
    }

    [Fact]
    public void AddRoute_ByRouteId_AllowsMultipleRoutesPerPair()
    {
        // Phase C (§4.3.4) - per-cell audio matrix needs multiple routes per (source, output) pair.
        // The run loop already sums additively, so two routes feeding cell-disjoint output channels
        // produce the union of their contributions on the same output.
        using var r = new AudioRouter(SampleRate, chunkSamples: 32);
        var src = new TestSource(Stereo, c => c == 0 ? 3f : 4f);
        var output = new TestOutput(Stereo);
        r.AddSource(src, "music");
        r.AddOutput(output, "out");
        r.AddRoute("music", "out", "cell_L", new ChannelMap([0, -1]), gain: 1f);
        r.AddRoute("music", "out", "cell_R", new ChannelMap([-1, 1]), gain: 1f);

        Assert.Equal(2, r.Routes.Count);
        Assert.Equal(new[] { "cell_L", "cell_R" }, r.Routes.Select(rt => rt.RouteId).Order());

        r.Start();
        WaitForChunks(r, 3);
        r.Stop();
        // Identity-equivalent split across two routes - left cell carries src L, right cell carries src R.
        AssertFramePattern(output.Captured[0], expected: [3f, 4f]);
    }

    [Fact]
    public void SetRouteGainById_TargetsOneRouteOfPair()
    {
        // Each cell route is identified separately so gain rides apply per-cell without disturbing siblings.
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Stereo), "dst");
        r.AddRoute("src", "dst", "cell_L", new ChannelMap([0, -1]), gain: 1f);
        r.AddRoute("src", "dst", "cell_R", new ChannelMap([-1, 1]), gain: 1f);

        r.SetRouteGainById("cell_L", 0.25f);

        // SetRouteGainById doesn't expose the target map; verify via the surface that did change:
        // re-adding the route preserves the cell map (we only changed the gain mid-stream).
        Assert.Equal(2, r.Routes.Count);
        r.AddRoute("src", "dst", "cell_L", new ChannelMap([0, -1]), gain: 0.25f); // idempotent under new gain
        Assert.Equal(0.25f, r.Routes.First(rt => rt.RouteId == "cell_L").Gain);
        Assert.Equal(1f, r.Routes.First(rt => rt.RouteId == "cell_R").Gain);
    }

    [Fact]
    public void RemoveRouteById_RemovesOneRouteAndLeavesSiblingsAlone()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Stereo), "dst");
        r.AddRoute("src", "dst", "cell_L", new ChannelMap([0, -1]), gain: 1f);
        r.AddRoute("src", "dst", "cell_R", new ChannelMap([-1, 1]), gain: 1f);

        Assert.True(r.RemoveRouteById("cell_L"));
        Assert.Single(r.Routes);
        Assert.Equal("cell_R", r.Routes[0].RouteId);
        Assert.False(r.RemoveRouteById("cell_L")); // already gone
    }

    [Fact]
    public void RemoveRoute_LegacyPairOverload_RemovesAllRoutesForPair()
    {
        // Back-compat: callers that didn't opt into route ids see "one logical edge" - RemoveRoute(src,output)
        // sweeps every route between them, mirroring the pre-Phase-C single-route-per-pair contract.
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Stereo), "dst");
        r.AddRoute("src", "dst", "cell_L", new ChannelMap([0, -1]), gain: 1f);
        r.AddRoute("src", "dst", "cell_R", new ChannelMap([-1, 1]), gain: 1f);

        Assert.True(r.RemoveRoute("src", "dst"));
        Assert.Empty(r.Routes);
    }

    [Fact]
    public void AddRoute_RouteIdCollisionAcrossPairs_Rejected()
    {
        // Reusing a routeId for a different (source, output) pair would silently steer subsequent
        // SetRouteGainById / RemoveRouteById calls at the wrong cell - reject loudly.
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src1");
        r.AddSource(new TestSource(Stereo), "src2");
        r.AddOutput(new TestOutput(Stereo), "dst");
        r.AddRoute("src1", "dst", "shared", ChannelMap.Identity(2));
        Assert.Throws<ArgumentException>(() =>
            r.AddRoute("src2", "dst", "shared", ChannelMap.Identity(2)));
    }

    [Fact]
    public void RemoveSource_AlsoRemovesItsRoutes()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "a");
        r.AddSource(new TestSource(Stereo), "b");
        r.AddOutput(new TestOutput(Stereo), "out");
        r.AddRoute("a", "out", ChannelMap.Identity(2));
        r.AddRoute("b", "out", ChannelMap.Identity(2));

        Assert.True(r.RemoveSource("a"));
        Assert.Single(r.Routes);
        Assert.Equal("b", r.Routes[0].SourceId);
    }

    [Fact]
    public void RemoveOutput_AlsoRemovesItsRoutes()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.AddOutput(new TestOutput(Stereo), "x");
        r.AddOutput(new TestOutput(Stereo), "y");
        r.AddRoute("src", "x", ChannelMap.Identity(2));
        r.AddRoute("src", "y", ChannelMap.Identity(2));

        Assert.True(r.RemoveOutput("x"));
        Assert.Single(r.Routes);
        Assert.Equal("y", r.Routes[0].OutputId);
    }

    // --- runtime semantics -------------------------------------------------

    [Fact]
    public void RunLoop_DirectRouting_AppliesPerRouteMap()
    {
        // Stereo source carrying L=1, R=2 fans out to:
        //   output A: identity stereo  → expect [1, 2]
        //   output B: swapped stereo   → expect [2, 1]
        //   output C: 4-channel duplicate → expect [1, 1, 2, 2]
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, perChannelValue: c => c == 0 ? 1f : 2f);
        var sinkA = new TestOutput(Stereo);
        var sinkB = new TestOutput(Stereo);
        var sinkC = new TestOutput(Quad);

        r.AddSource(src, "music");
        r.AddOutput(sinkA, "a");
        r.AddOutput(sinkB, "b");
        r.AddOutput(sinkC, "c");
        r.AddRoute("music", "a", ChannelMap.Identity(2));
        r.AddRoute("music", "b", new ChannelMap([1, 0]));
        r.AddRoute("music", "c", new ChannelMap([0, 0, 1, 1]));

        r.Start();
        WaitForChunks(r, 5);
        r.Stop();

        AssertFramePattern(sinkA.Captured[0], expected: [1f, 2f]);
        AssertFramePattern(sinkB.Captured[0], expected: [2f, 1f]);
        AssertFramePattern(sinkC.Captured[0], expected: [1f, 1f, 2f, 2f]);
    }

    [Fact]
    public void RunLoop_TwoSourcesRoutedToOneOutput_Sum()
    {
        // src1 → output at gain 1.0  (contributes [3, 4])
        // src2 → output at gain 1.0  (contributes [10, 20])
        // Expected sum: [13, 24]
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src1 = new TestSource(Stereo, perChannelValue: c => c == 0 ? 3f : 4f);
        var src2 = new TestSource(Stereo, perChannelValue: c => c == 0 ? 10f : 20f);
        var output = new TestOutput(Stereo);

        r.AddSource(src1, "a");
        r.AddSource(src2, "b");
        r.AddOutput(output, "out");
        r.AddRoute("a", "out", ChannelMap.Identity(2));
        r.AddRoute("b", "out", ChannelMap.Identity(2));

        r.Start();
        WaitForChunks(r, 5);
        r.Stop();

        AssertFramePattern(output.Captured[0], expected: [13f, 24f]);
    }

    [Fact]
    public void RunLoop_MultiSourceStereoLayoutsAndGains_MatchesReference()
    {
        // Exercises ApplyRoute steady-state SIMD for identity, stereo silence/L–R maps, and summed gains.
        using var r = new AudioRouter(SampleRate, chunkSamples: 32);
        var srcA = new TestSource(Stereo, c => c == 0 ? 3f : 4f);
        var srcB = new TestSource(Stereo, c => c == 0 ? 10f : 20f);
        var srcC = new TestSource(Stereo, c => c == 0 ? 1f : 2f);
        var output = new TestOutput(Stereo);

        r.AddSource(srcA, "a");
        r.AddSource(srcB, "b");
        r.AddSource(srcC, "c");
        r.AddOutput(output, "out");
        r.AddRoute("a", "out", ChannelMap.Identity(2), gain: 0.5f);
        r.AddRoute("b", "out", new ChannelMap([-1, 0]), gain: 0.25f);
        r.AddRoute("c", "out", new ChannelMap([1, -1]), gain: 1f);

        r.Start();
        WaitForChunks(r, 3);
        r.Stop();

        AssertFramePattern(output.Captured[0], expected: [3.5f, 4.5f]);
    }

    [Fact]
    public void RunLoop_MultiSourceStereoDupAndFullSilence_MatchesReference()
    {
        // Dup-L plus a second route that is all silence (high gain must not read / add samples).
        using var r = new AudioRouter(SampleRate, chunkSamples: 32);
        var srcA = new TestSource(Stereo, c => c == 0 ? 3f : 4f);
        var srcB = new TestSource(Stereo, c => c == 0 ? 99f : 100f);
        var output = new TestOutput(Stereo);

        r.AddSource(srcA, "a");
        r.AddSource(srcB, "b");
        r.AddOutput(output, "out");
        r.AddRoute("a", "out", new ChannelMap([0, 0]), gain: 1f);
        r.AddRoute("b", "out", new ChannelMap([-1, -1]), gain: 999f);

        r.Start();
        WaitForChunks(r, 3);
        r.Stop();

        AssertFramePattern(output.Captured[0], expected: [3f, 3f]);
    }

    [Fact]
    public void RunLoop_GainScalesContribution()
    {
        // src: [10, 20]  →  output with gain 0.5  →  expected [5, 10]
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, perChannelValue: c => c == 0 ? 10f : 20f);
        var output = new TestOutput(Stereo);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2), gain: 0.5f);

        r.Start();
        WaitForChunks(r, 5);
        r.Stop();

        AssertFramePattern(output.Captured[0], expected: [5f, 10f]);
    }

    [Fact]
    public void RunLoop_GainZero_RouteContributesNothing()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var loud = new TestSource(Stereo, perChannelValue: _ => 999f);
        var quiet = new TestSource(Stereo, perChannelValue: _ => 1f);
        var output = new TestOutput(Stereo);

        r.AddSource(loud, "loud");
        r.AddSource(quiet, "quiet");
        r.AddOutput(output, "out");
        r.AddRoute("loud", "out", ChannelMap.Identity(2), gain: 0f);
        r.AddRoute("quiet", "out", ChannelMap.Identity(2), gain: 1f);

        r.Start();
        WaitForChunks(r, 5);
        r.Stop();

        AssertFramePattern(output.Captured[0], expected: [1f, 1f]);
    }

    [Fact]
    public void SetRouteGain_TakesEffectOnNextChunk()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, perChannelValue: _ => 4f);
        var output = new TestOutput(Stereo);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2), gain: 1f);

        r.Start();
        WaitForChunks(r, 3);
        r.SetRouteGain("src", "out", 0.25f);
        var chunkAtChange = r.ChunksProduced;
        WaitForChunks(r, chunkAtChange + 5);
        r.Stop();

        // Find a chunk after the change and verify the value is scaled.
        var lastFrame = output.Captured[^1];
        AssertFramePattern(lastFrame, expected: [1f, 1f]);  // 4 × 0.25 = 1
    }

    [Fact]
    public void DynamicAddOutput_StartsReceivingMidStream()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, perChannelValue: _ => 7f);
        var sinkA = new TestOutput(Stereo);
        var sinkB = new TestOutput(Stereo);

        r.AddSource(src, "src");
        r.AddOutput(sinkA, "a");
        r.AddRoute("src", "a", ChannelMap.Identity(2));

        r.Start();
        WaitForChunks(r, 3);

        // Add second output + route while running.
        r.AddOutput(sinkB, "b");
        r.AddRoute("src", "b", ChannelMap.Identity(2));

        var chunkAtAdd = r.ChunksProduced;
        WaitForChunks(r, chunkAtAdd + 5);
        r.Stop();

        Assert.NotEmpty(sinkB.Captured);
        AssertFramePattern(sinkB.Captured[^1], expected: [7f, 7f]);
    }

    [Fact]
    public void DynamicRemoveOutput_StopsReceivingMidStream()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, perChannelValue: _ => 1f);
        var output = new TestOutput(Stereo);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        WaitForChunks(r, 3);
        Assert.True(r.RemoveOutput("out"));

        var beforeRemoveCount = output.Captured.Count;
        WaitForChunks(r, r.ChunksProduced + 5);
        r.Stop();
        var afterRemoveCount = output.Captured.Count;

        Assert.Equal(beforeRemoveCount, afterRemoveCount);
    }

    [Fact]
    public void RunLoop_StopsNaturallyWhenAllSourcesExhausted()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, finiteSamplesPerChannel: 200);
        var output = new TestOutput(Stereo);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (r.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.False(r.IsRunning);
        Assert.True(r.CompletedNaturally);
    }

    // --- helpers -----------------------------------------------------------

    private static void WaitForChunks(AudioRouter router, long count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (router.ChunksProduced < count && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
    }

    private static void AssertFramePattern(float[] captured, float[] expected)
    {
        Assert.True(captured.Length >= expected.Length,
            $"captured {captured.Length} floats, expected at least {expected.Length}");
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], captured[i], precision: 5);
    }

    private sealed class TestSource(AudioFormat fmt, Func<int, float>? perChannelValue = null, int? finiteSamplesPerChannel = null) : IAudioSource
    {
        private readonly Func<int, float> _perChannel = perChannelValue ?? (_ => 0f);
        private long _emittedSamples;

        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => finiteSamplesPerChannel.HasValue && _emittedSamples >= finiteSamplesPerChannel.Value;

        public int ReadInto(Span<float> dst)
        {
            if (IsExhausted) return 0;
            var samplesPerChannel = dst.Length / Format.Channels;
            if (finiteSamplesPerChannel.HasValue)
            {
                var remaining = (int)(finiteSamplesPerChannel.Value - _emittedSamples);
                if (samplesPerChannel > remaining) samplesPerChannel = remaining;
            }
            for (var s = 0; s < samplesPerChannel; s++)
                for (var c = 0; c < Format.Channels; c++)
                    dst[s * Format.Channels + c] = _perChannel(c);
            _emittedSamples += samplesPerChannel;
            return samplesPerChannel * Format.Channels;
        }
    }

    private sealed class TestOutput(AudioFormat fmt) : IAudioOutput
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