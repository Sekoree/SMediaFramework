using System.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioRouterClockingTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    // --- per-output isolation -----------------------------------------------

    [Fact]
    public void SlowOutput_DoesNotThrottleFastOutput()
    {
        // sinkSlow blocks for 50 ms inside Submit (simulating a clocked NDI
        // sender). With the OutputPump model, this must NOT slow the router or
        // the fast output - its drainer thread is independent.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var sinkFast = new CountingOutput(Stereo, blockMs: 0);
        var sinkSlow = new CountingOutput(Stereo, blockMs: 50);

        r.AddSource(src, "src");
        r.AddOutput(sinkFast, "fast");
        r.AddOutput(sinkSlow, "slow");
        r.AddRoute("src", "fast", ChannelMap.Identity(2));
        r.AddRoute("src", "slow", ChannelMap.Identity(2));

        r.Start();
        // 480 samples @ 48kHz = 10ms/chunk. In ~250ms we'd expect ~25 chunks
        // produced. Without isolation, the slow output (50ms/chunk) would cap
        // production at ~5 chunks.
        Thread.Sleep(250);
        var produced = r.ChunksProduced;
        r.Stop();

        Assert.True(produced >= 15,
            $"expected at least 15 chunks in 250ms with isolated outputs, got {produced}");

        // Fast output got most of the production; slow output may have dropped.
        var fastStats = r.GetPumpStats("fast");
        var slowStats = r.GetPumpStats("slow");
        Assert.True(fastStats.Processed > slowStats.Processed,
            $"fast output should outpace slow output (fast={fastStats.Processed}, slow={slowStats.Processed})");
    }

    // --- slaved clock ------------------------------------------------------

    [Fact]
    public void SlaveTo_PacesAgainstOutputClock()
    {
        // The clocked output only signals "ready" once per 5 ms. The router
        // should produce roughly one chunk every 5 ms, regardless of what
        // the wall-clock chunk duration would dictate.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);  // 10 ms wall-clock chunk
        var src = new TestSource(Stereo, _ => 1f);
        var output = new ManualClockOutput(Stereo, perChunkMs: 5);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));
        r.SlaveTo("out");

        var sw = Stopwatch.StartNew();
        r.Start();
        Thread.Sleep(200);
        sw.Stop();
        var produced = r.ChunksProduced;
        r.Stop();

        // The clocked output signals ~once/5 ms, so production scales with the ACTUAL elapsed window, not
        // the requested 200 ms (a loaded CI runner sleeps well past it - an absolute range is flaky).
        var expected = sw.Elapsed.TotalMilliseconds / 5.0;
        Assert.InRange(produced, expected * 0.4, expected * 1.75 + 5);
    }

    [Fact]
    public void SlaveTo_NonClockedOutput_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddOutput(new CountingOutput(Stereo), "out");
        Assert.Throws<ArgumentException>(() => r.SlaveTo("out"));
    }

    [Fact]
    public void SlaveTo_RemovedOutput_FallsBackToWallClock()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var output = new ManualClockOutput(Stereo, perChunkMs: 5);

        r.AddSource(src, "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));
        r.SlaveTo("out");

        r.Start();
        Thread.Sleep(50);
        Assert.True(r.RemoveOutput("out"));

        // After remove, the slaved clock falls back to wall-clock pacing
        // (10 ms/chunk). Router shouldn't stall.
        var producedAtRemove = r.ChunksProduced;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 200) Thread.Sleep(10);
        var producedAfter = r.ChunksProduced;
        r.Stop();

        Assert.True(producedAfter > producedAtRemove,
            $"router should keep producing after slaved output removed (was {producedAtRemove}, now {producedAfter})");
    }

    [Fact]
    public void SlaveToIngest_PacesAgainstPlaybackClock()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var ingest = new AdvancingIngestClock();

        r.AddSource(src, "src");
        r.AddOutput(new CountingOutput(Stereo), "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));
        r.SlaveToIngest(ingest);

        r.Start();
        for (var i = 0; i < 30; i++)
        {
            ingest.Advance(TimeSpan.FromMilliseconds(5));
            Thread.Sleep(5);
        }

        var produced = r.ChunksProduced;
        r.Stop();
        Assert.InRange(produced, 10, 80);
    }

    [Fact]
    public void SetClock_WhileRunning_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "src");
        r.Start();
        Assert.Throws<InvalidOperationException>(() =>
            r.SetClock(new WallClockRouterClock(SampleRate, 480)));
        r.Stop();
    }

    // --- primary-output backpressure (no flood-drop) -----------------------

    [Fact]
    public void PrimaryOutput_AppliesBackpressure_InsteadOfDroppingAudio()
    {
        // Regression: WaitForCapacity paces on the device ring, but the router commits into the pump
        // queue first (a drainer thread moves pump -> ring). If WaitForCapacity keeps signalling "ready"
        // while the drainer lags, the router used to flood the pump and DROP the overflow - and a dropped
        // chunk on the master output permanently desyncs A/V (played sample count keeps advancing while
        // the audio content skips). The primary output must now backpressure (wait for its drainer)
        // rather than drop. Here WaitForCapacity is always ready and Submit is slow, so the broken
        // behaviour would drop hundreds/thousands of chunks in 300 ms.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var primary = new AlwaysReadySlowOutput(Stereo, blockMs: 5);

        r.AddSource(src, "src");
        r.AddOutput(primary, "primary");
        r.AddRoute("src", "primary", ChannelMap.Identity(2));
        r.SlaveTo("primary");

        r.Start();
        // Poll until the drainer has made meaningful progress rather than asserting a count after a fixed
        // sleep: a loaded CI runner processes far fewer chunks per wall-ms (processed=9-in-300ms flake), so
        // give it time - a genuine stall never reaches the threshold within the timeout and still fails.
        var stats = r.GetPumpStats("primary");
        var sw = Stopwatch.StartNew();
        while (stats.Processed <= 10 && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(20);
            stats = r.GetPumpStats("primary");
        }
        r.Stop();

        Assert.True(stats.Processed > 10,
            $"drainer should make steady progress (processed={stats.Processed})");
        // Backpressure means dropped stays ~0 no matter how long the window runs (the router waits, never
        // floods the pump), so this holds even after the poll above.
        Assert.True(stats.Dropped < 10,
            $"primary output must backpressure rather than flood-drop (dropped={stats.Dropped}, processed={stats.Processed})");
    }

    [Fact]
    public void NonPrimaryOutput_StillDrops_WhenItCannotKeepUp()
    {
        // Isolation must be preserved: backpressure is ONLY for the pacing/primary output. A slow
        // NON-primary output must keep dropping so it can't stall the shared router (and the primary).
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var pacer = new ManualClockOutput(Stereo, perChunkMs: 2); // fast primary
        var slow = new AlwaysReadySlowOutput(Stereo, blockMs: 20); // slow secondary

        r.AddSource(src, "src");
        r.AddOutput(pacer, "pacer");
        r.AddOutput(slow, "slow");
        r.AddRoute("src", "pacer", ChannelMap.Identity(2));
        r.AddRoute("src", "slow", ChannelMap.Identity(2));
        r.SlaveTo("pacer");

        r.Start();
        Thread.Sleep(300);
        var slowStats = r.GetPumpStats("slow");
        r.Stop();

        Assert.True(slowStats.Dropped > 0,
            $"non-primary slow output should still drop to stay isolated (dropped={slowStats.Dropped})");
    }

    // --- helpers -----------------------------------------------------------

    private sealed class TestSource(AudioFormat fmt, Func<int, float>? perChannelValue = null) : IAudioSource
    {
        private readonly Func<int, float> _perChannel = perChannelValue ?? (_ => 0f);
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> dst)
        {
            var samplesPerChannel = dst.Length / Format.Channels;
            for (var s = 0; s < samplesPerChannel; s++)
                for (var c = 0; c < Format.Channels; c++)
                    dst[s * Format.Channels + c] = _perChannel(c);
            return dst.Length;
        }
    }

    private sealed class CountingOutput(AudioFormat fmt, int blockMs = 0) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (blockMs > 0) Thread.Sleep(blockMs);
        }
    }

    /// <summary>Output that paces the router via <see cref="IClockedOutput"/>: ready once every <c>perChunkMs</c> ms.</summary>
    private sealed class ManualClockOutput(AudioFormat fmt, int perChunkMs) : IAudioOutput, IClockedOutput
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _readyCount;

        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples) { }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            var nextReadyAt = (Volatile.Read(ref _readyCount) + 1) * perChunkMs;
            while (!token.IsCancellationRequested)
            {
                var elapsed = _sw.ElapsedMilliseconds;
                if (elapsed >= nextReadyAt)
                {
                    Interlocked.Increment(ref _readyCount);
                    return true;
                }
                var sleep = (int)Math.Max(1, nextReadyAt - elapsed);
                if (token.WaitHandle.WaitOne(sleep)) return false;
            }
            return false;
        }
    }

    /// <summary>Clocked output that is always "ready" to accept a chunk but whose Submit (the drainer
    /// side) blocks for <c>blockMs</c> - models a drainer that lags the producer, exercising the
    /// primary-output backpressure path.</summary>
    private sealed class AlwaysReadySlowOutput(AudioFormat fmt, int blockMs) : IAudioOutput, IClockedOutput
    {
        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (blockMs > 0) Thread.Sleep(blockMs);
        }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }

    private sealed class AdvancingIngestClock : IPlaybackClock
    {
        private TimeSpan _elapsed;

        public TimeSpan ElapsedSinceStart => _elapsed;
        public bool IsAdvancing => true;

        public void Advance(TimeSpan delta) => _elapsed += delta;
    }
}
