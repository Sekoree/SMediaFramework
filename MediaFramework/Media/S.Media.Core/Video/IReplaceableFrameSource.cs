namespace S.Media.Core.Video;

/// <summary>
/// A held-frame video source whose single displayed frame can be swapped <em>live</em>, without re-opening the
/// clip - e.g. a rendered text or still cue whose content is edited while it plays. The source adopts and owns the
/// new frame; the swap is picked up on the next read. Implementers must be safe against a concurrent read on the
/// render thread. A player exposes its source via <c>MediaPlayer.VideoSource</c>, so a session can test for this
/// capability and update the running clip in place instead of tearing it down and rebuilding the show.
/// </summary>
public interface IReplaceableFrameSource
{
    /// <summary>Replaces the held frame with <paramref name="frame"/> (ownership transfers to the source). A later
    /// read returns the new content; format/duration/position are unchanged.</summary>
    void ReplaceFrame(VideoFrame frame);
}
