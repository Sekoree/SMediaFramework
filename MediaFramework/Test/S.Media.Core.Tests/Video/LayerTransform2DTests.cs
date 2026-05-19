using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class LayerTransform2DTests
{
    [Fact]
    public void Identity_MapsPointsUnchanged()
    {
        var (x, y) = LayerTransform2D.Identity.Apply(3.5f, 7f);
        Assert.Equal(3.5f, x);
        Assert.Equal(7f, y);
    }

    [Fact]
    public void Translate_Shifts()
    {
        var t = LayerTransform2D.Translate(10f, -5f);
        var (x, y) = t.Apply(1f, 2f);
        Assert.Equal(11f, x);
        Assert.Equal(-3f, y);
    }

    [Fact]
    public void Scale_Multiplies()
    {
        var s = LayerTransform2D.Scale(2f, 3f);
        var (x, y) = s.Apply(4f, 5f);
        Assert.Equal(8f, x);
        Assert.Equal(15f, y);
    }

    [Fact]
    public void Rotate_90Deg_MapsXAxisToYAxis()
    {
        var r = LayerTransform2D.Rotate(MathF.PI / 2f);
        var (x, y) = r.Apply(1f, 0f);
        Assert.True(MathF.Abs(x) < 1e-5f, $"x should be ~0, got {x}");
        Assert.True(MathF.Abs(y - 1f) < 1e-5f, $"y should be ~1, got {y}");
    }

    [Fact]
    public void Compose_TranslateThenScale_AppliesBInner()
    {
        // a ∘ b means apply b first, then a.
        var b = LayerTransform2D.Translate(1f, 1f);
        var a = LayerTransform2D.Scale(2f, 2f);
        var c = LayerTransform2D.Compose(a, b);
        var (x, y) = c.Apply(0f, 0f);
        // Translate(1,1) first → (1,1). Then Scale(2,2) → (2,2).
        Assert.Equal(2f, x);
        Assert.Equal(2f, y);
    }

    [Fact]
    public void Invert_OfTranslateScale_RoundTrips()
    {
        var t = LayerTransform2D.Compose(
            LayerTransform2D.Translate(10f, 20f),
            LayerTransform2D.Scale(3f, 4f));
        var inv = t.Invert();
        var (x, y) = t.Apply(5f, 6f);
        var (rx, ry) = inv.Apply(x, y);
        Assert.True(MathF.Abs(rx - 5f) < 1e-4f, $"rx should be 5, got {rx}");
        Assert.True(MathF.Abs(ry - 6f) < 1e-4f, $"ry should be 6, got {ry}");
    }

    [Fact]
    public void Invert_OfSingular_Throws()
    {
        var degenerate = LayerTransform2D.Scale(0f, 1f);
        Assert.Throws<InvalidOperationException>(() => degenerate.Invert());
    }
}
