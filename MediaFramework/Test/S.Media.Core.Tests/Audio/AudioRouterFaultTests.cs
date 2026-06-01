using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterFaultTests
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void RunLoop_SourceThrows_RouterFaultsInsteadOfCrashing()
    {
        // Regression: a source throwing from ReadInto used to rethrow on the background router thread
        // and crash the host. The router must instead transition to a faulted, stopped state and
        // surface the error via Fault / Faulted.
        using var router = new AudioRouter(SampleRate);
        router.AddSource(new ThrowingSource(Stereo), "bad");
        router.AddOutput(new NullOutput(Stereo), "out");
        router.Connect("bad", "out");

        Exception? faulted = null;
        using var faultRaised = new ManualResetEventSlim(false);
        router.Faulted += (_, e) => { faulted = e.Exception; faultRaised.Set(); };

        router.Start();

        Assert.True(faultRaised.Wait(TimeSpan.FromSeconds(2)), "router should raise Faulted when a source throws");
        Assert.IsType<InvalidOperationException>(faulted);
        Assert.NotNull(router.Fault);

        // The router stopped itself rather than crashing the process.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (router.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(2))
            Thread.Sleep(5);
        Assert.False(router.IsRunning);
    }

    private sealed class ThrowingSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) => throw new InvalidOperationException("source boom");
    }

    private sealed class NullOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }
}
