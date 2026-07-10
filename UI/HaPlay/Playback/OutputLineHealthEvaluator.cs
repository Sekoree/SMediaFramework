using HaPlay.ViewModels;

namespace HaPlay.Playback;

/// <summary>Per-output health scoring for the I/O panel LEDs. The counters now come from the ShowSession
/// paths (composition throughput + audio-pump snapshots - see the deck's and cue workspace's
/// <c>TryGet…LineHealthMetrics</c>); the legacy engine's session-probing overloads were deleted with it.</summary>
internal static class OutputLineHealthEvaluator
{
    /// <summary>Phase E (§8.1) - one snapshot of a line's pump counters, so the caller can push throughput
    /// + drops into a per-line ring buffer for sparklines without re-querying the metrics.</summary>
    public readonly record struct LineHealthMetrics(
        OutputLineHealthState State,
        long VideoSubmitted,
        long VideoDropped,
        int VideoQueueDepth,
        int VideoQueueCap,
        long AudioEnqueued,
        long AudioDropped);
}
