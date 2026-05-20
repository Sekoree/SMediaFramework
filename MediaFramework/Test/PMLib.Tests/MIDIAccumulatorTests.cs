using PMLib.Accumulators;
using PMLib.MessageTypes;
using Xunit;

namespace PMLib.Tests;

public sealed class MIDIAccumulatorTests
{
    [Fact]
    public void HighResCCAccumulator_CombinesCoarseAndFineControllers()
    {
        var acc = new HighResCCAccumulator();
        ControlChange? emitted = null;
        acc.HighResChanged += cc => emitted = cc;

        Assert.True(acc.Process(new ControlChange(channel: 2, controller: 7, value: 64)));
        Assert.True(acc.Process(new ControlChange(channel: 2, controller: 39, value: 3)));

        Assert.True(emitted.HasValue);
        var cc = emitted.GetValueOrDefault();
        Assert.Equal(2, cc.Channel);
        Assert.Equal(7, cc.Controller);
        Assert.True(cc.IsHighResolution);
        Assert.Equal((64 << 7) | 3, cc.Value);
    }

    [Fact]
    public void HighResCCAccumulator_FineWithoutCoarse_IsNotConsumed()
    {
        var acc = new HighResCCAccumulator();

        Assert.False(acc.Process(new ControlChange(channel: 0, controller: 32, value: 1)));
    }

    [Fact]
    public void NRPNAccumulator_EmitsCompleteFourControllerSequence()
    {
        var acc = new NRPNAccumulator();
        NRPN? emitted = null;
        acc.NRPNReceived += nrpn => emitted = nrpn;

        Assert.True(acc.Process(new ControlChange(1, 99, 2)));
        Assert.True(acc.Process(new ControlChange(1, 98, 3)));
        Assert.True(acc.Process(new ControlChange(1, 6, 4)));
        Assert.True(acc.Process(new ControlChange(1, 38, 5)));

        Assert.True(emitted.HasValue);
        var nrpn = emitted.GetValueOrDefault();
        Assert.Equal(1, nrpn.Channel);
        Assert.Equal((2 << 7) | 3, nrpn.Parameter);
        Assert.Equal((4 << 7) | 5, nrpn.Value);
    }

    [Fact]
    public void NRPNAccumulator_FlushesPendingDataMsbAsSevenBitValue()
    {
        var acc = new NRPNAccumulator();
        NRPN? emitted = null;
        acc.NRPNReceived += nrpn => emitted = nrpn;

        Assert.True(acc.Process(new ControlChange(0, 99, 1)));
        Assert.True(acc.Process(new ControlChange(0, 98, 2)));
        Assert.True(acc.Process(new ControlChange(0, 6, 3)));
        Assert.False(acc.Process(new ControlChange(0, 10, 64)));

        Assert.True(emitted.HasValue);
        var nrpn = emitted.GetValueOrDefault();
        Assert.Equal((1 << 7) | 2, nrpn.Parameter);
        Assert.Equal(3 << 7, nrpn.Value);
    }
}
