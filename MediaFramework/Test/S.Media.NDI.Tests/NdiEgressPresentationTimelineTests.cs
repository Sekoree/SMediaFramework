using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NdiEgressPresentationTimelineTests
{
    private const int SoakDefaultRounds = 120_000;
    private const int SoakStressRounds = 1_000_000;
    private const int SoakQuickRounds = 4_000;

    private static bool SoakMode =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK"), "1", StringComparison.Ordinal);

    private static bool SoakStressMode =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Round count used by <c>Soak_sequential_reset_and_timecode_rounds</c> (environment-driven).
    /// </summary>
    public static int ResolveSoakRoundCountForTests()
    {
        if (SoakStressMode)
            return SoakStressRounds;
        if (!SoakMode)
            return SoakQuickRounds;
        var raw = Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS");
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var n))
            return SoakDefaultRounds;
        return Math.Clamp(n, 1_000, 10_000_000);
    }

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
    /// <c>RUN_MEDIA_SOAK=1</c> for shared-demux soak tests). Optional <c>RUN_NDI_EGRESS_SOAK_ROUNDS=&lt;n&gt;</c>
    /// overrides the default <c>120_000</c> when soak is on (clamped <c>1_000</c>–<c>10_000_000</c>).
    /// Optional lab stress: <c>RUN_NDI_EGRESS_SOAK_STRESS=1</c> runs <c>1_000_000</c> rounds (keep off in parallel CI).
    /// </summary>
    [Fact]
    public void Soak_sequential_reset_and_timecode_rounds()
    {
        var rounds = ResolveSoakRoundCountForTests();
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

    [Fact]
    public void Soak_round_count_clamps_RUN_NDI_EGRESS_SOAK_ROUNDS()
    {
        var oldSoak = Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK");
        var oldStress = Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS");
        var oldRounds = Environment.GetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS");
        try
        {
            Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS", null);
            Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK", "1");

            Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS", "500");
            Assert.Equal(1_000, ResolveSoakRoundCountForTests());

            Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS", "50000");
            Assert.Equal(50_000, ResolveSoakRoundCountForTests());

            Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS", "20000000");
            Assert.Equal(10_000_000, ResolveSoakRoundCountForTests());
        }
        finally
        {
            if (oldSoak is null) Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK", oldSoak);
            if (oldStress is null) Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_STRESS", oldStress);
            if (oldRounds is null) Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_EGRESS_SOAK_ROUNDS", oldRounds);
        }
    }
}
