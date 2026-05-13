using S.Media.NDI.Clock;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NdiEgressMuxPlayheadClockTests
{
    [Fact]
    public void Elapsed_tracks_max_of_audio_and_video_pts()
    {
        var c = new NdiEgressMuxPlayheadClock();
        var t0 = TimeSpan.FromSeconds(10);
        c.NotifyAudioPresentation(t0);
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
        c.NotifyVideoPresentation(t0 + TimeSpan.FromMilliseconds(40));
        Assert.Equal(TimeSpan.FromMilliseconds(40), c.ElapsedSinceStart);
        c.NotifyAudioPresentation(t0 + TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), c.ElapsedSinceStart);
        Assert.True(c.IsAdvancing);
    }

    [Fact]
    public void Pause_freezes_Resume_continues()
    {
        var c = new NdiEgressMuxPlayheadClock();
        c.NotifyPresentation(TimeSpan.FromSeconds(1));
        c.NotifyPresentation(TimeSpan.FromSeconds(2));
        var atPause = c.ElapsedSinceStart;
        c.Pause();
        Assert.False(c.IsAdvancing);
        Assert.Equal(atPause, c.ElapsedSinceStart);
        c.Resume();
        Assert.True(c.IsAdvancing);
        c.NotifyPresentation(TimeSpan.FromSeconds(3));
        Assert.True(c.ElapsedSinceStart > atPause);
    }

    [Fact]
    public void Reset_clears_origin()
    {
        var c = new NdiEgressMuxPlayheadClock();
        var t0 = TimeSpan.FromHours(1);
        c.NotifyPresentation(t0);
        c.NotifyPresentation(t0 + TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), c.ElapsedSinceStart);
        c.Reset();
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
        c.NotifyPresentation(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
        c.NotifyPresentation(TimeSpan.FromHours(2) + TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), c.ElapsedSinceStart);
    }

    /// <summary>Optional lab: <c>RUN_NDI_MUX_SOAK=1</c> runs <c>120_000</c> rounds (default CI path uses <c>4_000</c>).</summary>
    [Fact]
    public void Soak_mux_playhead_max_pts_rounds()
    {
        var rounds = string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_MUX_SOAK"), "1", StringComparison.Ordinal)
            ? 120_000
            : 4_000;
        var c = new NdiEgressMuxPlayheadClock();
        var t0 = TimeSpan.FromTicks(777_000);
        for (var r = 0; r < rounds; r++)
        {
            c.Reset();
            var o = t0 + TimeSpan.FromTicks(r * 13);
            c.NotifyAudioPresentation(o);
            c.NotifyVideoPresentation(o + TimeSpan.FromMilliseconds(16));
            Assert.Equal(TimeSpan.FromMilliseconds(16), c.ElapsedSinceStart);
            c.NotifyAudioPresentation(o + TimeSpan.FromMilliseconds(100));
            Assert.Equal(TimeSpan.FromMilliseconds(100), c.ElapsedSinceStart);
        }
    }
}
