using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class LayerOpacityTweenTests
{
    [Fact]
    public void AtStart_ReturnsStartOpacity()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1));
        Assert.Equal(0f, t.OpacityAt(TimeSpan.Zero));
        Assert.Equal(0f, t.OpacityAt(TimeSpan.FromMilliseconds(-50)));
    }

    [Fact]
    public void PastEnd_ReturnsEndOpacity()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1));
        Assert.Equal(1f, t.OpacityAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(1f, t.OpacityAt(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Linear_Midpoint_IsHalf()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1), LayerEasing.Linear);
        var mid = t.OpacityAt(TimeSpan.FromMilliseconds(500));
        Assert.InRange(mid, 0.49f, 0.51f);
    }

    [Fact]
    public void EaseInOutSine_Midpoint_IsHalf()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1), LayerEasing.EaseInOutSine);
        var mid = t.OpacityAt(TimeSpan.FromMilliseconds(500));
        Assert.InRange(mid, 0.49f, 0.51f);
    }

    [Fact]
    public void EaseInOutSine_Quarter_IsBelowLinear()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1), LayerEasing.EaseInOutSine);
        var q = t.OpacityAt(TimeSpan.FromMilliseconds(250));
        // Linear would give 0.25; ease-in-out-sine is shallower in the first half.
        Assert.True(q < 0.25f, $"ease-in-out-sine at 0.25 should be < 0.25, got {q}");
    }

    [Fact]
    public void EaseInOutCubic_Quarter_IsBelowLinear()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1), LayerEasing.EaseInOutCubic);
        var q = t.OpacityAt(TimeSpan.FromMilliseconds(250));
        Assert.True(q < 0.25f, $"ease-in-out-cubic at 0.25 should be < 0.25, got {q}");
    }

    [Fact]
    public void ZeroDuration_JumpsToEnd()
    {
        var t = new LayerOpacityTween(0.2f, 0.7f, TimeSpan.Zero);
        Assert.Equal(0.7f, t.OpacityAt(TimeSpan.Zero));
    }

    [Fact]
    public void IsComplete_TrueAfterDuration()
    {
        var t = new LayerOpacityTween(0f, 1f, TimeSpan.FromSeconds(1));
        Assert.False(t.IsComplete(TimeSpan.FromMilliseconds(500)));
        Assert.True(t.IsComplete(TimeSpan.FromSeconds(1)));
        Assert.True(t.IsComplete(TimeSpan.FromSeconds(2)));
    }
}
