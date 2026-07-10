namespace S.Media.Time;

/// <summary>
/// Correlated <see cref="SourceTimeline"/>s that originate from one sender/device or demux session - e.g.
/// NDI video + NDI audio, or a camera + its embedded audio (Doc 03). The group owns the single
/// sender→session anchor so the streams keep their sender-side relationship; each member adds only a small
/// per-stream correction <see cref="SourceTimeline.Offset"/> (known A/V phase, capture latency). This is
/// what stops live video and audio from being treated as two unrelated clocks (the recurring NDI desync).
/// </summary>
/// <remarks>Not thread-safe - driven from one ingest/player thread.</remarks>
public sealed class SourceSyncGroup(RebasePolicy policy = RebasePolicy.Holdback)
{
    private readonly List<SourceTimeline> _members = [];

    /// <summary>Mapping policy shared by all members of the group.</summary>
    public RebasePolicy Policy { get; } = policy;

    /// <summary>The member timelines (one per correlated stream).</summary>
    public IReadOnlyList<SourceTimeline> Members => _members;

    /// <summary>True once <see cref="Anchor"/> has tied the group to the master timeline.</summary>
    public bool IsAnchored { get; private set; }

    /// <summary>Adds a stream to the group with an optional per-stream correction offset.</summary>
    public SourceTimeline AddStream(TimeSpan correctionOffset = default)
    {
        var timeline = new SourceTimeline(Policy, correctionOffset);
        _members.Add(timeline);
        return timeline;
    }

    /// <summary>
    /// Anchor every member to the same sender reference (<paramref name="senderPts"/> ↔
    /// <paramref name="masterNow"/>), preserving each member's per-stream offset. Call on first frame and
    /// again to re-anchor on <see cref="RebasePolicy.RebaseToLatest"/> drift collapse. Because all members
    /// share one anchor, their relative phase is fixed by their offsets - observable and correctable.
    /// </summary>
    public void Anchor(TimeSpan senderPts, TimeSpan masterNow)
    {
        foreach (var member in _members)
            member.Anchor(senderPts, masterNow);
        IsAnchored = true;
    }
}
