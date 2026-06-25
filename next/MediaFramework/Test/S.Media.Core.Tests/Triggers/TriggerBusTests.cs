using Xunit;

namespace S.Media.Core.Tests.Triggers;

public sealed class TriggerBusTests
{
    [Fact]
    public void Fire_returns_false_when_unregistered()
    {
        var bus = new TriggerBus();
        Assert.False(bus.Fire("missing"));
    }

    [Fact]
    public void Fire_invokes_handler_with_numeric_payload()
    {
        var bus = new TriggerBus();
        double? seen = null;
        bus.Register("test.gain", (in TriggerPayload p) =>
        {
            Assert.Equal(TriggerValueKind.Numeric, p.Kind);
            seen = p.NumericValue;
        });
        Assert.True(bus.Fire("test.gain", TriggerPayload.FromNumeric(0.75)));
        Assert.Equal(0.75, seen);
    }

    [Fact]
    public void Unregister_removes_handler()
    {
        var bus = new TriggerBus();
        var count = 0;
        bus.Register("x", (in TriggerPayload _) => count++);
        Assert.True(bus.Unregister("x"));
        Assert.False(bus.Fire("x"));
        Assert.Equal(0, count);
    }
}
