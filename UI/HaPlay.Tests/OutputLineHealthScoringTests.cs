using HaPlay.Playback;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Pins the rate-based output-health scoring that replaced the lifetime-total formula. The old rule
/// counted composition slot overflow (normal master-aligned pacing: a superseded pending frame is not a
/// visible drop) and pump overruns cumulatively against a lifetime &gt;120 threshold - so a perfectly
/// smooth deck reported a steadily climbing "drops" number every second and latched the LED red within
/// minutes. Health must instead reflect what happened since the previous poll.
/// </summary>
public sealed class OutputLineHealthScoringTests
{
    [Fact]
    public void QuietWindow_IsHealthy()
    {
        Assert.Equal(OutputLineHealthState.Healthy, OutputLineHealthEvaluator.Score(0, 0));
        // A couple of late ticks from a load spike must not flash the LED.
        Assert.Equal(OutputLineHealthState.Healthy, OutputLineHealthEvaluator.Score(2, 0));
    }

    [Fact]
    public void SustainedLateVideo_Warns_ThenErrors()
    {
        Assert.Equal(OutputLineHealthState.Warning, OutputLineHealthEvaluator.Score(3, 0));
        Assert.Equal(OutputLineHealthState.Warning, OutputLineHealthEvaluator.Score(19, 0));
        Assert.Equal(OutputLineHealthState.Error, OutputLineHealthEvaluator.Score(20, 0));
    }

    [Fact]
    public void AudioDrops_AreAlwaysAtLeastAWarning()
    {
        // Audio drops are audible - a single one in a window already warrants attention.
        Assert.Equal(OutputLineHealthState.Warning, OutputLineHealthEvaluator.Score(0, 1));
        Assert.Equal(OutputLineHealthState.Error, OutputLineHealthEvaluator.Score(0, 10));
    }

    [Fact]
    public void RecentDelta_ReportsPerWindowCounts_NotLifetimeTotals()
    {
        var prev = new Dictionary<Guid, long>();
        var line = Guid.NewGuid();

        // First observation is 0: startup churn (prebuffer overruns during an open) must not flash the LED.
        Assert.Equal(0, OutputLineHealthEvaluator.RecentDelta(prev, line, 50));
        // Steady counter → zero recent events, regardless of the lifetime total.
        Assert.Equal(0, OutputLineHealthEvaluator.RecentDelta(prev, line, 50));
        // Growth since the last poll is the recent count.
        Assert.Equal(7, OutputLineHealthEvaluator.RecentDelta(prev, line, 57));
        Assert.Equal(0, OutputLineHealthEvaluator.RecentDelta(prev, line, 57));
    }

    [Fact]
    public void RecentDelta_TreatsACounterReset_AsAFreshCounter()
    {
        var prev = new Dictionary<Guid, long>();
        var line = Guid.NewGuid();
        Assert.Equal(0, OutputLineHealthEvaluator.RecentDelta(prev, line, 100));

        // Clip reload rebuilt the composition: the cumulative counter restarted. The fresh counter's
        // value IS the recent count - not a huge negative or a phantom spike.
        Assert.Equal(4, OutputLineHealthEvaluator.RecentDelta(prev, line, 4));
        Assert.Equal(1, OutputLineHealthEvaluator.RecentDelta(prev, line, 5));
    }

    [Fact]
    public void LinesTrackIndependently()
    {
        var prev = new Dictionary<Guid, long>();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        OutputLineHealthEvaluator.RecentDelta(prev, a, 10);
        OutputLineHealthEvaluator.RecentDelta(prev, b, 0);
        Assert.Equal(5, OutputLineHealthEvaluator.RecentDelta(prev, a, 15));
        Assert.Equal(2, OutputLineHealthEvaluator.RecentDelta(prev, b, 2));
    }
}
