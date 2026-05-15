using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class MediaPlayerTests
{
    [Fact]
    public void TryOpen_missing_file_returns_false()
    {
        var path = "/nonexistent/path/that/does/not/exist-" + Guid.NewGuid();
        Assert.False(MediaPlayer.TryOpen(path, MediaPlayerOpenOptions.Default, null, false, out var p, out var err));
        Assert.Null(p);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void PlaybackHud_FormatLine_matches_expected_tokens_for_fixed_snapshot()
    {
        var snap = new PlaybackHudSnapshot(
            ClockPosition: new TimeSpan(0, 1, 2, 3, 400),
            VideoPts: new TimeSpan(0, 0, 0, 5, 123),
            AudioHeard: new TimeSpan(0, 0, 0, 4, 0),
            AudioDeckDecode: new TimeSpan(0, 0, 0, 4, 500),
            DisplayedCount: 100,
            DecodedCount: 120,
            VFpsEstimate: 29.5,
            NominalFpsLabel: "30Hz",
            DroppedLate: 1,
            DroppedDrain: 2,
            GlDroppedNewer: 3,
            NDIVidDr: 4,
            NDIVidQ: 5,
            PaUnd: 6,
            PaDr: 7,
            PumpDr: 8,
            NDIAuDr: 9,
            NDIMonitorTail: "  ndiRx2 P0V1 tallyΔ1");

        var line = PlaybackHud.FormatLine(snap);

        Assert.Equal(
            "clock 01:02:03.400  vPTS 00:05.123  aHeard 00:04.000  aDec 00:04.500  " +
            "show 100/120  vFps~29.5  nom 30Hz  mux shared  vLate 1  vDrn 2  " +
            "glDr 3  ndiVidDr 4  ndiVidQ 5  paUnd 6  paDr 7  pumpDr 8  ndiAuDr 9  ndiRx2 P0V1 tallyΔ1",
            line);
    }

    [Fact]
    public void PlaybackHud_FormatClock_uses_total_hours_and_fixed_width_fields()
    {
        Assert.Equal("01:02:03.456", PlaybackHud.FormatClock(new TimeSpan(0, 1, 2, 3, 456)));
        Assert.Equal("25:00:00.000", PlaybackHud.FormatClock(new TimeSpan(1, 1, 0, 0, 0)));
    }
}
