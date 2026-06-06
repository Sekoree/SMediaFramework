using PMLib.MessageTypes;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class MidiHighResolution14BitCombinerTests
{
    [Fact]
    public void CoarseThenFine_EmitsOneCombinedHighResolutionValue()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]);

        // Coarse (MSB) is held back until its fine (LSB) partner arrives.
        Assert.Null(combiner.Process(new ControlChange(0, 0, 100)));

        var combined = Assert.IsType<ControlChange>(combiner.Process(new ControlChange(0, 32, 50)));
        Assert.True(combined.IsHighResolution);
        Assert.Equal(0, combined.Controller);
        Assert.Equal((100 << 7) | 50, combined.Value); // 12850
        Assert.Equal(0, combined.Channel);
    }

    [Fact]
    public void FineWithoutCoarse_CombinesWithZeroMsb()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]);

        var combined = Assert.IsType<ControlChange>(combiner.Process(new ControlChange(0, 32, 50)));
        Assert.True(combined.IsHighResolution);
        Assert.Equal(50, combined.Value);
    }

    [Fact]
    public void LatestCoarseIsReusedForFineOnlyStreams()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]);
        Assert.Null(combiner.Process(new ControlChange(0, 0, 1)));            // MSB = 1
        Assert.Equal(128 + 10, ((ControlChange)combiner.Process(new ControlChange(0, 32, 10))!).Value);
        Assert.Equal(128 + 20, ((ControlChange)combiner.Process(new ControlChange(0, 32, 20))!).Value); // fine-only, MSB reused
    }

    [Fact]
    public void UnconfiguredController_PassesThroughUnchanged()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]); // only controller 0 is 14-bit

        var cc = Assert.IsType<ControlChange>(combiner.Process(new ControlChange(0, 1, 64)));
        Assert.False(cc.IsHighResolution);
        Assert.Equal(1, cc.Controller);
        Assert.Equal(64, cc.Value);

        // CC 33 would be the fine partner of coarse 1, which is not configured -> untouched.
        var fine = Assert.IsType<ControlChange>(combiner.Process(new ControlChange(0, 33, 64)));
        Assert.False(fine.IsHighResolution);
        Assert.Equal(33, fine.Controller);
    }

    [Fact]
    public void PerChannelState_IsIndependent()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]);
        Assert.Null(combiner.Process(new ControlChange(0, 0, 2)));   // ch0 MSB = 2
        Assert.Null(combiner.Process(new ControlChange(1, 0, 5)));   // ch1 MSB = 5

        Assert.Equal((2 << 7) | 9, ((ControlChange)combiner.Process(new ControlChange(0, 32, 9))!).Value);
        Assert.Equal((5 << 7) | 3, ((ControlChange)combiner.Process(new ControlChange(1, 32, 3))!).Value);
    }

    [Fact]
    public void NonControlChange_PassesThrough()
    {
        var combiner = new MidiHighResolution14BitCombiner([0]);
        IMIDIMessage note = new NoteOn(0, 60, 100);
        Assert.Same(note, combiner.Process(note));
    }

    [Fact]
    public void EmptyConfiguration_PassesEverythingThrough()
    {
        var combiner = new MidiHighResolution14BitCombiner([]);
        Assert.False(combiner.IsEnabled);
        IMIDIMessage cc = new ControlChange(0, 0, 100);
        Assert.Same(cc, combiner.Process(cc)); // coarse NOT swallowed when disabled
    }
}
