using HaPlay.ViewModels;

namespace HaPlay.Playback;

/// <summary>Per-output health scoring for the I/O panel LEDs. The counters now come from the ShowSession
/// paths (composition throughput + audio-pump snapshots - see the deck's and cue workspace's
/// <c>TryGet…LineHealthMetrics</c>); the legacy engine's session-probing overloads were deleted with it.</summary>
internal static class OutputLineHealthEvaluator
{
    /// <summary>Phase E (§8.1) - one snapshot of a line's pump counters, so the caller can push throughput
    /// + drops into a per-line ring buffer for sparklines without re-querying the metrics.
    /// <see cref="VideoSubmitted"/>/<see cref="AudioEnqueued"/> are cumulative (they feed the sparkline and
    /// stats summary); <see cref="VideoDropped"/>/<see cref="AudioDropped"/> carry the RECENT count - events
    /// since the previous ~1 Hz health poll - so the LED reflects what is happening now, not a lifetime
    /// total that latches red forever.</summary>
    public readonly record struct LineHealthMetrics(
        OutputLineHealthState State,
        long VideoSubmitted,
        long VideoDropped,
        int VideoQueueDepth,
        int VideoQueueCap,
        long AudioEnqueued,
        long AudioDropped);

    /// <summary>
    /// Scores one health-poll window (~1 s). <paramref name="recentVideoLate"/> is the number of
    /// composition pump ticks that ran over the canvas budget since the last poll - the "output is
    /// starved" signal (a couple from a load spike is tolerable; a sustained rate means visibly choppy
    /// output). <paramref name="recentAudioDropped"/> is audio chunks the pump failed to deliver -
    /// always audible, so any recent drop warns. Slot overflow is deliberately NOT an input: a
    /// master-aligned / latest-wins layer slot replaces a stale pending frame as part of normal pacing
    /// (decode-ahead, canvas-vs-source rate skew), so counting it reported hundreds of phantom "drops"
    /// per minute on a perfectly smooth output.
    /// </summary>
    public static OutputLineHealthState Score(long recentVideoLate, long recentAudioDropped) =>
        recentVideoLate >= 20 || recentAudioDropped >= 10
            ? OutputLineHealthState.Error
            : recentVideoLate >= 3 || recentAudioDropped >= 1
                ? OutputLineHealthState.Warning
                : OutputLineHealthState.Healthy;

    /// <summary>
    /// Turns a cumulative counter into "events since the last poll" for <paramref name="key"/>. The first
    /// observation reports 0 (startup churn - e.g. prebuffer overruns during an open - must not flash the
    /// LED). A counter that went BACKWARD means the underlying composition/pump was rebuilt (clip reload);
    /// the fresh counter's value is the recent count.
    /// </summary>
    public static long RecentDelta(Dictionary<Guid, long> previous, Guid key, long current)
    {
        var recent = previous.TryGetValue(key, out var last)
            ? current >= last ? current - last : current
            : 0;
        previous[key] = current;
        return recent;
    }
}
