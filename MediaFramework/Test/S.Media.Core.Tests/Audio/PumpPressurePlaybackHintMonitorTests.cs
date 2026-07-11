using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class PumpPressurePlaybackHintMonitorTests
{
    [Fact]
    public void ctor_WithOutputId_rejects_blank()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        Assert.Throws<ArgumentException>(() => new PumpPressurePlaybackHintMonitor(r, "  "));
    }

    [Fact]
    public void ctor_WithOutputId_subscribes()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, "slow-output", maxAbsPpm: 200, ppmPerDropPerSecond: 10);
        Assert.Equal(0, m.HintPpmBias);
    }

    [Fact]
    public void ApplyObservation_SustainedDrops_YieldsNegativePpmBias()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 200, ppmPerDropPerSecond: 10);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        m.ApplyObservation(20, t0.AddSeconds(1));

        Assert.InRange(m.GetHintPpmBias(t0.AddSeconds(1)), -200, -1);
    }

    [Fact]
    public void ApplyObservation_NoNewDrops_KeepsHintWithinGracePeriod()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 200, ppmPerDropPerSecond: 10);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        var lastDrop = t0.AddSeconds(1);
        m.ApplyObservation(10, lastDrop);
        var mid = m.GetHintPpmBias(lastDrop);
        Assert.True(mid < 0);

        // A drop-free observation does not change the hint, and reads inside the grace window
        // (up to lastDrop + DecayGracePeriod) still return the full bias.
        m.ApplyObservation(10, t0.AddSeconds(2));
        Assert.Equal(mid, m.GetHintPpmBias(lastDrop + PumpPressurePlaybackHintMonitor.DecayGracePeriod));
    }

    [Fact]
    public void QuietOutput_DecaysHintTowardZero()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 200, ppmPerDropPerSecond: 10);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        var lastDrop = t0.AddSeconds(1);
        m.ApplyObservation(10, lastDrop);
        var mid = m.GetHintPpmBias(lastDrop);
        Assert.True(mid < 0);

        // One half-life past the grace period → half the bias.
        var afterHalfLife = m.GetHintPpmBias(
            lastDrop + PumpPressurePlaybackHintMonitor.DecayGracePeriod + PumpPressurePlaybackHintMonitor.DecayHalfLife);
        Assert.InRange(afterHalfLife, mid / 2 - 0.01, mid / 2 + 0.01);

        // Long quiet → effectively zero (a transient stall no longer biases the output forever).
        Assert.InRange(m.GetHintPpmBias(t0.AddSeconds(120)), -0.05, 0);
    }

    [Fact]
    public void FreshDrops_ReanchorDecay_SoSustainedPressureHoldsFullBias()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 200, ppmPerDropPerSecond: 10);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        m.ApplyObservation(10, t0.AddSeconds(1));
        var mid = m.GetHintPpmBias(t0.AddSeconds(1));

        // Same drop rate again much later: the hint VALUE is unchanged, but the decay anchor moves,
        // so a read right after the new drops returns the full bias again.
        var laterDrop = t0.AddSeconds(30);
        m.ApplyObservation(300, laterDrop);
        Assert.Equal(mid, m.GetHintPpmBias(laterDrop), precision: 6);
    }

    [Fact]
    public void ApplyObservation_manySteps_staysClamped_and_finite()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 40, ppmPerDropPerSecond: 4);
        var t0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        for (var i = 1; i <= 5000; i++)
        {
            m.ApplyObservation(i, t0.AddSeconds(i));
            Assert.InRange(m.HintPpmBias, -40, 0);
            Assert.True(double.IsFinite(m.HintPpmBias));
        }
    }

    [Fact]
    public void ApplyObservation_sinkFilterConstructor_manySteps_staysClamped_and_finite()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, "slow-output", maxAbsPpm: 40, ppmPerDropPerSecond: 4);
        var t0 = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        for (var i = 1; i <= 5000; i++)
        {
            m.ApplyObservation(i, t0.AddSeconds(i));
            Assert.InRange(m.HintPpmBias, -40, 0);
            Assert.True(double.IsFinite(m.HintPpmBias));
        }
    }
}
