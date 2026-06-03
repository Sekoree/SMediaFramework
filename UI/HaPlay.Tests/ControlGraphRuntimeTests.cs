using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlGraphRuntimeTests
{
    [Fact]
    public async Task MidiInput_MapRange_X32Fader_SendsOscFloat()
    {
        var midiNodeId = Guid.NewGuid();
        var mapNodeId = Guid.NewGuid();
        var x32NodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = midiNodeId,
                    Kind = ControlNodeKind.MidiInput,
                    Settings = new MidiInputControlNodeSettings
                    {
                        Channel = 1,
                        Controller = 0,
                        HighResolution14Bit = true,
                    },
                },
                new ControlNodeConfig
                {
                    Id = mapNodeId,
                    Kind = ControlNodeKind.MapRange,
                    Settings = new MapRangeControlNodeSettings
                    {
                        InputMin = 0,
                        InputMax = 16383,
                        OutputMin = 0,
                        OutputMax = 1,
                    },
                },
                new ControlNodeConfig
                {
                    Id = x32NodeId,
                    Kind = ControlNodeKind.X32ChannelFader,
                    Settings = new X32ChannelFaderControlNodeSettings
                    {
                        Host = "192.168.1.50",
                        Port = 10023,
                        Channel = 1,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = midiNodeId, ToNodeId = mapNodeId },
                new ControlConnectionConfig { FromNodeId = mapNodeId, ToNodeId = x32NodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);

        await runtime.InjectMidiControlChangeAsync(midiNodeId, channel: 1, controller: 0, value: 8192, highResolution14Bit: true);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("192.168.1.50", sent.Host);
        Assert.Equal(10023, sent.Port);
        Assert.Equal("/ch/01/mix/fader", sent.Address);
        var arg = Assert.Single(sent.Arguments);
        Assert.Equal(OSCArgumentType.Float32, arg.Type);
        Assert.InRange(arg.AsFloat32(), 0.499f, 0.501f);
    }

    [Fact]
    public void Validate_MissingNode_ReturnsIssue()
    {
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = Guid.NewGuid(),
                    Kind = ControlNodeKind.MidiInput,
                    Settings = new MidiInputControlNodeSettings(),
                },
            ],
            Connections =
            [
                new ControlConnectionConfig
                {
                    FromNodeId = Guid.NewGuid(),
                    ToNodeId = Guid.NewGuid(),
                },
            ],
        };

        var validation = ControlGraphRuntime.Validate(graph);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "missing-from-node");
    }

    [Fact]
    public void Bcf2000Preset_CreatesEightHighResolutionChannelSlots()
    {
        var layer = X32Presets.CreateDefaultBcf2000ChannelLayer(firstChannel: 9, firstController: 0);

        Assert.Equal("BCF2000 X32 Channels 1-8", layer.Name);
        Assert.Equal(8, layer.Slots.Count);
        Assert.All(layer.Slots, slot => Assert.True(slot.HighResolution14Bit));
        Assert.Equal(9, layer.Slots[0].TargetIndex);
        Assert.Equal(16, layer.Slots[7].TargetIndex);
        Assert.Equal("/ch/09/mix/fader", X32Presets.ChannelFaderAddress(layer.Slots[0].TargetIndex));
    }

    private sealed class RecordingOscSender : IControlOscSender
    {
        public List<SentOscMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOscMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
