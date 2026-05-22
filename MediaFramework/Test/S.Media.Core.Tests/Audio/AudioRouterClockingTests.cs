using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
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
        // the fast output — its drainer thread is independent.
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

        r.Start();
        Thread.Sleep(200);
        var produced = r.ChunksProduced;
        r.Stop();

        // 200ms / 5ms ≈ 40; allow generous slack for thread scheduling.
        Assert.InRange(produced, 25, 60);
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

    private sealed class AdvancingIngestClock : IPlaybackClock
    {
        private TimeSpan _elapsed;

        public TimeSpan ElapsedSinceStart => _elapsed;
        public bool IsAdvancing => true;

        public void Advance(TimeSpan delta) => _elapsed += delta;
    }
}
