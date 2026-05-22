using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class PumpPressurePlaybackHintMonitorTests
{
    [Fact]
    public void ctor_WithSinkId_rejects_blank()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        Assert.Throws<ArgumentException>(() => new PumpPressurePlaybackHintMonitor(r, "  "));
    }

    [Fact]
    public void ctor_WithSinkId_subscribes()
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

        Assert.InRange(m.HintPpmBias, -200, -1);
    }

    [Fact]
    public void ApplyObservation_NoNewDrops_KeepsPriorHint()
    {
        using var r = new AudioRouter(48_000, chunkSamples: 480);
        using var m = new PumpPressurePlaybackHintMonitor(r, maxAbsPpm: 200, ppmPerDropPerSecond: 10);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        m.ApplyObservation(0, t0);
        m.ApplyObservation(10, t0.AddSeconds(1));
        var mid = m.HintPpmBias;
        m.ApplyObservation(10, t0.AddSeconds(2));
        Assert.Equal(mid, m.HintPpmBias);
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
