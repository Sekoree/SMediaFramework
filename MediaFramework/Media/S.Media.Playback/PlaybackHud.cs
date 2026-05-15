namespace S.Media.Playback;

/// <summary>Scalar HUD fields for one <c>\r</c> status line (smoke tools / hosts).</summary>
public readonly record struct PlaybackHudSnapshot(
    TimeSpan ClockPosition,
    TimeSpan VideoPts,
    TimeSpan AudioHeard,
    TimeSpan AudioDeckDecode,
    long DisplayedCount,
    long DecodedCount,
    double VFpsEstimate,
    string NominalFpsLabel,
    long DroppedLate,
    long DroppedDrain,
    long GlDroppedNewer,
    long NDIVidDr,
    int NDIVidQ,
    long PaUnd,
    long PaDr,
    long PumpDr,
    long NDIAuDr,
    string NDIMonitorTail);

/// <summary>Formats playback HUD text without referencing SDL, NDI, or PortAudio.</summary>
public static class PlaybackHud
{
    /// <summary>Wall-clock style string for a playhead <see cref="TimeSpan"/> (hours may exceed 24).</summary>
    public static string FormatClock(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    /// <summary>Formats one status line (caller typically prefixes <c>\r</c> and writes without newline).</summary>
    public static string FormatLine(in PlaybackHudSnapshot s) =>
        $"clock {FormatClock(s.ClockPosition)}  vPTS {s.VideoPts:mm\\:ss\\.fff}  " +
        $"aHeard {s.AudioHeard:mm\\:ss\\.fff}  aDec {s.AudioDeckDecode:mm\\:ss\\.fff}  " +
        $"show {s.DisplayedCount}/{s.DecodedCount}  vFps~{s.VFpsEstimate:0.#}  nom {s.NominalFpsLabel}  " +
        $"mux shared  vLate {s.DroppedLate}  vDrn {s.DroppedDrain}  " +
        $"glDr {s.GlDroppedNewer}  ndiVidDr {s.NDIVidDr}  ndiVidQ {s.NDIVidQ}  paUnd {s.PaUnd}  paDr {s.PaDr}  pumpDr {s.PumpDr}  ndiAuDr {s.NDIAuDr}{s.NDIMonitorTail}";
}
