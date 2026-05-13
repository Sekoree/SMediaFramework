using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NdiEgressPresentationTimelineTests
{
    private static bool SoakMode =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK"), "1", StringComparison.Ordinal);

    private static bool SoakStressMode =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS"), "1", StringComparison.Ordinal);

    [Fact]
    public void FirstSample_becomes_anchor_at_zero_delta()
    {
        var t = new NdiEgressPresentationTimeline();
        var anchor = TimeSpan.FromSeconds(12.5);
        Assert.Equal(0L, t.TimecodeFromPresentationTime(anchor));
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, t.TimecodeFromPresentationTime(anchor + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Audio_then_video_shares_anchor()
    {
        var t = new NdiEgressPresentationTimeline();
        var t0 = TimeSpan.FromSeconds(100);
        Assert.Equal(0L, t.TimecodeFromPresentationTime(t0));
        var tVideo = t0 + TimeSpan.FromMilliseconds(40);
        Assert.Equal(TimeSpan.FromMilliseconds(40).Ticks, t.TimecodeFromPresentationTime(tVideo));
    }

    [Fact]
    public void Large_backward_jump_reanchors()
    {
        var t = new NdiEgressPresentationTimeline();
        var a = TimeSpan.FromSeconds(50);
        Assert.Equal(0L, t.TimecodeFromPresentationTime(a));
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, t.TimecodeFromPresentationTime(a + TimeSpan.FromSeconds(10)));

        var jumped = a - TimeSpan.FromSeconds(5);
        Assert.Equal(0L, t.TimecodeFromPresentationTime(jumped));
    }

    [Fact]
    public void Reset_clears_anchor()
    {
        var t = new NdiEgressPresentationTimeline();
        var a = TimeSpan.FromMinutes(1);
        t.TimecodeFromPresentationTime(a);
        t.Reset();
        Assert.Equal(0L, t.TimecodeFromPresentationTime(a + TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// Optional heavier run: set environment variable <c>RUN_NDI_EGRESS_SOAK=1</c> (same pattern as
    /// <c>RUN_MEDIA_SOAK=1</c> for shared-demux soak tests).
    /// Optional lab stress: <c>RUN_NDI_EGRESS_SOAK_STRESS=1</c> runs <c>1_000_000</c> rounds (keep off in parallel CI).
    /// </summary>
    [Fact]
    public void Soak_sequential_reset_and_timecode_rounds()
    {
        var rounds = SoakStressMode ? 1_000_000 : SoakMode ? 120_000 : 4_000;
        for (var r = 0; r < rounds; r++)
        {
            var t = new NdiEgressPresentationTimeline();
            var o = TimeSpan.FromTicks(1_009_039L + r * 997);
            Assert.Equal(0L, t.TimecodeFromPresentationTime(o));
            Assert.Equal(500, t.TimecodeFromPresentationTime(o + TimeSpan.FromTicks(500)));
            t.Reset();
            Assert.Equal(0L, t.TimecodeFromPresentationTime(o + TimeSpan.FromTicks(10_000)));
        }
    }
}
