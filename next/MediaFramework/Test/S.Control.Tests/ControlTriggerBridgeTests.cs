using OSCLib;
using S.Control;
using S.Media.Core.Triggers;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlTriggerBridgeTests
{
    [Fact]
    public void MidiProfileResolvesMappedMessages()
    {
        var profile = new ControlMidiTriggerProfile()
            .MapNoteOn(0, 60, "pad.kick.fire")
            .MapControlChange(0, 7, "out.main.gain")
            .MapProgramChange(0, 3, "patch.lead");

        Assert.True(profile.TryResolveNoteOn(0, 60, out var noteId));
        Assert.Equal("pad.kick.fire", noteId);
        Assert.True(profile.TryResolveControlChange(0, 7, out var ccId));
        Assert.Equal("out.main.gain", ccId);
        Assert.True(profile.TryResolveProgramChange(0, 3, out var programId));
        Assert.Equal("patch.lead", programId);
    }

    [Fact]
    public void OscPayloadMapsFirstArgument()
    {
        var message = new OSCMessage("/pad/kick", [OSCArgument.Float32(0.5f)]);
        var payload = ControlOscTriggerBridge.MapMessageToPayload(message);
        Assert.Equal(TriggerValueKind.Numeric, payload.Kind);
        Assert.Equal(0.5, payload.NumericValue, 3);
    }

    [Fact]
    public void OscBridgeRegistersAndDisposesHandler()
    {
        var bus = new TriggerBus();
        using var server = new OSCServer(new OSCServerOptions { Port = 0 });
        using var bridge = new ControlOscTriggerBridge(bus, server);
        bus.Register("/x", (in TriggerPayload _) => { });

        Assert.True(bus.Fire("/x"));
    }
}
