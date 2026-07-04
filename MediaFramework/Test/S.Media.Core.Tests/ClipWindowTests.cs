using Xunit;

namespace S.Media.Core.Tests;

public sealed class ClipWindowTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void FromOffsets_KnownDuration_ComputesTrimmedWindow()
    {
        var w = ClipWindow.FromOffsets(Sec(80), Sec(10), Sec(100));

        Assert.True(w.HasKnownEnd);
        Assert.Equal(Sec(80), w.Start);
        Assert.Equal(Sec(90), w.End);
        Assert.Equal(Sec(10), w.Duration);
    }

    [Fact]
    public void FromOffsets_UnknownDuration_IsUnboundedFromStart()
    {
        var w = ClipWindow.FromOffsets(Sec(5), Sec(2), TimeSpan.Zero);

        Assert.False(w.HasKnownEnd);
        Assert.Equal(Sec(5), w.Start);
        Assert.Equal(TimeSpan.Zero, w.Duration);
    }

    [Fact]
    public void FromOffsets_StartBeyondDuration_ClampsToGuardedMax()
    {
        var w = ClipWindow.FromOffsets(Sec(200), TimeSpan.Zero, Sec(100));

        // Start clamps to duration − guard; end stays at the (untrimmed) duration, leaving a
        // one-guard-wide window rather than a negative one.
        Assert.Equal(Sec(100) - ClipWindow.DefaultEndGuard, w.Start);
        Assert.Equal(Sec(100), w.End);
        Assert.Equal(ClipWindow.DefaultEndGuard, w.Duration);
    }

    [Fact]
    public void ToSourcePosition_AddsStartAndClampsToGuardedEnd()
    {
        var w = ClipWindow.FromOffsets(Sec(80), Sec(10), Sec(100)); // window [80, 90], duration 10

        Assert.Equal(Sec(82), w.ToSourcePosition(Sec(2)));
        // Beyond the window clamps to Duration − guard, offset by Start.
        Assert.Equal(Sec(80) + (Sec(10) - ClipWindow.DefaultEndGuard), w.ToSourcePosition(Sec(99)));
        // Negative relative clamps to start.
        Assert.Equal(Sec(80), w.ToSourcePosition(Sec(-3)));
    }

    [Fact]
    public void ToSourcePosition_Unbounded_JustAddsStart()
    {
        var w = ClipWindow.FromOffsets(Sec(5), TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(Sec(12), w.ToSourcePosition(Sec(7)));
    }

    [Fact]
    public void ToRelativePosition_SubtractsStartAndClampsToDuration()
    {
        var w = ClipWindow.FromOffsets(Sec(80), Sec(10), Sec(100));

        Assert.Equal(Sec(2), w.ToRelativePosition(Sec(82)));
        Assert.Equal(TimeSpan.Zero, w.ToRelativePosition(Sec(70))); // before start
        Assert.Equal(Sec(10), w.ToRelativePosition(Sec(95)));       // past end clamps to duration
    }

    [Fact]
    public void IsAtEnd_FiresWithinGuardOfEnd_NeverForUnbounded()
    {
        var w = ClipWindow.FromOffsets(Sec(80), Sec(10), Sec(100)); // end = 90s

        Assert.False(w.IsAtEnd(Sec(89)));
        Assert.True(w.IsAtEnd(Sec(90)));
        Assert.True(w.IsAtEnd(Sec(89.97))); // within the 50 ms guard

        var unbounded = ClipWindow.FromOffsets(Sec(0), TimeSpan.Zero, TimeSpan.Zero);
        Assert.False(unbounded.IsAtEnd(Sec(99999)));
    }
}
