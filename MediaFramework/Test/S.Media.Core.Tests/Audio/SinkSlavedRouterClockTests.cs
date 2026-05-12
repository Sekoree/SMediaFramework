using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class SinkSlavedRouterClockTests
{
    [Fact]
    public void LazyFallback_WaitsWallClock_WhenSinkMissing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = new SinkSlavedRouterClock(48_000, 480, () => null);
        clock.Reset();
        Assert.True(clock.WaitForNextChunk(cts.Token));
    }

    [Fact]
    public void Ctor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SinkSlavedRouterClock(0, 480, () => null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SinkSlavedRouterClock(48_000, 0, () => null));
        Assert.Throws<ArgumentNullException>(() => new SinkSlavedRouterClock(48_000, 480, null!));
    }

    [Fact]
    public void WhenSinkPresent_DelegatesToWaitForCapacity()
    {
        var sink = new RecordingClockedSink();
        var clock = new SinkSlavedRouterClock(48_000, 256, () => sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(2, sink.Calls);
        Assert.Equal(256, sink.LastChunkSamples);
    }

    [Fact]
    public void WhenSinkTogglesBetweenPresentAndMissing_UsesSinkThenWallThenSinkAgain()
    {
        var sink = new RecordingClockedSink();
        IClockedSink? current = sink;
        // Small chunk so wall fallback sleeps only ~1.3 ms @ 48 kHz.
        var clock = new SinkSlavedRouterClock(48_000, 64, () => current);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(1, sink.Calls);

        current = null;
        Assert.True(clock.WaitForNextChunk(cts.Token));

        current = sink;
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(2, sink.Calls);
    }

    [Fact]
    public void WaitForNextChunk_PreCancelledToken_ReturnsFalse_WhenSinkMissing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var clock = new SinkSlavedRouterClock(48_000, 480, () => null);
        Assert.False(clock.WaitForNextChunk(cts.Token));
    }

    [Fact]
    public void WaitForNextChunk_PreCancelledToken_ReturnsFalse_WhenSinkHonoursToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new CancelHonouringSink();
        var clock = new SinkSlavedRouterClock(48_000, 480, () => sink);
        Assert.False(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public void Reset_WhenSinkMissing_InitialisesLazyFallbackForSubsequentWaits()
    {
        var clock = new SinkSlavedRouterClock(48_000, 480, () => null);
        clock.Reset();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
    }

    private sealed class RecordingClockedSink : IClockedSink
    {
        public int Calls;
        public int LastChunkSamples;

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            LastChunkSamples = chunkSamples;
            Interlocked.Increment(ref Calls);
            return !token.IsCancellationRequested;
        }
    }

    private sealed class CancelHonouringSink : IClockedSink
    {
        public int Calls;

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return !token.IsCancellationRequested;
        }
    }
}
