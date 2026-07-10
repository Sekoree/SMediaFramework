namespace S.Media.Time;

/// <summary>Result of placing one live frame onto the master timeline.</summary>
/// <param name="DueMaster">Master time at which the frame should present.</param>
/// <param name="ReAnchored">
/// True if the timeline (re-)anchored on this call - the first frame, or a
/// <see cref="RebasePolicy.RebaseToLatest"/> drift collapse.
/// </param>
public readonly record struct LiveFramePlacement(TimeSpan DueMaster, bool ReAnchored);

/// <summary>
/// Drives one live source's <see cref="SourceTimeline"/> against the session master (Doc 03 §2) - the
/// consumer-side policy that <see cref="SourceTimeline"/> deliberately leaves to its caller: anchor
/// sender↔master on the first frame, map each subsequent frame's sender PTS to a master due-time, and (for
/// <see cref="RebasePolicy.RebaseToLatest"/>) collapse accumulated drift by re-anchoring the newest frame
/// to "now" once it exceeds a threshold. This is what turns "live" into "a source scheduled against the
/// master", replacing the old master-less path (the recurring NDI desync - see
/// <c>Next/03-AV-Sync-Clocks-Routing.md</c>).
/// </summary>
/// <remarks>
/// Pure timing; holds no frame buffer (the source/receiver owns frames). A <see cref="RebasePolicy.Scheduled"/>
/// timeline is never anchored - its sender PTS already live on the master timeline, so placement is the
/// identity <c>pts + Offset</c> used by files (driving a file through this driver is therefore a harmless
/// no-op). Not thread-safe - driven from one ingest thread.
/// </remarks>
public sealed class LiveTimelineDriver
{
    /// <summary>Default drift magnitude before a <see cref="RebasePolicy.RebaseToLatest"/> timeline re-anchors.</summary>
    public static readonly TimeSpan DefaultDriftCollapse = TimeSpan.FromMilliseconds(250);

    private readonly SourceTimeline _timeline;
    private readonly TimeSpan _driftCollapse;
    private long _placed;
    private long _reAnchors;

    public LiveTimelineDriver(SourceTimeline timeline, TimeSpan? driftCollapseThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        _timeline = timeline;
        _driftCollapse = driftCollapseThreshold ?? DefaultDriftCollapse;
        if (_driftCollapse <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(driftCollapseThreshold), "drift-collapse threshold must be positive.");
    }

    /// <summary>The timeline being driven (its <see cref="SourceTimeline.Offset"/> is the live A/V phase trim).</summary>
    public SourceTimeline Timeline => _timeline;

    /// <summary>Frames placed since construction / <see cref="Reset"/>.</summary>
    public long PlacedCount => _placed;

    /// <summary>Times the timeline (re-)anchored - 1 (warm-up) plus each drift collapse.</summary>
    public long ReAnchorCount => _reAnchors;

    /// <summary>
    /// Place a frame with sender timestamp <paramref name="senderPts"/> at master time
    /// <paramref name="masterNow"/>: returns the master due-time and whether the timeline (re-)anchored.
    /// </summary>
    public LiveFramePlacement Place(TimeSpan senderPts, TimeSpan masterNow)
    {
        _placed++;

        // Scheduled: PTS already lives on the master timeline - never anchor; pts + Offset is authoritative.
        if (_timeline.Policy == RebasePolicy.Scheduled)
            return new LiveFramePlacement(_timeline.DueTime(senderPts), ReAnchored: false);

        if (!_timeline.IsAnchored)
            return Anchor(senderPts, masterNow);

        var due = _timeline.DueTime(senderPts);
        if (_timeline.Policy == RebasePolicy.RebaseToLatest && Abs(due - masterNow) > _driftCollapse)
            return Anchor(senderPts, masterNow);

        return new LiveFramePlacement(due, ReAnchored: false);
    }

    /// <summary>Master due-time for a sender PTS without anchoring or advancing counters (read-only).</summary>
    public TimeSpan PeekDue(TimeSpan senderPts) => _timeline.DueTime(senderPts);

    /// <summary>Drop the anchor (seek / source restart); the next <see cref="Place"/> re-anchors.</summary>
    public void Reset()
    {
        _timeline.Reset();
        _placed = 0;
        _reAnchors = 0;
    }

    private LiveFramePlacement Anchor(TimeSpan senderPts, TimeSpan masterNow)
    {
        _timeline.Anchor(senderPts, masterNow);
        _reAnchors++;
        return new LiveFramePlacement(masterNow, ReAnchored: true);
    }

    private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? -t : t;
}
