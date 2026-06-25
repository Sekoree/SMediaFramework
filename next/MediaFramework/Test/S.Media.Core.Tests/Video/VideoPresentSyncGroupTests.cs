using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoPresentSyncGroupTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // ---- scheduler policy (fake members) -----------------------------------

    [Fact]
    public void All_members_ready_present_in_lockstep_at_the_oldest_ready_pts()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head);
        var a = new FakeMember { ReadyPts = Ms(100) };
        var b = new FakeMember { ReadyPts = Ms(66) };   // b is one frame behind a
        group.AddMember(a);
        group.AddMember(b);

        var result = group.Tick();

        // Group presents at the oldest of the two ready PTS so the ahead member can't pull further ahead.
        Assert.True(result.Presented);
        Assert.Equal(Ms(66), result.GroupTargetPts);
        Assert.Equal([Ms(66)], b.Presented);     // b advanced to the common point
        Assert.Empty(a.Presented);               // a held its newer frame (PresentUpTo(66) was a no-op)
        Assert.Equal(1, result.PresentedMembers);
        Assert.False(result.HeldForLaggingMember);
    }

    [Fact]
    public void Holds_when_a_member_is_behind_so_ready_members_do_not_tear_ahead()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head);
        var ready = new FakeMember { ReadyPts = Ms(100) };
        var behind = new FakeMember { ReadyPts = null };   // no unpresented frame due
        group.AddMember(ready);
        group.AddMember(behind);

        var result = group.Tick();

        Assert.False(result.Presented);
        Assert.True(result.HeldForLaggingMember);
        Assert.Equal(1, result.LaggingMembers);
        Assert.Equal(0, ready.PresentCalls);     // ready member held — no present issued at all
    }

    [Fact]
    public void Degrades_to_presenting_ready_members_after_the_starve_budget()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head, new VideoPresentSyncGroupOptions { MaxStarveHoldTicks = 3 });
        var ready = new FakeMember { ReadyPts = Ms(100) };
        var wedged = new FakeMember { ReadyPts = null };
        group.AddMember(ready);
        group.AddMember(wedged);

        for (var i = 0; i < 3; i++)
            Assert.True(group.Tick().HeldForLaggingMember);   // holds while within budget

        var afterBudget = group.Tick();                        // 4th consecutive partial tick

        Assert.True(afterBudget.Presented);                    // give up waiting for the wedged member
        Assert.False(afterBudget.HeldForLaggingMember);
        Assert.Equal([Ms(100)], ready.Presented);              // the live member keeps the wall moving
    }

    [Fact]
    public void No_member_ready_is_a_quiet_hold_and_resets_the_starve_counter()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head, new VideoPresentSyncGroupOptions { MaxStarveHoldTicks = 1 });
        var a = new FakeMember { ReadyPts = null };
        var b = new FakeMember { ReadyPts = null };
        group.AddMember(a);
        group.AddMember(b);

        var result = group.Tick();

        Assert.False(result.Presented);
        Assert.False(result.HeldForLaggingMember);   // nobody due to advance — normal between-frames, not a lag hold
        Assert.Equal(0, result.LaggingMembers);
        Assert.Equal(0, a.PresentCalls);
    }

    [Fact]
    public void A_quiet_tick_clears_a_pending_starve_hold()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head, new VideoPresentSyncGroupOptions { MaxStarveHoldTicks = 3 });
        var ready = new FakeMember { ReadyPts = Ms(100) };
        var behind = new FakeMember { ReadyPts = null };
        group.AddMember(ready);
        group.AddMember(behind);

        Assert.True(group.Tick().HeldForLaggingMember);   // 1 hold accrued

        // Both go quiet (no new frames due) — resets the counter, so the budget starts fresh afterwards.
        ready.ReadyPts = null;
        Assert.False(group.Tick().HeldForLaggingMember);

        ready.ReadyPts = Ms(100);
        // Budget is 3 again from zero, so this is a hold, not an immediate degrade.
        Assert.True(group.Tick().HeldForLaggingMember);
    }

    [Fact]
    public void Paused_reference_presents_nothing()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100), IsRunning = false };
        using var group = new VideoPresentSyncGroup(head);
        var a = new FakeMember { ReadyPts = Ms(100) };
        group.AddMember(a);

        var result = group.Tick();

        Assert.False(result.Presented);
        Assert.Equal(0, a.PresentCalls);
    }

    [Fact]
    public void Non_presentable_members_are_excluded_from_the_lockstep_decision()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(100) };
        using var group = new VideoPresentSyncGroup(head);
        var live = new FakeMember { ReadyPts = Ms(100) };
        var offline = new FakeMember { ReadyPts = null, IsPresentable = false };
        group.AddMember(live);
        group.AddMember(offline);

        var result = group.Tick();

        // The offline member doesn't count as "behind" — the live member presents normally.
        Assert.True(result.Presented);
        Assert.Equal([Ms(100)], live.Presented);
        Assert.Equal(0, result.LaggingMembers);
    }

    // ---- concrete member: SyncPresentVideoOutput ---------------------------

    [Fact]
    public void Member_presents_newest_due_frame_and_drops_skipped_older_ones()
    {
        var inner = new RecordingOutput();
        using var member = new SyncPresentVideoOutput(inner);
        member.Configure(VideoFormatFor());

        member.Submit(Frame(Ms(0)));
        member.Submit(Frame(Ms(33)));
        member.Submit(Frame(Ms(66)));

        var outcome = member.PresentUpTo(Ms(70));

        Assert.Equal(VideoSyncPresentOutcome.Presented, outcome);
        Assert.Equal([Ms(66)], inner.Submitted);     // newest due frame presented
        Assert.Equal(2, member.DroppedFrames);        // the two older frames were dropped
        Assert.Equal(0, member.BufferedFrameCount);
    }

    [Fact]
    public void Member_keeps_future_frames_buffered_and_holds_when_nothing_is_due()
    {
        var inner = new RecordingOutput();
        using var member = new SyncPresentVideoOutput(inner);
        member.Configure(VideoFormatFor());
        member.Submit(Frame(Ms(33)));
        member.Submit(Frame(Ms(66)));

        // Present up to 40 ms: only the 33 ms frame is due; the 66 ms frame stays buffered.
        Assert.Equal(VideoSyncPresentOutcome.Presented, member.PresentUpTo(Ms(40)));
        Assert.Equal([Ms(33)], inner.Submitted);
        Assert.Equal(1, member.BufferedFrameCount);

        Assert.True(member.TryPeekReadyPts(Ms(66), out var pts));
        Assert.Equal(Ms(66), pts);

        // Nothing newer is due before 66 ms -> hold (device keeps the 33 ms frame).
        Assert.Equal(VideoSyncPresentOutcome.NoChange, member.PresentUpTo(Ms(50)));
        Assert.Single(inner.Submitted);
    }

    [Fact]
    public void Member_drops_oldest_on_capacity_overflow()
    {
        var inner = new RecordingOutput();
        using var member = new SyncPresentVideoOutput(inner, maxBufferedFrames: 2);
        member.Configure(VideoFormatFor());

        member.Submit(Frame(Ms(0)));
        member.Submit(Frame(Ms(33)));
        member.Submit(Frame(Ms(66)));   // overflows -> drops the 0 ms frame

        Assert.Equal(1, member.DroppedFrames);
        Assert.Equal(2, member.BufferedFrameCount);
        member.PresentUpTo(Ms(100));
        Assert.Equal([Ms(66)], inner.Submitted);   // 33 dropped as older during catch-up, 66 presented
    }

    [Fact]
    public void Member_drops_a_stale_frame_submitted_after_a_present()
    {
        var inner = new RecordingOutput();
        using var member = new SyncPresentVideoOutput(inner);
        member.Configure(VideoFormatFor());
        member.Submit(Frame(Ms(66)));
        member.PresentUpTo(Ms(70));
        Assert.Equal([Ms(66)], inner.Submitted);

        // A late out-of-order frame at/older than what we already presented must never be presented.
        member.Submit(Frame(Ms(33)));
        Assert.Equal(0, member.BufferedFrameCount);
        Assert.Equal(VideoSyncPresentOutcome.NoChange, member.PresentUpTo(Ms(200)));
        Assert.Single(inner.Submitted);
    }

    [Fact]
    public void Two_real_members_present_the_same_frame_through_the_group()
    {
        var head = new FakePlayhead { CurrentPosition = Ms(66) };
        using var group = new VideoPresentSyncGroup(head);
        var innerA = new RecordingOutput();
        var innerB = new RecordingOutput();
        using var a = new SyncPresentVideoOutput(innerA);
        using var b = new SyncPresentVideoOutput(innerB);
        a.Configure(VideoFormatFor());
        b.Configure(VideoFormatFor());
        group.AddMember(a);
        group.AddMember(b);

        // Both fed the same canvas PTS sequence.
        foreach (var pts in new[] { Ms(0), Ms(33), Ms(66) })
        {
            a.Submit(Frame(pts));
            b.Submit(Frame(pts));
        }

        var result = group.Tick();

        Assert.True(result.Presented);
        Assert.Equal(Ms(66), result.GroupTargetPts);
        Assert.Equal([Ms(66)], innerA.Submitted);
        Assert.Equal([Ms(66)], innerB.Submitted);   // identical frame on both outputs, same tick
    }

    // ---- fakes -------------------------------------------------------------

    private static VideoFormat VideoFormatFor() => new(1, 1, PixelFormat.Bgra32, new Rational(30, 1));

    private static VideoFrame Frame(TimeSpan pts) => new(pts, VideoFormatFor(), new byte[4], 4);

    private sealed class FakePlayhead : IReadOnlyPlayhead
    {
        public TimeSpan CurrentPosition { get; set; }
        public bool IsRunning { get; set; } = true;
        public double PlaybackRate => 1.0;
    }

    private sealed class FakeMember : ISyncPresentableVideoOutput
    {
        public bool IsPresentable { get; set; } = true;
        public TimeSpan? ReadyPts { get; set; }
        public int PresentCalls { get; private set; }
        public List<TimeSpan> Presented { get; } = [];

        public bool TryPeekReadyPts(TimeSpan target, out TimeSpan readyPts)
        {
            if (ReadyPts is { } p && p <= target) { readyPts = p; return true; }
            readyPts = default;
            return false;
        }

        public VideoSyncPresentOutcome PresentUpTo(TimeSpan target)
        {
            PresentCalls++;
            if (ReadyPts is { } p && p <= target)
            {
                Presented.Add(p);
                ReadyPts = null;
                return VideoSyncPresentOutcome.Presented;
            }
            return VideoSyncPresentOutcome.NoChange;
        }
    }

    private sealed class RecordingOutput : IVideoOutput
    {
        public List<TimeSpan> Submitted { get; } = [];
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            Submitted.Add(frame.PresentationTime);
            frame.Dispose();
        }
    }
}
