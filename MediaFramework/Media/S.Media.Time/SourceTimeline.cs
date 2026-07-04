namespace S.Media.Time;

/// <summary>How a source's PTS is mapped onto the session master timeline (D4 / Doc 03).</summary>
public enum RebasePolicy
{
    /// <summary>Source PTS is authoritative; a frame is due when <c>master ≥ pts + offset</c>. Files and
    /// exact-rate live senders — gives perfect lip-sync.</summary>
    Scheduled,

    /// <summary>Keep a bounded jitter buffer and present the frame whose rebased PTS brackets master.
    /// Live sources whose clock differs from ours.</summary>
    Holdback,

    /// <summary>Like <see cref="Holdback"/> but collapse accumulated drift by re-anchoring the newest
    /// frame to "now" when the buffer over/underflows (NDI's RebaseToLatest, expressed as a policy).</summary>
    RebaseToLatest,
}

/// <summary>
/// Maps one source's presentation timestamps onto the session master timeline (Doc 03). It owns the
/// timing math — a fixed signed <see cref="Offset"/> plus an optional anchor that ties a source PTS to a
/// master instant — but not the frame buffer: the player holds frames and asks the timeline <em>when</em>
/// each is due (<see cref="DueTime"/>) or <em>which</em> source time corresponds to now
/// (<see cref="SourceTimeAt"/>). The <see cref="Policy"/> tells the consumer how to drive anchoring.
/// </summary>
/// <remarks>
/// <para>File playback (<see cref="RebasePolicy.Scheduled"/>): the source PTS already lives on the master
/// timeline, so leaving the timeline un-anchored gives <c>DueTime(pts) = pts + Offset</c>.</para>
/// <para>Live (<see cref="RebasePolicy.Holdback"/> / <see cref="RebasePolicy.RebaseToLatest"/>): the
/// sender clock is unrelated to master, so <see cref="Anchor"/> ties a sender PTS to master-now; on
/// over/underflow, RebaseToLatest re-anchors the newest frame. Not thread-safe — driven from one
/// player/router thread.</para>
/// </remarks>
public sealed class SourceTimeline(RebasePolicy policy, TimeSpan offset = default)
{
    private TimeSpan _anchorSource;
    private TimeSpan _anchorMaster;

    /// <summary>Mapping policy for this source.</summary>
    public RebasePolicy Policy { get; } = policy;

    /// <summary>Signed per-source correction (known A/V phase error, capture latency, manual trim). First-class control.</summary>
    public TimeSpan Offset { get; set; } = offset;

    /// <summary>True once <see cref="Anchor"/> has tied this source's PTS to the master timeline.</summary>
    public bool IsAnchored { get; private set; }

    /// <summary>Tie <paramref name="sourcePts"/> to <paramref name="masterNow"/>. Call on the first frame
    /// (warm-up) and again to re-anchor on <see cref="RebasePolicy.RebaseToLatest"/> drift collapse.</summary>
    public void Anchor(TimeSpan sourcePts, TimeSpan masterNow)
    {
        _anchorSource = sourcePts;
        _anchorMaster = masterNow;
        IsAnchored = true;
    }

    /// <summary>Master time at which a frame with <paramref name="sourcePts"/> is due to present.</summary>
    public TimeSpan DueTime(TimeSpan sourcePts) =>
        IsAnchored ? sourcePts - _anchorSource + _anchorMaster + Offset : sourcePts + Offset;

    /// <summary>The source PTS corresponding to <paramref name="masterNow"/> (inverse of <see cref="DueTime"/>).</summary>
    public TimeSpan SourceTimeAt(TimeSpan masterNow) =>
        IsAnchored ? masterNow - _anchorMaster - Offset + _anchorSource : masterNow - Offset;

    /// <summary>Drop the anchor (e.g. on seek / source restart); reverts to <c>pts + Offset</c>.</summary>
    public void Reset()
    {
        IsAnchored = false;
        _anchorSource = default;
        _anchorMaster = default;
    }
}
