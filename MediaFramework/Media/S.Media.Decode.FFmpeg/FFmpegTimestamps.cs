namespace S.Media.Decode.FFmpeg;

/// <summary>
/// FFMPEG-02: the timestamp-normalization arithmetic shared by every FFmpeg decode path — the standalone audio
/// and video file decoders and the shared A/V demux. These conversions were hand-duplicated in four places; the
/// timebase ↔ wall-clock mapping and the best-effort-PTS resolution are the historically most seek-fragile part
/// of the pipeline (see the HW-frame-PTS and timebase-mismatch traps), so they now have a single, pure, directly
/// unit-testable definition instead of copies that could drift.
/// </summary>
internal static class FFmpegTimestamps
{
    /// <summary>
    /// The effective decode PTS for a frame: FFmpeg's <c>best_effort_timestamp</c> when it is known, otherwise the
    /// raw container <c>pts</c>. Returns <see cref="AV_NOPTS_VALUE"/> when neither is known — the caller then
    /// applies its own fallback (audio counts emitted samples, video counts emitted frames at the nominal rate).
    /// This mirrors the classic <c>pts = best_effort; if (pts == AV_NOPTS_VALUE) pts = frame->pts;</c> idiom.
    /// </summary>
    public static long ResolvePts(long bestEffort, long pts) =>
        bestEffort != AV_NOPTS_VALUE ? bestEffort : pts;

    /// <summary>True when a resolved PTS is unknown (both best-effort and raw were <see cref="AV_NOPTS_VALUE"/>).</summary>
    public static bool IsNoPts(long pts) => pts == AV_NOPTS_VALUE;

    /// <summary>Converts a stream-timebase PTS to wall-clock time: <c>pts × (num/den)</c> seconds.</summary>
    public static TimeSpan ToTimeSpan(long pts, AVRational timeBase) =>
        TimeSpan.FromSeconds((double)pts * timeBase.num / timeBase.den);

    /// <summary>Converts a wall-clock seek position to a stream-timebase timestamp for <c>av_seek_frame</c>:
    /// <c>seconds × (den/num)</c>. The inverse of <see cref="ToTimeSpan"/>.</summary>
    public static long ToStreamTimestamp(TimeSpan position, AVRational timeBase) =>
        (long)(position.TotalSeconds * timeBase.den / timeBase.num);
}
