using Xunit;

namespace S.Media.Core.Tests.Clock;

/// <summary>
/// Phase 13 allocation contract: shipped <see cref="IPlaybackClock"/> reads must not allocate per call on the hot path.
/// </summary>
public sealed class PlaybackClockAllocationTests
{
    private const int WarmupIterations = 200;
    private const int MeasuredIterations = 2000;

    [Fact]
    public void MediaClock_CurrentPosition_withMaster_does_not_allocate_per_read()
    {
        using var clock = new MediaClock();
        var master = new StubPlaybackClock(advancing: true, TimeSpan.FromSeconds(2));
        clock.SetMaster(master);
        clock.Start();
        AssertNoAllocationOnHotPath(() => _ = clock.CurrentPosition);
    }

    [Fact]
    public void CompositePlaybackClock_ElapsedSinceStart_does_not_allocate_per_read()
    {
        var inner = new StubPlaybackClock(advancing: true, TimeSpan.FromSeconds(4));
        var composite = new CompositePlaybackClock(new PlaybackClockCandidate(inner, 1));
        AssertNoAllocationOnHotPath(() =>
        {
            _ = composite.ElapsedSinceStart;
            _ = composite.IsAdvancing;
        });
    }

    [Fact]
    public void VideoPtsClock_ElapsedSinceStart_does_not_allocate_per_read()
    {
        var pts = new VideoPtsClock();
        pts.NotifyFramePts(TimeSpan.FromMilliseconds(40));
        pts.Resume();
        AssertNoAllocationOnHotPath(() =>
        {
            _ = pts.ElapsedSinceStart;
            _ = pts.IsAdvancing;
        });
    }

    private static void AssertNoAllocationOnHotPath(Action read)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        for (var i = 0; i < WarmupIterations; i++)
            read();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredIterations; i++)
            read();
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    private sealed class StubPlaybackClock(bool advancing, TimeSpan elapsed) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => elapsed;
        public bool IsAdvancing => advancing;
    }
}
