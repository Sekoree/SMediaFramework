using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class MediaClockMasterTests
{
    [Fact]
    public void NoMaster_FallsBackToStopwatchBehavior()
    {
        using var clock = new MediaClock();
        Assert.Null(clock.Master);
        clock.Start();
        Thread.Sleep(50);
        Assert.True(clock.CurrentPosition > TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void WithMaster_PositionTracksMasterElapsed()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        // Master advances by 250 ms.
        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.250);
        // Position = baseline (0) + (10.250 - anchor 10) = 0.250.
        var pos = clock.CurrentPosition;
        Assert.InRange(pos.TotalMilliseconds, 240, 260);
    }

    [Fact]
    public void WithMaster_StopwatchDriftDoesNotAffectPosition()
    {
        // Stopwatch ticks would say "100ms passed"; master says zero. Master wins.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.Zero, IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        Thread.Sleep(100); // wall time elapses but master is frozen
        Assert.Equal(TimeSpan.Zero, clock.CurrentPosition);
    }

    [Fact]
    public void Seek_WithMaster_ReAnchorsCleanly()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(5), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        clock.Seek(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(2), clock.CurrentPosition);

        // Master advances by 100 ms; position should advance by the same.
        master.ElapsedSinceStart = TimeSpan.FromSeconds(5.1);
        var pos = clock.CurrentPosition;
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(100), pos);
    }

    [Fact]
    public void SetMaster_MidRun_PositionDoesNotJump()
    {
        using var clock = new MediaClock();
        clock.Start();
        Thread.Sleep(100);

        var preSwap = clock.CurrentPosition;
        // Attach a master with a high elapsed value — naive math would jump
        // forward by years. The swap should re-anchor to keep continuity.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromHours(1), IsAdvancing = true };
        clock.SetMaster(master);

        var postSwap = clock.CurrentPosition;
        Assert.InRange((postSwap - preSwap).Duration().TotalMilliseconds, 0, 5);
    }

    [Fact]
    public void DetachMaster_ResumesStopwatchPacing()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        clock.SetMaster(null);
        var beforeSleep = clock.CurrentPosition;
        Thread.Sleep(80);
        var afterSleep = clock.CurrentPosition;

        // Stopwatch took over — position advanced even though master is frozen.
        Assert.True(afterSleep > beforeSleep + TimeSpan.FromMilliseconds(40),
            $"position should advance via stopwatch after detach (before {beforeSleep}, after {afterSleep})");
    }

    [Fact]
    public void Pause_FreezesPositionEvenIfMasterAdvances()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(0), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(200);
        Thread.Sleep(20); // let driver pick up the change
        clock.Pause();
        var paused = clock.CurrentPosition;

        // Master keeps advancing while paused.
        master.ElapsedSinceStart = TimeSpan.FromSeconds(60);
        Thread.Sleep(20);
        Assert.Equal(paused, clock.CurrentPosition);
    }

    [Fact]
    public void Resume_AfterPause_IncludesMasterDriftWhilePaused()
    {
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(0), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromMilliseconds(150);
        clock.Pause();
        var paused = clock.CurrentPosition;

        // Master jumps ahead while paused (real audio sink kept playing).
        master.ElapsedSinceStart = TimeSpan.FromSeconds(30);
        clock.Start();

        // Right after resume, position includes elapsed master time accrued while paused
        // so the timeline stays aligned with audio that continued in the sink buffer.
        var afterResume = clock.CurrentPosition;
        Assert.True(afterResume > paused + TimeSpan.FromSeconds(20));

        master.ElapsedSinceStart = TimeSpan.FromSeconds(30.1);
        var advanced = clock.CurrentPosition;
        Assert.InRange((advanced - afterResume).TotalMilliseconds, 95, 110);
    }

    [Fact]
    public void Master_StartedHoursBeforeAttachment_DoesNotJumpPosition()
    {
        // The user explicitly worried about this: if MediaClock has been
        // running for a while and a master (e.g. NDIClock that's been counting
        // for hours) is then attached, the absolute master.ElapsedSinceStart
        // value must NOT contaminate the apparent position.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromHours(3), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.Start();
        Thread.Sleep(80);

        var beforeAttach = clock.CurrentPosition;
        clock.SetMaster(master);
        var afterAttach = clock.CurrentPosition;

        // Continuity: the swap should not jump even though master shows 3 hours.
        Assert.InRange((afterAttach - beforeAttach).Duration().TotalMilliseconds, 0, 5);

        // Subsequent advances track master deltas only.
        master.ElapsedSinceStart = TimeSpan.FromHours(3) + TimeSpan.FromMilliseconds(250);
        var pos = clock.CurrentPosition;
        var expected = beforeAttach + TimeSpan.FromMilliseconds(250);
        Assert.InRange((pos - expected).Duration().TotalMilliseconds, 0, 5);
    }

    [Fact]
    public void Master_GoingBackwards_DoesNotMoveBackwards()
    {
        // Defensive: a buggy master shouldn't be able to drag position
        // backwards.
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(10), IsAdvancing = true };
        using var clock = new MediaClock();
        clock.SetMaster(master);
        clock.Start();

        master.ElapsedSinceStart = TimeSpan.FromSeconds(10.5);
        var p1 = clock.CurrentPosition;

        master.ElapsedSinceStart = TimeSpan.FromSeconds(9); // illegal regression
        var p2 = clock.CurrentPosition;

        Assert.True(p2 <= p1, "position should clamp instead of moving backwards");
    }

    [Fact]
    public void SetMasterChain_SelectsHighestPriorityAdvancingMaster()
    {
        IMediaClock clock = new MediaClock();
        var low = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(1), IsAdvancing = true };
        var high = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(9), IsAdvancing = true };
        clock.SetMasterChain(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));
        clock.Start();

        Assert.Equal(TimeSpan.Zero, clock.CurrentPosition);
        high.ElapsedSinceStart = TimeSpan.FromSeconds(10);
        low.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.CurrentPosition);
    }

    [Fact]
    public void SetMasterChain_NoArgs_DetachesMaster()
    {
        using var clock = new MediaClock();
        var master = new FakeClock { ElapsedSinceStart = TimeSpan.Zero, IsAdvancing = true };
        clock.SetMaster(master);
        Assert.NotNull(clock.Master);
        clock.SetMasterChain();
        Assert.Null(clock.Master);
    }

    [Fact]
    public void SetMasterChain_WhenOnlyLowerPriorityAdvancing_UsesThatMaster()
    {
        IMediaClock clock = new MediaClock();
        var primary = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(5), IsAdvancing = false };
        var fallback = new FakeClock { ElapsedSinceStart = TimeSpan.FromSeconds(2), IsAdvancing = true };
        clock.SetMasterChain(
            new PlaybackClockCandidate(fallback, 1),
            new PlaybackClockCandidate(primary, 100));
        clock.Start();

        Assert.Equal(TimeSpan.Zero, clock.CurrentPosition);
        fallback.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.CurrentPosition);
    }

    private sealed class FakeClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; set; }
        public bool IsAdvancing { get; set; } = true;
    }
}
