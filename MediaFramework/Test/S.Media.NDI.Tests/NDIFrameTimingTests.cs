using NDILib;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDIFrameTimingTests
{
    [Fact]
    public void TryMapPresentationTime_UsesFirstFrameAsOrigin()
    {
        long origin = 0;
        var originSet = false;

        Assert.True(NDIFrameTiming.TryMapPresentationTime(1_000_000, NDIConstants.TimestampUndefined,
            ref origin, ref originSet, out var first));
        Assert.True(NDIFrameTiming.TryMapPresentationTime(1_500_000, NDIConstants.TimestampUndefined,
            ref origin, ref originSet, out var second));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(TimeSpan.FromTicks(500_000), second);
    }

    [Fact]
    public void TryMapPresentationTime_FallsBackToTimestampWhenTimecodeIsSynthesized()
    {
        long origin = 0;
        var originSet = false;

        Assert.True(NDIFrameTiming.TryMapPresentationTime(
            NDIConstants.TimecodeSynthesize,
            2_000_000,
            ref origin,
            ref originSet,
            out var first));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(2_000_000, origin);
    }

    [Fact]
    public void TryMapPresentationTime_ReturnsFalseWhenNoNdiTimingExists()
    {
        long origin = 0;
        var originSet = false;

        Assert.False(NDIFrameTiming.TryMapPresentationTime(
            NDIConstants.TimecodeSynthesize,
            NDIConstants.TimestampUndefined,
            ref origin,
            ref originSet,
            out _));
        Assert.False(originSet);
    }

    [Fact]
    public void TryGetAbsolutePresentationTime_UsesTimecodeDirectly_NoSessionOrigin()
    {
        // No origin/rebase: the raw 100 ns timecode maps straight to a TimeSpan, so the value depends only on
        // the frame's timecode — not on which frame this receiver happened to see first.
        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(1_500_000, NDIConstants.TimestampUndefined, out var a));
        Assert.Equal(TimeSpan.FromTicks(1_500_000), a);

        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(1_000_000, NDIConstants.TimestampUndefined, out var b));
        Assert.Equal(TimeSpan.FromTicks(1_000_000), b);
    }

    [Fact]
    public void TryGetAbsolutePresentationTime_TwoReceivers_AgreeOnSameFrame()
    {
        // Two receivers that joined at different points still resolve the same later frame (timecode 5_000_000)
        // to the same absolute time — the property multi-receiver wall sync needs.
        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(5_000_000, NDIConstants.TimestampUndefined, out var fromEarlyJoiner));
        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(5_000_000, NDIConstants.TimestampUndefined, out var fromLateJoiner));
        Assert.Equal(fromEarlyJoiner, fromLateJoiner);
    }

    [Fact]
    public void TryGetAbsolutePresentationTime_FallsBackToTimestamp_ThenFailsWhenNeither()
    {
        Assert.True(NDIFrameTiming.TryGetAbsolutePresentationTime(
            NDIConstants.TimecodeSynthesize, 2_000_000, out var viaTimestamp));
        Assert.Equal(TimeSpan.FromTicks(2_000_000), viaTimestamp);

        Assert.False(NDIFrameTiming.TryGetAbsolutePresentationTime(
            NDIConstants.TimecodeSynthesize, NDIConstants.TimestampUndefined, out var none));
        Assert.Equal(TimeSpan.Zero, none);
    }
}
