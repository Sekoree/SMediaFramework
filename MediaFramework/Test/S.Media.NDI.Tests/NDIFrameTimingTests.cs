using NDILib;
using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// NDI-01: unit tests for <see cref="NDIFrameTiming"/> — the pure timestamp-correlation logic that maps an NDI
/// frame's timecode/timestamp fields onto a presentation timeline. This is the receiver's most subtle behaviour
/// (which field wins, session-relative rebase, backward-frame clamping) and it runs with no native NDI, so it is
/// fully unit-testable off hardware.
/// </summary>
public sealed class NDIFrameTimingTests
{
    private const long Synth = NDIConstants.TimecodeSynthesize;   // == long.MaxValue
    private const long Undef = NDIConstants.TimestampUndefined;   // == long.MaxValue

    [Fact]
    public void TryGetFrameStartTicks_PrefersTimecode_ThenTimestamp_ThenFails()
    {
        // A real timecode wins outright.
        Assert.True(NDIFrameTiming.TryGetFrameStartTicks(timecode100Ns: 1_000, timestamp100Ns: 9_999, out var a));
        Assert.Equal(1_000, a);

        // No timecode (synthesize) → fall back to the timestamp.
        Assert.True(NDIFrameTiming.TryGetFrameStartTicks(Synth, timestamp100Ns: 2_000, out var b));
        Assert.Equal(2_000, b);

        // Neither present → no start.
        Assert.False(NDIFrameTiming.TryGetFrameStartTicks(Synth, Undef, out var c));
        Assert.Equal(0, c);
    }

    [Fact]
    public void TryGetAbsolutePresentationTime_MapsTicks_AndRejectsNegativeOrMissing()
    {
        // 10,000,000 × 100 ns = 1 s.
        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(10_000_000, Undef, out var t));
        Assert.Equal(TimeSpan.FromSeconds(1), t);

        // A negative start tick is not a valid absolute presentation time.
        Assert.False(NDIFrameTiming.TryGetAbsolutePresentationTime(-5, Undef, out var neg));
        Assert.Equal(TimeSpan.Zero, neg);

        // Neither field present.
        Assert.False(NDIFrameTiming.TryGetAbsolutePresentationTime(Synth, Undef, out _));
    }

    [Fact]
    public void TryMapPresentationTime_SetsOriginOnFirstFrame_ThenRebasesRelative()
    {
        long origin = 0;
        var originSet = false;

        // First frame defines the session origin and presents at zero.
        Assert.True(NDIFrameTiming.TryMapPresentationTime(1_000_000, Undef, ref origin, ref originSet, out var first));
        Assert.True(originSet);
        Assert.Equal(1_000_000, origin);
        Assert.Equal(TimeSpan.Zero, first);

        // A later frame presents at its delta from the origin (2,000,000 ticks = 0.2 s).
        Assert.True(NDIFrameTiming.TryMapPresentationTime(3_000_000, Undef, ref origin, ref originSet, out var later));
        Assert.Equal(TimeSpan.FromTicks(2_000_000), later);
        Assert.Equal(1_000_000, origin); // origin is not moved by later frames
    }

    [Fact]
    public void TryMapPresentationTime_ClampsFramesBeforeTheOriginToZero()
    {
        long origin = 1_000_000;
        var originSet = true;

        // A frame timestamped before the established origin must not present at a negative time.
        Assert.True(NDIFrameTiming.TryMapPresentationTime(500_000, Undef, ref origin, ref originSet, out var mapped));
        Assert.Equal(TimeSpan.Zero, mapped);
    }

    [Fact]
    public void TryMapPresentationTime_FailsWhenTheFrameHasNoTiming()
    {
        long origin = 0;
        var originSet = false;
        Assert.False(NDIFrameTiming.TryMapPresentationTime(Synth, Undef, ref origin, ref originSet, out var t));
        Assert.Equal(TimeSpan.Zero, t);
        Assert.False(originSet); // a timing-less frame does not establish the origin
    }

    [Theory]
    [InlineData(30, 1, 333_333)]        // 30 fps → 1/30 s
    [InlineData(24, 1, 416_667)]        // 24 fps
    [InlineData(60_000, 1_001, 166_833)] // 59.94 fps (NTSC)
    public void FrameDurationTicks_IsThePeriodForTheRate(int rateN, int rateD, long expectedTicks) =>
        Assert.Equal(expectedTicks, NDIFrameTiming.FrameDurationTicks(rateN, rateD));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 0)]
    [InlineData(-30, 1)]
    public void FrameDurationTicks_FallsBackTo33ms_ForAnInvalidRate(int rateN, int rateD) =>
        Assert.Equal(TimeSpan.FromMilliseconds(33).Ticks, NDIFrameTiming.FrameDurationTicks(rateN, rateD));
}
