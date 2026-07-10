using FFmpeg.AutoGen;
using S.Media.Decode.FFmpeg;
using Xunit;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// FFMPEG-02: pure invariant tests for the extracted <see cref="FFmpegTimestamps"/> normalization arithmetic.
/// These need no native FFmpeg (unlike the fixture matrix), so they run on every runner and lock the timebase ↔
/// wall-clock mapping - the historically most seek-fragile part of decode - against a single tested definition.
/// </summary>
public sealed class FFmpegTimestampsTests
{
    [Fact]
    public void ResolvePts_PrefersBestEffort_ThenRawPts_ThenNoPts()
    {
        // best-effort known → use it (even when a raw pts also exists).
        Assert.Equal(123, FFmpegTimestamps.ResolvePts(bestEffort: 123, pts: 456));
        // best-effort unknown → fall back to the raw container pts.
        Assert.Equal(456, FFmpegTimestamps.ResolvePts(bestEffort: AV_NOPTS_VALUE, pts: 456));
        // both unknown → propagate AV_NOPTS_VALUE so the caller applies its own fallback.
        Assert.Equal(AV_NOPTS_VALUE, FFmpegTimestamps.ResolvePts(bestEffort: AV_NOPTS_VALUE, pts: AV_NOPTS_VALUE));
    }

    [Fact]
    public void IsNoPts_OnlyTrueForTheSentinel()
    {
        Assert.True(FFmpegTimestamps.IsNoPts(AV_NOPTS_VALUE));
        Assert.False(FFmpegTimestamps.IsNoPts(0));
        Assert.False(FFmpegTimestamps.IsNoPts(-1));
        Assert.False(FFmpegTimestamps.IsNoPts(long.MaxValue));
    }

    [Theory]
    [InlineData(90_000, 1, 90_000, 1.0)]   // one full second at a 90 kHz timebase
    [InlineData(45_000, 1, 90_000, 0.5)]
    [InlineData(30, 1, 30, 1.0)]           // 30 ticks at 1/30 s per tick
    [InlineData(0, 1, 90_000, 0.0)]
    [InlineData(48_000, 1, 48_000, 1.0)]   // audio sample-rate timebase
    public void ToTimeSpan_ScalesByTheTimebase(long pts, int num, int den, double expectedSeconds)
    {
        var result = FFmpegTimestamps.ToTimeSpan(pts, new AVRational { num = num, den = den });
        Assert.Equal(expectedSeconds, result.TotalSeconds, precision: 9);
    }

    [Theory]
    [InlineData(1.0, 1, 30, 30)]
    [InlineData(2.0, 1, 90_000, 180_000)]
    [InlineData(0.5, 1, 48_000, 24_000)]
    [InlineData(0.0, 1, 90_000, 0)]
    public void ToStreamTimestamp_IsTheInverseScaling(double seconds, int num, int den, long expectedTicks)
    {
        var ts = FFmpegTimestamps.ToStreamTimestamp(TimeSpan.FromSeconds(seconds), new AVRational { num = num, den = den });
        Assert.Equal(expectedTicks, ts);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(1, 90_000)]
    [InlineData(1, 48_000)]
    [InlineData(1001, 30_000)] // NTSC-style non-integer frame rate timebase
    public void ToStreamTimestamp_And_ToTimeSpan_RoundTrip(int num, int den)
    {
        var tb = new AVRational { num = num, den = den };
        var original = TimeSpan.FromSeconds(3.0);

        var ts = FFmpegTimestamps.ToStreamTimestamp(original, tb);
        var back = FFmpegTimestamps.ToTimeSpan(ts, tb);

        // A round-trip loses at most one timebase tick to truncation.
        var tickSeconds = (double)tb.num / tb.den;
        Assert.True(Math.Abs((back - original).TotalSeconds) <= tickSeconds + 1e-9,
            $"round-trip drifted by more than one tick: {original} → {ts} → {back}");
    }
}
