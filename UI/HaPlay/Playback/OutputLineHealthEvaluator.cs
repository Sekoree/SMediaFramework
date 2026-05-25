using HaPlay.ViewModels;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>Derives per-output health from router pump metrics during active playback.</summary>
internal static class OutputLineHealthEvaluator
{
    /// <summary>Phase E (§8.1) — one snapshot of a line's pump counters. The session-aware
    /// <see cref="EvaluateWithMetrics"/> overload returns this so the caller can push throughput
    /// + drops into a per-line ring buffer for sparklines without re-querying the metrics.</summary>
    public readonly record struct LineHealthMetrics(
        OutputLineHealthState State,
        long VideoSubmitted,
        long VideoDropped,
        int VideoQueueDepth,
        int VideoQueueCap,
        long AudioEnqueued,
        long AudioDropped);

    public static OutputLineHealthState Evaluate(
        HaPlayPlaybackSession session,
        OutputLineViewModel line) =>
        EvaluateWithMetrics(session, line).State;

    /// <summary>Same scoring as <see cref="Evaluate"/> but also exposes the underlying counters so the
    /// caller can push them into a sparkline history (§8.1).</summary>
    public static LineHealthMetrics EvaluateWithMetrics(
        HaPlayPlaybackSession session,
        OutputLineViewModel line)
    {
        if (!session.HasWiredLine(line))
            return new LineHealthMetrics(OutputLineHealthState.Unknown, 0, 0, 0, 0, 0, 0);

        long videoDropped = 0;
        long videoSubmitted = 0;
        int videoQueueDepth = 0;
        int videoQueueCap = 0;
        long audioDropped = 0;
        long audioEnqueued = 0;

        if (session.TryGetVideoHealthMetrics(line, out VideoOutputPumpMetrics vm))
        {
            videoDropped = vm.DroppedFrames;
            videoSubmitted = vm.SubmittedFrames;
            videoQueueDepth = vm.CurrentQueuedDepth;
            videoQueueCap = vm.MaxQueueDepth;
        }

        if (session.TryGetAudioHealthMetrics(line, out var st))
        {
            audioDropped = st.Dropped;
            audioEnqueued = st.Enqueued;
        }

        var portAudioUnderruns = session.GetPortAudioUnderrunDelta(line);

        OutputLineHealthState state;
        if (videoDropped == 0 && audioDropped == 0 && portAudioUnderruns == 0 && videoSubmitted + audioEnqueued > 0)
        {
            if (videoQueueCap > 0 && videoQueueDepth >= Math.Max(1, videoQueueCap * 3 / 4))
                state = OutputLineHealthState.Warning;
            else
                state = OutputLineHealthState.Healthy;
        }
        else
        {
            var totalSubmitted = Math.Max(1, videoSubmitted + audioEnqueued);
            var totalDropped = videoDropped + audioDropped;
            var dropRatio = (double)totalDropped / totalSubmitted;

            // Underrun scoring: use rate-based threshold instead of absolute count.
            // 48000 frames/sec = full stream; allow up to ~500ms cumulative underrun
            // before Error (covers startup transients and OS scheduler jitter).
            // Warning only above 960 frames (~20ms) to ignore single-callback blips.
            var underrunHeavy = portAudioUnderruns > 24000;
            var underrunSignificant = portAudioUnderruns > 960;

            if (totalDropped > 120 || dropRatio > 0.05 || underrunHeavy)
                state = OutputLineHealthState.Error;
            else if (totalDropped > 0 || underrunSignificant
                     || (videoQueueCap > 0 && videoQueueDepth >= Math.Max(1, videoQueueCap / 2)))
                state = OutputLineHealthState.Warning;
            else
                state = totalSubmitted > 0 ? OutputLineHealthState.Healthy : OutputLineHealthState.Unknown;
        }

        return new LineHealthMetrics(state,
            videoSubmitted, videoDropped, videoQueueDepth, videoQueueCap,
            audioEnqueued, audioDropped);
    }
}
