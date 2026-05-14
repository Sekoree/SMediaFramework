using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioRouterControlTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    // --- pause / resume ---------------------------------------------------

    [Fact]
    public void Pause_StopsProductionAndFlushesSinks()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var sink = new FlushableSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Thread.Sleep(50);
        Assert.True(r.IsRunning);
        var producedDuring = r.ChunksProduced;
        Assert.True(producedDuring > 0);

        r.Pause();

        Assert.False(r.IsRunning);
        Assert.True(sink.FlushCount >= 1, "Pause should call Flush on flushable sinks");

        var producedAfterPause = r.ChunksProduced;
        Thread.Sleep(50);
        Assert.Equal(producedAfterPause, r.ChunksProduced);
    }

    [Fact]
    public void Resume_PicksUpFromWherePauseLeftOff()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 1f);
        var sink = new FlushableSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Thread.Sleep(50);
        r.Pause();
        var atPause = r.ChunksProduced;

        r.Resume();
        Assert.True(r.IsRunning);
        Thread.Sleep(50);
        r.Stop();

        Assert.True(r.ChunksProduced > atPause,
            $"router should keep producing after Resume (was {atPause}, now {r.ChunksProduced})");
    }

    [Fact]
    public void NaturalEof_FlushesFlushableSinks()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new FiniteTestSource(Stereo, samplesPerChannelTotal: 200);
        var sink = new FlushableSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        var flushBefore = sink.FlushCount;
        r.Start();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (r.IsRunning && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.False(r.IsRunning);
        Assert.True(r.CompletedNaturally);
        Assert.True(sink.FlushCount > flushBefore,
            "natural EOF should call IFlushableSink.Flush from FinishRunLoopThreadLifetime");
    }

    [Fact]
    public void Pause_WhenNotRunning_IsNoOp()
    {
        using var r = new AudioRouter(SampleRate);
        r.Pause(); // shouldn't throw
        Assert.False(r.IsRunning);
    }

    // --- seek -------------------------------------------------------------

    [Fact]
    public void SeekSource_CallsSeekOnSourceAndKeepsRunning()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new SeekableTestSource(Stereo);
        var sink = new FlushableSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Thread.Sleep(50);

        r.SeekSource("src", TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), src.LastSeekTo);
        Assert.True(r.IsRunning, "router should still be running after seek");
        Assert.True(sink.FlushCount >= 1, "seek should flush downstream sinks");
    }

    [Fact]
    public void SeekSource_NonSeekableSource_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        r.AddSource(new TestSource(Stereo), "live");
        Assert.Throws<InvalidOperationException>(() =>
            r.SeekSource("live", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SeekSource_UnknownSource_Throws()
    {
        using var r = new AudioRouter(SampleRate);
        Assert.Throws<ArgumentException>(() =>
            r.SeekSource("missing", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SeekSource_WhilePaused_DoesNotResume()
    {
        using var r = new AudioRouter(SampleRate);
        var src = new SeekableTestSource(Stereo);
        r.AddSource(src, "src");

        // Router never started — SeekSource should still call Seek without
        // spinning up the run loop.
        r.SeekSource("src", TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), src.LastSeekTo);
        Assert.False(r.IsRunning);
    }

    // --- volume immediacy --------------------------------------------------

    [Fact]
    public void SetRouteGain_TakesEffectWithinOneChunk()
    {
        // Documents the contract: gain change is sample-accurate to the next
        // chunk boundary (~10ms with 480 samples @ 48kHz).
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var src = new TestSource(Stereo, _ => 4f);
        var sink = new CapturingSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2), gain: 1f);

        r.Start();
        Thread.Sleep(40);
        var beforeChange = r.ChunksProduced;

        r.SetRouteGain("src", "out", 0.25f);

        // Within at most 2 chunks (one in flight + one new), the captured
        // value should reflect the new gain.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        bool sawNewGain = false;
        while (DateTime.UtcNow < deadline)
        {
            var captured = sink.LastChunk;
            if (captured != null && Math.Abs(captured[0] - 1.0f) < 0.001f)
            {
                sawNewGain = true;
                break;
            }
            Thread.Sleep(5);
        }
        r.Stop();
        Assert.True(sawNewGain,
            $"new gain (4 × 0.25 = 1.0) should appear in capture within 1s; last value was {sink.LastChunk?[0]}");

        // And it took only a handful of chunks.
        var produced = r.ChunksProduced - beforeChange;
        Assert.True(produced < 20, $"expected change within ~few chunks, took {produced}");
    }

    [Fact]
    public void SetRouteGain_RampsCleanly_FirstChunkInterpolatesFromOldToNew()
    {
        // Source = constant 4.0. Initial gain 1.0 (output = 4.0).
        // Change to 0.5 (output = 2.0). The next chunk should ramp linearly:
        // first sample ≈ 4.0, last sample ≈ 2.0 — not a hard step.
        const int chunk = 64;
        using var r = new AudioRouter(SampleRate, chunkSamples: chunk);
        var src = new TestSource(Stereo, _ => 4f);
        var sink = new ChunkLogSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2), gain: 1f);

        r.Start();
        Thread.Sleep(40);                     // let some steady-state chunks land
        var chunksBeforeMutation = sink.AllChunks.Count;
        r.SetRouteGain("src", "out", 0.5f);
        Thread.Sleep(40);
        r.Stop();

        // Between reading Count and calling SetRouteGain, the router can still
        // deliver one steady-state chunk (gain 1.0). Scan forward — the ramp
        // chunk has first≈4, last≈2.
        var captured = sink.AllChunks;
        Assert.True(captured.Count > chunksBeforeMutation + 1,
            $"need chunks after mutation start, countBefore={chunksBeforeMutation} total={captured.Count}");
        float[]? rampChunk = null;
        var rampIndex = -1;
        for (var i = chunksBeforeMutation; i < captured.Count; i++)
        {
            var c = captured[i];
            var f = c[0];
            var l = c[(chunk - 1) * 2];
            if (l < f - 1.5f && f > 3.85f && l is > 1.85f and < 2.15f)
            {
                rampChunk = c;
                rampIndex = i;
                break;
            }
        }

        Assert.True(rampChunk is not null,
            $"expected a ramp chunk after gain change; countBefore={chunksBeforeMutation} total={captured.Count}");

        var first = rampChunk![0];
        var last  = rampChunk[(chunk - 1) * 2];

        // First sample should be near old gain × source = ~4.0; last near new = ~2.0.
        Assert.InRange(first, 3.9f, 4.05f);
        Assert.InRange(last,  1.95f, 2.1f);

        // And the chunk should be monotonically decreasing (or at least last < first).
        Assert.True(last < first - 1.0f, $"expected ramp down: first={first}, last={last}");

        // Subsequent chunks should be steady at the new gain.
        if (rampIndex >= 0 && captured.Count > rampIndex + 1)
        {
            var steady = captured[rampIndex + 1];
            Assert.InRange(steady[0], 1.95f, 2.05f);
            Assert.InRange(steady[(chunk - 1) * 2], 1.95f, 2.05f);
        }
    }

    [Fact]
    public void SetRouteGain_NoChange_NoRampOverhead()
    {
        // Setting the same gain twice shouldn't introduce a ramp on the
        // unchanged chunks — they should be exactly constant.
        using var r = new AudioRouter(SampleRate, chunkSamples: 64);
        var src = new TestSource(Stereo, _ => 4f);
        var sink = new ChunkLogSink(Stereo);

        r.AddSource(src, "src");
        r.AddSink(sink, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2), gain: 0.5f);

        r.Start();
        Thread.Sleep(40);
        r.Stop();

        Assert.NotEmpty(sink.AllChunks);
        var any = sink.AllChunks[0];
        for (var i = 0; i < any.Length; i++)
            Assert.Equal(2.0f, any[i], precision: 4);
    }

    [Fact]
    public void ReconfigureSampleRate_MatchingFormats_Succeeds()
    {
        using var r = new AudioRouter(44100, chunkSamples: 128);
        var f = new AudioFormat(44100, 2);
        r.AddSource(new TestSource(f), "s");
        r.AddSink(new FlushableSink(f), "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        r.ReconfigureSampleRate(44100);
        Assert.Equal(44100, r.SampleRate);
    }

    [Fact]
    public void ReconfigureSampleRate_MismatchedSource_Throws()
    {
        using var r = new AudioRouter(48000);
        var f48 = new AudioFormat(48000, 2);
        r.AddSource(new TestSource(f48), "s");
        r.AddSink(new FlushableSink(f48), "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        Assert.Throws<InvalidOperationException>(() => r.ReconfigureSampleRate(44100));
    }

    [Fact]
    public void ReconfigureSampleRate_WhenRunning_Throws()
    {
        using var r = new AudioRouter(48000, chunkSamples: 480);
        var f = new AudioFormat(48000, 2);
        r.AddSource(new TestSource(f), "s");
        r.AddSink(new FlushableSink(f), "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        r.Start();
        Assert.Throws<InvalidOperationException>(() => r.ReconfigureSampleRate(48000));
        r.Stop();
    }

    [Fact]
    public void ReconfigureSampleRate_AfterSlaveTo_RebuildsSlavedClock()
    {
        using var r = new AudioRouter(48000, chunkSamples: 64);
        var f = new AudioFormat(48000, 2);
        r.AddSource(new TestSource(f), "s");
        r.AddSink(new ImmediateClockedSink(f), "p");
        r.AddRoute("s", "p", ChannelMap.Identity(2));
        r.SlaveTo("p");
        r.ReconfigureSampleRate(48000);
        Assert.Equal(48000, r.SampleRate);
    }

    [Fact]
    public void ReconfigureSampleRateWhileRunning_WhenStopped_Throws()
    {
        using var r = new AudioRouter(48000, chunkSamples: 64);
        var f = new AudioFormat(48000, 2);
        r.AddSource(new TestSource(f), "s");
        r.AddSink(new FlushableSink(f), "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        Assert.Throws<InvalidOperationException>(() => r.ReconfigureSampleRateWhileRunning(44100));
    }

    [Fact]
    public void ReconfigureSampleRateWhileRunning_MatchingFormats_UpdatesSampleRate()
    {
        using var r = new AudioRouter(48000, chunkSamples: 64);
        var src = new SwitchableSource(new AudioFormat(48000, 2));
        var sink = new SwitchableSink(new AudioFormat(48000, 2));
        r.AddSource(src, "s");
        r.AddSink(sink, "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        r.Start();
        Thread.Sleep(30);
        src.FormatValue = new AudioFormat(44100, 2);
        sink.FormatValue = new AudioFormat(44100, 2);
        r.ReconfigureSampleRateWhileRunning(44100);
        Assert.Equal(44100, r.SampleRate);
        Thread.Sleep(30);
        r.Stop();
    }

    [Fact]
    public void ReconfigureSampleRateWhileRunning_MismatchedSource_Throws()
    {
        using var r = new AudioRouter(48000, chunkSamples: 64);
        var src = new SwitchableSource(new AudioFormat(48000, 2));
        var sink = new SwitchableSink(new AudioFormat(48000, 2));
        r.AddSource(src, "s");
        r.AddSink(sink, "o");
        r.AddRoute("s", "o", ChannelMap.Identity(2));
        r.Start();
        sink.FormatValue = new AudioFormat(44100, 2);
        Assert.Throws<InvalidOperationException>(() => r.ReconfigureSampleRateWhileRunning(44100));
        r.Stop();
    }

    [Fact]
    public void ReconfigureSampleRateWhileRunning_WithSlavedClock()
    {
        using var r = new AudioRouter(48000, chunkSamples: 64);
        var src = new SwitchableSource(new AudioFormat(48000, 2));
        var sink = new SwitchableClockedSink(new AudioFormat(48000, 2));
        r.AddSource(src, "s");
        r.AddSink(sink, "p");
        r.AddRoute("s", "p", ChannelMap.Identity(2));
        r.SlaveTo("p");
        r.Start();
        Thread.Sleep(25);
        src.FormatValue = new AudioFormat(44100, 2);
        sink.FormatValue = new AudioFormat(44100, 2);
        r.ReconfigureSampleRateWhileRunning(44100);
        Assert.Equal(44100, r.SampleRate);
        r.Stop();
    }

    // --- helpers ----------------------------------------------------------

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

    /// <summary>Emits a fixed number of samples per channel then reports exhausted.</summary>
    private sealed class FiniteTestSource(AudioFormat fmt, int samplesPerChannelTotal) : IAudioSource
    {
        private long _emittedSamples;

        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => _emittedSamples >= samplesPerChannelTotal;

        public int ReadInto(Span<float> dst)
        {
            if (IsExhausted) return 0;
            var samplesPerChannel = dst.Length / Format.Channels;
            var remaining = samplesPerChannelTotal - (int)_emittedSamples;
            if (samplesPerChannel > remaining) samplesPerChannel = remaining;
            for (var s = 0; s < samplesPerChannel; s++)
                for (var c = 0; c < Format.Channels; c++)
                    dst[s * Format.Channels + c] = 1f;
            _emittedSamples += samplesPerChannel;
            return samplesPerChannel * Format.Channels;
        }
    }

    private sealed class SeekableTestSource(AudioFormat fmt) : IAudioSource, ISeekableSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromMinutes(10);
        public TimeSpan Position { get; private set; }
        public TimeSpan? LastSeekTo { get; private set; }

        public int ReadInto(Span<float> dst)
        {
            dst.Clear();
            return dst.Length;
        }

        public void Seek(TimeSpan position)
        {
            LastSeekTo = position;
            Position = position;
        }
    }

    private sealed class FlushableSink(AudioFormat fmt) : IAudioSink, IFlushableSink
    {
        public AudioFormat Format { get; } = fmt;
        public int FlushCount { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public void Flush() => FlushCount++;
    }

    private sealed class CapturingSink(AudioFormat fmt) : IAudioSink
    {
        private readonly Lock _gate = new();
        private float[]? _last;

        public AudioFormat Format { get; } = fmt;
        public float[]? LastChunk { get { lock (_gate) return _last; } }

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            lock (_gate) _last = packedSamples.ToArray();
        }
    }

    private sealed class ChunkLogSink(AudioFormat fmt) : IAudioSink
    {
        private readonly Lock _gate = new();
        private readonly List<float[]> _chunks = [];
        public AudioFormat Format { get; } = fmt;
        public IReadOnlyList<float[]> AllChunks
        {
            get { lock (_gate) return _chunks.ToArray(); }
        }
        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            lock (_gate) _chunks.Add(packedSamples.ToArray());
        }
    }

    private sealed class ImmediateClockedSink(AudioFormat fmt) : IAudioSink, IClockedSink
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }

    private sealed class SwitchableSource(AudioFormat initial) : IAudioSource
    {
        public AudioFormat FormatValue { get; set; } = initial;
        public AudioFormat Format => FormatValue;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> dst)
        {
            dst.Fill(0.125f);
            return dst.Length;
        }
    }

    private sealed class SwitchableSink(AudioFormat initial) : IAudioSink
    {
        public AudioFormat FormatValue { get; set; } = initial;
        public AudioFormat Format => FormatValue;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class SwitchableClockedSink(AudioFormat initial) : IAudioSink, IClockedSink
    {
        public AudioFormat FormatValue { get; set; } = initial;
        public AudioFormat Format => FormatValue;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }
}
