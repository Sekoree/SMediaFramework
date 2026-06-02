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
}
