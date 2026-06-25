using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterPumpLifecycleTests
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void OutputAddedBeforeStart_ReceivesAudio_AfterStart()
    {
        // Lazy pump-thread start (P2-1): the drainer Thread is created idle at AddOutput time
        // and only .Start()ed when the router runs (avoids one OS thread per output for routers
        // that never start — the suite-level thread-pressure / OOM source). If Start() forgets to
        // EnsureStarted the registered pumps, committed chunks never reach the output. Guard it.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var output = new RecordingOutput(Stereo);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "output added before Start should receive audio once the router runs");
        r.Stop();
    }

    [Fact]
    public void OutputAddedWhileRunning_ReceivesAudio()
    {
        // The other EnsureStarted call site: an output added to an already-running router must
        // start its drainer immediately, otherwise it silently never drains.
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.Start();
        Thread.Sleep(30);

        var output = new RecordingOutput(Stereo);
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "output added while running should start draining immediately");
        r.Stop();
    }

    [Fact]
    public void ClockedOutputAddedWhileRunning_DoesNotThrow_AndReceivesAudio()
    {
        // Regression: hot-wiring an audio device (PortAudio is IClockedOutput + IPlaybackClock) into a
        // running router used to throw "cannot slave clock while router is running" — AutoWirePrimary
        // tried to promote the first clocked output to pacing primary mid-stream. A running router must
        // instead keep the new clocked output as a non-primary slave (no mid-stream re-clock).
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        // Start with only a non-clocked sink routed (the "video playing, no audio output" case: the
        // framework wires a discard sink so the source is consumed and the router runs at wall clock).
        r.AddOutput(new RecordingOutput(Stereo), "discard");
        r.AddRoute("src", "discard", ChannelMap.Identity(2));
        r.Start();
        Thread.Sleep(30);
        Assert.True(r.IsRunning);

        var clocked = new ClockedRecordingOutput(Stereo);
        var ex = Record.Exception(() =>
        {
            var id = r.AddOutput(clocked, "device");
            r.AddRoute("src", id, ChannelMap.Identity(2));
        });

        Assert.Null(ex);
        Assert.True(SpinUntil(() => clocked.SubmitCount > 0, 2000),
            "a clocked output hot-wired into a running router should still receive audio");
        // The hot-wired clocked output must NOT have hijacked the router as the pacing primary.
        Assert.Null(r.PrimaryOutputId);
        r.Stop();
    }

    [Fact]
    public void OutputAddedBeforeStart_DisposeBeforeStart_DoesNotThrow()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(new RecordingOutput(Stereo), "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        var ex = Record.Exception(r.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void NormalPumpLifecycle_DoesNotReportStuck()
    {
        using var r = new AudioRouter(SampleRate, chunkSamples: 480);
        var output = new RecordingOutput(Stereo);
        r.AddSource(new SilenceSource(Stereo), "src");
        r.AddOutput(output, "out");
        r.AddRoute("src", "out", ChannelMap.Identity(2));

        r.Start();
        Assert.True(SpinUntil(() => output.SubmitCount > 0, 2000),
            "normal pump should process at least one chunk");
        r.Stop();

        Assert.False(r.GetPumpStats("out").IsStuck);
        Assert.Empty(r.StuckOutputPumpIds);
    }

    [Fact]
    public async Task Dispose_WithOutputBlockedInSubmit_ReturnsWithoutDisposingLivePumpState()
    {
        var output = new BlockingOutput(Stereo);
        var router = new AudioRouter(SampleRate, chunkSamples: 64);
        router.AddSource(new SilenceSource(Stereo), "src");
        router.AddOutput(output, "out");
        router.AddRoute("src", "out", ChannelMap.Identity(2));
        Task? disposeTask = null;

        try
        {
            router.Start();
            Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(2)), "output pump should be blocked inside Submit");

            disposeTask = Task.Run(router.Dispose);
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(7))) == disposeTask;

            Assert.True(completed,
                "router dispose should return after bounded pump join attempts even when Submit remains blocked");
            await disposeTask;
            Assert.Contains("out", router.StuckOutputPumpIds);
        }
        finally
        {
            output.Release();
            router.Dispose();
            if (disposeTask is { IsCompleted: false })
                await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    private sealed class SilenceSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
    }

    private sealed class RecordingOutput(AudioFormat fmt) : IAudioOutput
    {
        private int _submits;
        public int SubmitCount => Volatile.Read(ref _submits);
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref _submits);
    }

    /// <summary>Models a hardware device: clocked (paces the router when promoted) and exposes a playback
    /// clock, exactly like <c>PortAudioOutput</c>. Always ready to accept a chunk.</summary>
    private sealed class ClockedRecordingOutput(AudioFormat fmt) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        private int _submits;
        public int SubmitCount => Volatile.Read(ref _submits);
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref _submits);
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => true;
    }

    private sealed class BlockingOutput(AudioFormat fmt) : IAudioOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public AudioFormat Format { get; } = fmt;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            Entered.Set();
            _release.Wait();
        }

        public void Release() => _release.Set();
    }
}
