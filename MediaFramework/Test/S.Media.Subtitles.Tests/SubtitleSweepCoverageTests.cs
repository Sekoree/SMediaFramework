using Xunit;

namespace S.Media.Subtitles.Tests;

/// <summary>The interval bookkeeping behind seek-aware subtitle streaming: renders ask
/// <see cref="SubtitleSweepCoverage.IsCovered"/> per tick and an uncovered position triggers a demux
/// seek, so these pin down exactly when a position counts as covered.</summary>
public class SubtitleSweepCoverageTests
{
    private const long Grace = 60_000;

    [Fact]
    public void ActiveSweep_CoversStartThroughFrontierPlusGrace()
    {
        var coverage = new SubtitleSweepCoverage(Grace);
        coverage.BeginSweep(0);
        coverage.AdvanceFrontier(10_000);

        Assert.True(coverage.IsCovered(0));
        Assert.True(coverage.IsCovered(5_000));
        Assert.True(coverage.IsCovered(10_000 + Grace));     // just inside the near-ahead grace
        Assert.False(coverage.IsCovered(10_000 + Grace + 1)); // just past it - worth a seek
        Assert.False(coverage.IsCovered(3_600_000));
    }

    [Fact]
    public void SeekSweep_ArchivesTheOldSweep_AndLeavesTheGapUncovered()
    {
        var coverage = new SubtitleSweepCoverage(Grace);
        coverage.BeginSweep(0);
        coverage.AdvanceFrontier(10_000);

        coverage.BeginSweep(600_000); // playhead jumped: seek sweep starts deep in the file

        Assert.True(coverage.IsCovered(5_000));    // archived first sweep
        Assert.False(coverage.IsCovered(300_000)); // the gap between the sweeps
        Assert.True(coverage.IsCovered(610_000));  // active sweep start + grace
        Assert.False(coverage.IsCovered(300_000 - 1));
    }

    [Fact]
    public void CompleteToEnd_CoversTheTailForever_ButFullCoverageNeedsTheGapsClosed()
    {
        var coverage = new SubtitleSweepCoverage(Grace);
        coverage.BeginSweep(0);
        coverage.AdvanceFrontier(10_000);
        coverage.BeginSweep(600_000);
        coverage.AdvanceFrontier(700_000);
        coverage.CompleteToEnd();

        Assert.True(coverage.IsCovered(long.MaxValue - 1)); // tail covered to end-of-stream
        Assert.False(coverage.IsCovered(300_000));          // gap remains
        Assert.False(coverage.IsFullyCovered);

        // A back-fill sweep across the gap merges everything into one [0, ∞) span.
        coverage.BeginSweep(9_000);
        coverage.AdvanceFrontier(600_500);
        coverage.BeginSweep(650_000); // archive the back-fill (any next sweep does)

        Assert.True(coverage.IsCovered(300_000));
        Assert.True(coverage.IsFullyCovered);
    }

    [Fact]
    public void NegativePositions_ClampToZero()
    {
        var coverage = new SubtitleSweepCoverage(Grace);
        coverage.BeginSweep(0);

        Assert.True(coverage.IsCovered(-5));
    }
}
