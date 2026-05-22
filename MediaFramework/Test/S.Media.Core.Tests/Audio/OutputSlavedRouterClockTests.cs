using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class OutputSlavedRouterClockTests
{
    [Fact]
    public void LazyFallback_WaitsWallClock_WhenOutputMissing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = new OutputSlavedRouterClock(48_000, 480, () => null);
        clock.Reset();
        Assert.True(clock.WaitForNextChunk(cts.Token));
    }

    [Fact]
    public void Ctor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputSlavedRouterClock(0, 480, () => null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputSlavedRouterClock(48_000, 0, () => null));
        Assert.Throws<ArgumentNullException>(() => new OutputSlavedRouterClock(48_000, 480, null!));
    }

    [Fact]
    public void WhenOutputPresent_DelegatesToWaitForCapacity()
    {
        var output = new RecordingClockedOutput();
        var clock = new OutputSlavedRouterClock(48_000, 256, () => output);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(2, output.Calls);
        Assert.Equal(256, output.LastChunkSamples);
    }

    [Fact]
    public void WhenOutputTogglesBetweenPresentAndMissing_UsesOutputThenWallThenOutputAgain()
    {
        var output = new RecordingClockedOutput();
        IClockedOutput? current = output;
        // Small chunk so wall fallback sleeps only ~1.3 ms @ 48 kHz.
        var clock = new OutputSlavedRouterClock(48_000, 64, () => current);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(1, output.Calls);

        current = null;
        Assert.True(clock.WaitForNextChunk(cts.Token));

        current = output;
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(2, output.Calls);
    }

    [Fact]
    public void WaitForNextChunk_PreCancelledToken_ReturnsFalse_WhenOutputMissing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var clock = new OutputSlavedRouterClock(48_000, 480, () => null);
        Assert.False(clock.WaitForNextChunk(cts.Token));
    }

    [Fact]
    public void WaitForNextChunk_PreCancelledToken_ReturnsFalse_WhenOutputHonoursToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = new CancelHonouringOutput();
        var clock = new OutputSlavedRouterClock(48_000, 480, () => output);
        Assert.False(clock.WaitForNextChunk(cts.Token));
        Assert.Equal(1, output.Calls);
    }

    [Fact]
    public void Reset_WhenOutputMissing_InitialisesLazyFallbackForSubsequentWaits()
    {
        var clock = new OutputSlavedRouterClock(48_000, 480, () => null);
        clock.Reset();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
    }

    private sealed class RecordingClockedOutput : IClockedOutput
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

    private sealed class CancelHonouringOutput : IClockedOutput
    {
        public int Calls;

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return !token.IsCancellationRequested;
        }
    }
}
