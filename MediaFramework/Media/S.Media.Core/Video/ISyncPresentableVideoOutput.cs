namespace S.Media.Core.Video;

/// <summary>What a single coordinated present on an <see cref="ISyncPresentableVideoOutput"/> did.</summary>
public enum VideoSyncPresentOutcome
{
    /// <summary>No unpresented frame was at or before the target - the device kept its last frame on screen.</summary>
    NoChange,

    /// <summary>A newer frame was presented to the device (and any older buffered frames were dropped).</summary>
    Presented,
}

/// <summary>
/// A video output that <strong>buffers</strong> submitted frames and presents them on an
/// <em>external</em> schedule (a <c>VideoPresentSyncGroup</c>) instead of its own cadence, so
/// several physical outputs can present the frame for one shared reference timestamp in lock-step.
/// </summary>
/// <remarks>
/// <para>
/// This is the video half of the multi-output genlock work (issues-doc #2, Option&nbsp;B, Phase&nbsp;2b in
/// <c>Doc/HaPlay-MultiOutput-Sync.md</c>). The audio half disciplines each device's sample rate
/// (<c>AdaptiveRateAudioOutput</c> driven by <c>OutputSyncGroup</c>); this is the
/// "synchronized drop/repeat across outputs" the architecture doc lists as not-implemented - the piece
/// that actually makes two outputs present the <em>same pixels on the same tick</em> for a stitched canvas.
/// </para>
/// <para>
/// The default implementation is <c>SyncPresentVideoOutput</c>; the scheduler is
/// <c>VideoPresentSyncGroup</c>. "Ready"/present operate on <strong>unpresented</strong> buffered
/// frames: a member is only "advance-ready" when it holds a frame the display has not shown yet.
/// </para>
/// </remarks>
public interface ISyncPresentableVideoOutput
{
    /// <summary>True while this output is an active present target (configured and not disposed).</summary>
    bool IsPresentable { get; }

    /// <summary>
    /// Presentation time of the newest <em>unpresented</em> buffered frame at or before
    /// <paramref name="target"/> (peek only - no present, no drop). Returns <c>false</c> when the member
    /// has no unpresented frame due yet, so the scheduler can tell "would advance this tick" from "is
    /// current / starved" and avoid letting other members run ahead.
    /// </summary>
    bool TryPeekReadyPts(TimeSpan target, out TimeSpan readyPts);

    /// <summary>
    /// Presents the newest unpresented buffered frame at or before <paramref name="target"/> to the device,
    /// disposing any older buffered frames (coordinated catch-up / drop). A no-op (<see cref="VideoSyncPresentOutcome.NoChange"/>)
    /// when nothing newer is due. Runs on the group's tick thread.
    /// </summary>
    VideoSyncPresentOutcome PresentUpTo(TimeSpan target);
}
