using S.Media.Core.Audio;
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
}
