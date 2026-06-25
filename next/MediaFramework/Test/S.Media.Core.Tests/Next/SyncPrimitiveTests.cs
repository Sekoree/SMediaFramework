using Xunit;

namespace S.Media.Core.Tests.Next;

public class SourceTimelineTests
{
    [Fact]
    public void Scheduled_unanchored_maps_pts_plus_offset()
    {
        var t = new SourceTimeline(RebasePolicy.Scheduled, TimeSpan.FromMilliseconds(100));
        Assert.False(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(100), t.DueTime(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Anchor_ties_source_pts_to_master_and_is_invertible()
    {
        var t = new SourceTimeline(RebasePolicy.Holdback);
        t.Anchor(sourcePts: TimeSpan.FromSeconds(100), masterNow: TimeSpan.FromSeconds(2));

        Assert.True(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(2), t.DueTime(TimeSpan.FromSeconds(100)));   // anchored frame
        Assert.Equal(TimeSpan.FromSeconds(3), t.DueTime(TimeSpan.FromSeconds(101)));   // +1s sender → +1s master
        Assert.Equal(TimeSpan.FromSeconds(100), t.SourceTimeAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(101), t.SourceTimeAt(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Offset_shifts_due_time_while_anchored()
    {
        var t = new SourceTimeline(RebasePolicy.Holdback, TimeSpan.FromMilliseconds(500));
        t.Anchor(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(500), t.DueTime(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void Reset_drops_anchor()
    {
        var t = new SourceTimeline(RebasePolicy.RebaseToLatest);
        t.Anchor(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
        t.Reset();
        Assert.False(t.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(5), t.DueTime(TimeSpan.FromSeconds(5)));
    }
}

public class SessionClockTests
{
    [Fact]
    public void Now_tracks_the_reference_elapsed()
    {
        var clk = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.Zero };
        var sc = new SessionClock(clk);
        Assert.Equal(TimeSpan.Zero, sc.Now);

        clk.ElapsedSinceStart = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);
    }

    [Fact]
    public void SetReference_preserves_now_across_the_swap()
    {
        var a = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var sc = new SessionClock(a);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);

        var b = new FakePlaybackClock { ElapsedSinceStart = TimeSpan.FromSeconds(100) };
        sc.SetReference(b);
        Assert.Equal(TimeSpan.FromSeconds(3), sc.Now);          // no jump on swap

        b.ElapsedSinceStart = TimeSpan.FromSeconds(101);
        Assert.Equal(TimeSpan.FromSeconds(4), sc.Now);          // advances from the new reference
    }

    [Fact]
    public void IsAdvancing_reflects_reference()
    {
        var clk = new FakePlaybackClock { IsAdvancing = false };
        var sc = new SessionClock(clk);
        Assert.False(sc.IsAdvancing);
        clk.IsAdvancing = true;
        Assert.True(sc.IsAdvancing);
    }

    [Fact]
    public void LiveWallClock_is_advancing()
    {
        var sc = SessionClock.LiveWallClock();
        Assert.True(sc.IsAdvancing);
        Assert.True(sc.Now >= TimeSpan.Zero);
    }
}

public class SourceSyncGroupTests
{
    [Fact]
    public void Anchoring_the_group_anchors_all_members_with_their_own_offset()
    {
        var group = new SourceSyncGroup(RebasePolicy.Holdback);
        var video = group.AddStream(correctionOffset: TimeSpan.FromMilliseconds(20)); // known A/V phase
        var audio = group.AddStream();

        Assert.Equal(2, group.Members.Count);
        Assert.Equal(RebasePolicy.Holdback, video.Policy);

        group.Anchor(senderPts: TimeSpan.FromSeconds(50), masterNow: TimeSpan.FromSeconds(1));

        Assert.True(group.IsAnchored);
        Assert.True(video.IsAnchored && audio.IsAnchored);
        // Shared sender anchor; per-stream offset is the only difference (the correctable A/V phase).
        Assert.Equal(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(20), video.DueTime(TimeSpan.FromSeconds(50)));
        Assert.Equal(TimeSpan.FromSeconds(1), audio.DueTime(TimeSpan.FromSeconds(50)));
    }
}
