using Xunit;

namespace S.Media.Core.Tests.Next;

public class LiveTimelineDriverTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Scheduled_is_identity_and_never_anchors()
    {
        // File path: PTS already on master time; driving it is a harmless no-op (pts + offset).
        var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.Scheduled, 100 * Ms));

        var p = driver.Place(senderPts: TimeSpan.FromSeconds(5), masterNow: TimeSpan.FromSeconds(9));

        Assert.False(p.ReAnchored);
        Assert.False(driver.Timeline.IsAnchored);
        Assert.Equal(TimeSpan.FromSeconds(5) + 100 * Ms, p.DueMaster);
    }

    [Fact]
    public void Holdback_anchors_first_frame_to_master_now()
    {
        var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.Holdback));

        // Sender clock is unrelated to master: first frame (sender 100s) lands at master-now.
        var first = driver.Place(senderPts: TimeSpan.FromSeconds(100), masterNow: TimeSpan.FromSeconds(2));

        Assert.True(first.ReAnchored);
        Assert.Equal(TimeSpan.FromSeconds(2), first.DueMaster);
        Assert.Equal(1, driver.ReAnchorCount);
    }

    [Fact]
    public void Holdback_subsequent_frames_advance_with_sender_delta_no_reanchor()
    {
        var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.Holdback));
        driver.Place(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2)); // anchor: sender 100 ↔ master 2

        // +40 ms of sender time → +40 ms of master due-time. Holdback never auto-collapses.
        var p = driver.Place(TimeSpan.FromSeconds(100) + 40 * Ms, TimeSpan.FromSeconds(2) + 39 * Ms);

        Assert.False(p.ReAnchored);
        Assert.Equal(TimeSpan.FromSeconds(2) + 40 * Ms, p.DueMaster);
        Assert.Equal(1, driver.ReAnchorCount);
    }

    [Fact]
    public void Holdback_per_stream_offset_trims_phase()
    {
        // Offset is the first-class A/V phase trim (replaces the old HAPLAY_LIVE_AV_SYNC_OFFSET_MS hack).
        var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.Holdback, offset: -16 * Ms));
        driver.Place(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(0)); // anchor

        var p = driver.Place(TimeSpan.FromSeconds(10) + 100 * Ms, TimeSpan.FromSeconds(0) + 100 * Ms);

        // due = (pts - anchorSrc) + anchorMaster + offset = 100ms + 0 + (-16ms)
        Assert.Equal(100 * Ms - 16 * Ms, p.DueMaster);
    }

    [Fact]
    public void RebaseToLatest_collapses_drift_beyond_threshold()
    {
        var driver = new LiveTimelineDriver(
            new SourceTimeline(RebasePolicy.RebaseToLatest), driftCollapseThreshold: 200 * Ms);
        driver.Place(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(0)); // anchor: sender 50 ↔ master 0

        // Sender ran 1s but master only advanced 600ms → due would be 1000ms vs master 600ms = 400ms drift.
        var collapsed = driver.Place(TimeSpan.FromSeconds(51), TimeSpan.FromSeconds(0) + 600 * Ms);

        Assert.True(collapsed.ReAnchored);                       // snapped back to now
        Assert.Equal(600 * Ms, collapsed.DueMaster);
        Assert.Equal(2, driver.ReAnchorCount);                   // warm-up + this collapse
    }

    [Fact]
    public void RebaseToLatest_within_threshold_does_not_collapse()
    {
        var driver = new LiveTimelineDriver(
            new SourceTimeline(RebasePolicy.RebaseToLatest), driftCollapseThreshold: 200 * Ms);
        driver.Place(TimeSpan.FromSeconds(50), TimeSpan.Zero);

        // 100ms drift < 200ms threshold → keep scheduling against the existing anchor.
        var p = driver.Place(TimeSpan.FromSeconds(50) + 500 * Ms, 400 * Ms);

        Assert.False(p.ReAnchored);
        Assert.Equal(500 * Ms, p.DueMaster);
        Assert.Equal(1, driver.ReAnchorCount);
    }

    [Fact]
    public void SyncGroup_keeps_correlated_streams_in_sender_relationship()
    {
        // NDI video + audio share one sender→master anchor; only their per-stream offsets differ.
        var group = new SourceSyncGroup(RebasePolicy.Holdback);
        var video = group.AddStream(correctionOffset: TimeSpan.Zero);
        var audio = group.AddStream(correctionOffset: -20 * Ms);   // audio leads video by 20ms at the sender

        group.Anchor(senderPts: TimeSpan.FromSeconds(80), masterNow: TimeSpan.FromSeconds(5));

        // A frame from each stream at the same sender instant keeps its 20ms relationship on master.
        var sender = TimeSpan.FromSeconds(80) + 250 * Ms;
        Assert.Equal(TimeSpan.FromSeconds(5) + 250 * Ms, video.DueTime(sender));
        Assert.Equal(TimeSpan.FromSeconds(5) + 250 * Ms - 20 * Ms, audio.DueTime(sender));
    }

    [Fact]
    public void Reset_drops_anchor_so_next_frame_reanchors()
    {
        var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.Holdback));
        driver.Place(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
        Assert.True(driver.Timeline.IsAnchored);

        driver.Reset();
        Assert.False(driver.Timeline.IsAnchored);
        Assert.Equal(0, driver.PlacedCount);

        var p = driver.Place(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(7));
        Assert.True(p.ReAnchored);
        Assert.Equal(TimeSpan.FromSeconds(7), p.DueMaster);
    }

    [Fact]
    public void NonPositive_drift_threshold_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveTimelineDriver(new SourceTimeline(RebasePolicy.RebaseToLatest), TimeSpan.Zero));
    }
}
