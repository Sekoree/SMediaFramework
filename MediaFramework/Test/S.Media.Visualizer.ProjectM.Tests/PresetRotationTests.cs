using S.Media.Visualizer.ProjectM;
using Xunit;

namespace S.Media.Visualizer.ProjectM.Tests;

/// <summary>
/// The failed-preset blocklist rotation shared by the continuous renderer and the layer surface:
/// broken presets must be skipped in the SAME advance call (no empty visualizer slot), and a
/// fully broken pack must stop rotating instead of spinning.
/// </summary>
public sealed class PresetRotationTests
{
    [Fact]
    public void SequentialAdvance_WrapsInOrder()
    {
        var rotation = new PresetRotation(["a", "b", "c"], shuffle: false);
        Assert.True(rotation.TryAdvance(out var p1));
        Assert.True(rotation.TryAdvance(out var p2));
        Assert.True(rotation.TryAdvance(out var p3));
        Assert.True(rotation.TryAdvance(out var p4));
        Assert.Equal(["a", "b", "c", "a"], new[] { p1, p2, p3, p4 });
    }

    [Fact]
    public void FailedPresets_AreSkippedOnSubsequentAdvances()
    {
        var rotation = new PresetRotation(["a", "b", "c"], shuffle: false);
        Assert.True(rotation.TryAdvance(out _));      // a
        Assert.True(rotation.MarkFailed("b"));
        Assert.False(rotation.MarkFailed("b"));       // logged-once contract
        Assert.Equal(1, rotation.FailedCount);

        Assert.True(rotation.TryAdvance(out var next));
        Assert.Equal("c", next);                      // b skipped in the same advance
        Assert.True(rotation.TryAdvance(out var wrap));
        Assert.Equal("a", wrap);
    }

    [Fact]
    public void AllFailed_StopsAdvancing()
    {
        var rotation = new PresetRotation(["a", "b"], shuffle: false);
        rotation.MarkFailed("a");
        rotation.MarkFailed("b");
        Assert.True(rotation.AllFailed);
        Assert.False(rotation.TryAdvance(out _));
    }

    [Fact]
    public void EmptyPack_NeverAdvancesAndNeverReportsAllFailed()
    {
        var rotation = new PresetRotation([], shuffle: false);
        Assert.False(rotation.TryAdvance(out _));
        Assert.False(rotation.AllFailed);
    }

    [Fact]
    public void ShuffledAdvance_AvoidsImmediateRepeatAndSkipsFailed()
    {
        var rotation = new PresetRotation(["a", "b", "c", "d"], shuffle: true);
        rotation.MarkFailed("c");

        Assert.True(rotation.TryAdvance(out var previous));
        for (var i = 0; i < 200; i++)
        {
            Assert.True(rotation.TryAdvance(out var current));
            Assert.NotEqual("c", current);
            Assert.NotEqual(previous, current); // no immediate repeats while >1 candidate remains
            previous = current;
        }
    }
}
