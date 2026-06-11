using S.Media.Core.Clock;

namespace S.Media.Playback;

/// <summary>
/// An <see cref="IPlayhead"/> view of <paramref name="inner"/> shifted by a constant
/// <paramref name="offset"/>: <see cref="CurrentPosition"/> = inner + offset.
/// </summary>
/// <remarks>
/// Companion to <c>RetimingVideoOutput</c> for clip-window playback: when a trimmed clip's frames
/// are rebased to clip-relative PTS (offset = −window.Start) before they reach a composition slot,
/// the timeline used for master-aligned frame selection must be rebased identically — mixing
/// source-time playhead with clip-relative frame PTS makes "closest to the playhead" pick a stale
/// pre-seek frame over fresh post-seek frames after every backward seek (video freezes while
/// audio plays on).
/// </remarks>
public sealed class OffsetPlayhead(IPlayhead inner, TimeSpan offset) : IPlayhead
{
    public TimeSpan CurrentPosition => inner.CurrentPosition + offset;

    public bool IsRunning => inner.IsRunning;

    public double PlaybackRate => inner.PlaybackRate;

    public void Seek(TimeSpan position) => inner.Seek(position - offset);
}
