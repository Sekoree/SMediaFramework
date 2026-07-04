namespace S.Media.Core.Video;

/// <summary>
/// A live <see cref="IVideoSource"/> (NDI receiver, capture device) whose synthesized presentation timeline
/// can be re-anchored to a play clock. This is the seam that lets a live source present <em>Scheduled
/// against the session master</em> (Doc 03 §2) instead of the old master-less "latest-on-tick" path: the
/// player calls <see cref="RebaseToLatest"/> at start, and the live timeline driver again on a
/// RebaseToLatest drift collapse, so the next delivered frame's PTS lands at the given master time and
/// subsequent frames step from there.
/// </summary>
public interface ILiveVideoSource : IVideoSource
{
    /// <summary>
    /// Re-anchor the source's synthesized PTS so the next frame presents at <paramref name="playClockNow"/>
    /// on the master timeline.
    /// </summary>
    void RebaseToLatest(TimeSpan playClockNow);
}
