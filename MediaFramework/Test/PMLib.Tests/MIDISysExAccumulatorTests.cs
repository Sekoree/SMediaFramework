using PMLib.MessageTypes;
using PMLib.Types;
using Xunit;

namespace PMLib.Tests;

public sealed class MIDISysExAccumulatorTests
{
    [Fact]
    public void Process_AccumulatesFragmentedSysExUntilEox()
    {
        var acc = new MIDISysExAccumulator();

        var msg = acc.Process(Event(0xF0, 0x7D, 0x01, 0x02), out var sysEx);
        Assert.Null(msg);
        Assert.Null(sysEx);
        Assert.True(acc.IsActive);

        msg = acc.Process(Event(0x03, 0x04, 0xF7, 0x00), out sysEx);

        Assert.Null(msg);
        Assert.True(sysEx.HasValue);
        var completed = sysEx.GetValueOrDefault();
        Assert.Equal(new byte[] { 0xF0, 0x7D, 0x01, 0x02, 0x03, 0x04, 0xF7 }, completed.Data);
        Assert.False(acc.IsActive);
    }

    [Fact]
    public void Process_RealTimeInsideSysEx_DoesNotDisruptAccumulation()
    {
        var acc = new MIDISysExAccumulator();
        Assert.Null(acc.Process(Event(0xF0, 0x7D, 0x01, 0x02), out var sysEx));
        Assert.Null(sysEx);

        var realtime = acc.Process(Event(0xF8, 0, 0, 0), out sysEx);
        Assert.IsType<TimingClock>(realtime);
        Assert.Null(sysEx);
        Assert.True(acc.IsActive);

        Assert.Null(acc.Process(Event(0x03, 0xF7, 0, 0), out sysEx));
        Assert.True(sysEx.HasValue);
        var completed = sysEx.GetValueOrDefault();
        Assert.Equal(new byte[] { 0xF0, 0x7D, 0x01, 0x02, 0x03, 0xF7 }, completed.Data);
    }

    [Fact]
    public void Process_SysExStartEvent_DoesNotAlsoDecodeAsChannelMessage()
    {
        var acc = new MIDISysExAccumulator();

        var msg = acc.Process(Event(0xF0, 0x01, 0xF7, 0), out var sysEx);

        Assert.Null(msg);
        Assert.NotNull(sysEx);
    }

    private static PmEvent Event(byte b0, byte b1, byte b2, byte b3) =>
        new()
        {
            Message = (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24))
        };
}
