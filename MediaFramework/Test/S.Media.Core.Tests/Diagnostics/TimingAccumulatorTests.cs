using System.Diagnostics;
using S.Media.Core.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Diagnostics;

public sealed class TimingAccumulatorTests
{
    private static long Ms(double ms) => (long)(ms / 1000d * Stopwatch.Frequency);

    [Fact]
    public void Empty_SnapshotIsZero()
    {
        var acc = new TimingAccumulator();
        var snap = acc.Snapshot();

        Assert.Equal(0, snap.Count);
        Assert.Equal(0d, snap.AvgMs);
        Assert.Equal(0d, snap.MaxMs);
        Assert.Equal(0d, snap.LastMs);
    }

    [Fact]
    public void Record_TracksCountAvgMaxLast()
    {
        var acc = new TimingAccumulator();
        acc.Record(Ms(10));
        acc.Record(Ms(30));
        acc.Record(Ms(20));

        var snap = acc.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal(20d, snap.AvgMs, 1);
        Assert.Equal(30d, snap.MaxMs, 1);
        Assert.Equal(20d, snap.LastMs, 1);
    }

    [Fact]
    public void Record_NegativeClampsToZero()
    {
        var acc = new TimingAccumulator();
        acc.Record(-1234);

        var snap = acc.Snapshot();
        Assert.Equal(1, snap.Count);
        Assert.Equal(0d, snap.AvgMs);
    }

    [Fact]
    public void Record_TimeSpanOverload_MatchesStopwatchUnits()
    {
        var acc = new TimingAccumulator();
        acc.Record(TimeSpan.FromMilliseconds(25));

        var snap = acc.Snapshot();
        Assert.Equal(25d, snap.LastMs, 1);
    }

    [Fact]
    public void WindowAvgMs_DiffsConsecutiveSnapshots()
    {
        var acc = new TimingAccumulator();
        acc.Record(Ms(100));
        var first = acc.Snapshot();

        acc.Record(Ms(10));
        acc.Record(Ms(20));
        var second = acc.Snapshot();

        // The window between the snapshots saw two samples averaging 15 ms; the lifetime avg is ~43 ms.
        Assert.Equal(15d, second.WindowAvgMs(first), 1);
        Assert.Equal(0d, second.WindowAvgMs(second));
    }

    [Fact]
    public void Record_Concurrent_CountsEverySample()
    {
        var acc = new TimingAccumulator();
        const int threads = 4;
        const int perThread = 10_000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                acc.Record(Ms(1));
        });

        var snap = acc.Snapshot();
        Assert.Equal(threads * perThread, snap.Count);
        Assert.Equal(1d, snap.AvgMs, 1);
        Assert.Equal(1d, snap.MaxMs, 1);
    }
}
