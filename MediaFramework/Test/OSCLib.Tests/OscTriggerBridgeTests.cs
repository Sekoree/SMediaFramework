using OSCLib;
using S.Media.Core.Triggers;
using Xunit;

namespace OSCLib.Tests;

public sealed class OscTriggerBridgeTests
{
    [Fact]
    public void MapMessageToPayload_reads_first_float()
    {
        var msg = new OSCMessage("/pad/kick", [OSCArgument.Float32(0.5f)]);
        var p = OscTriggerBridge.MapMessageToPayload(msg);
        Assert.Equal(TriggerValueKind.Numeric, p.Kind);
        Assert.Equal(0.5, p.NumericValue, 3);
    }

    [Fact]
    public void Bridge_constructor_registers_without_throw()
    {
        var bus = new TriggerBus();
        var server = new OSCServer(new OSCServerOptions { Port = 0 });
        using var bridge = new OscTriggerBridge(bus, server);
        bus.Register("/x", (in TriggerPayload _) => { });
        Assert.True(bus.Fire("/x"));
    }
}
