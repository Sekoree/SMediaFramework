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
    public async Task OscInput_MapRange_MidiOutput_SendsControlChange()
    {
        var oscEndpointId = Guid.NewGuid();
        var midiEndpointId = Guid.NewGuid();
        var oscNodeId = Guid.NewGuid();
        var mapNodeId = Guid.NewGuid();
        var midiNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = oscNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings
                    {
                        EndpointId = oscEndpointId,
                        AddressPattern = "/ch/01/mix/fader",
                    },
                },
                new ControlNodeConfig
                {
                    Id = mapNodeId,
                    Kind = ControlNodeKind.MapRange,
                    Settings = new MapRangeControlNodeSettings
                    {
                        InputMin = 0,
                        InputMax = 1,
                        OutputMin = 0,
                        OutputMax = 16383,
                    },
                },
                new ControlNodeConfig
                {
                    Id = midiNodeId,
                    Kind = ControlNodeKind.MidiOutput,
                    Settings = new MidiOutputControlNodeSettings
                    {
                        EndpointId = midiEndpointId,
                        Channel = 1,
                        Controller = 0,
                        HighResolution14Bit = true,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = oscNodeId, ToNodeId = mapNodeId },
                new ControlConnectionConfig { FromNodeId = mapNodeId, ToNodeId = midiNodeId },
            ],
        };
        var midiSender = new RecordingMidiSender();
        var runtime = new ControlGraphRuntime(graph, new RecordingOscSender(), midiSender);

        await runtime.InjectOscMessageAsync(oscNodeId, "/ch/01/mix/fader", [OSCArgument.Float32(0.5f)], originId: oscEndpointId);

        var sent = Assert.Single(midiSender.Sent);
        Assert.Equal(midiEndpointId, sent.EndpointId);
        Assert.Equal(1, sent.Channel);
        Assert.Equal(0, sent.Controller);
        Assert.Equal(8192, sent.Value);
        Assert.True(sent.HighResolution14Bit);
    }

    [Fact]
    public async Task Output_WithDoNotEchoToOrigin_SuppressesEchoToSameEndpoint()
    {
        var endpointId = Guid.NewGuid();
        var inputNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings
                    {
                        EndpointId = endpointId,
                        AddressPattern = "/ch/01/mix/fader",
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings
                    {
                        EndpointId = endpointId,
                        Host = "192.168.1.50",
                        Address = "/ch/02/mix/fader",
                        FeedbackMode = ControlFeedbackMode.DoNotEchoToOrigin,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = outputNodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);

        await runtime.InjectOscMessageAsync(inputNodeId, "/ch/01/mix/fader", [OSCArgument.Float32(0.25f)], originId: endpointId);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Output_WithMinSendInterval_SuppressesRapidSecondSend()
    {
        var inputNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings { AddressPattern = "/in" },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings
                    {
                        Host = "127.0.0.1",
                        Address = "/out",
                        MinSendIntervalMs = 1000,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = outputNodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);

        await runtime.InjectOscMessageAsync(inputNodeId, "/in", [OSCArgument.Float32(0.1f)]);
        await runtime.InjectOscMessageAsync(inputNodeId, "/in", [OSCArgument.Float32(0.2f)]);

        var sent = Assert.Single(sender.Sent);
        Assert.InRange(Assert.Single(sent.Arguments).AsFloat32(), 0.09f, 0.11f);
    }

    [Fact]
    public async Task MidiOutput_MotorFeedbackOnly_SuppressesEventsFromSameMidiEndpoint()
    {
        var midiEndpointId = Guid.NewGuid();
        var inputNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.MidiInput,
                    Settings = new MidiInputControlNodeSettings
                    {
                        EndpointId = midiEndpointId,
                        Channel = 1,
                        Controller = 0,
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.MidiOutput,
                    Settings = new MidiOutputControlNodeSettings
                    {
                        EndpointId = midiEndpointId,
                        Channel = 1,
                        Controller = 0,
                        FeedbackMode = ControlFeedbackMode.MotorFeedbackOnly,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = outputNodeId },
            ],
        };
        var midiSender = new RecordingMidiSender();
        var runtime = new ControlGraphRuntime(graph, new RecordingOscSender(), midiSender);

        await runtime.InjectMidiControlChangeAsync(inputNodeId, channel: 1, controller: 0, value: 64);

        Assert.Empty(midiSender.Sent);
    }

    [Fact]
    public async Task MidiInput_WithSoftTakeover_SuppressesUntilTargetCrossed()
    {
        var inputNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.MidiInput,
                    Settings = new MidiInputControlNodeSettings
                    {
                        Channel = 1,
                        Controller = 0,
                        SoftTakeoverEnabled = true,
                        SoftTakeoverTolerance = 0.02,
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings
                    {
                        Host = "127.0.0.1",
                        Address = "/out",
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = outputNodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);
        runtime.SetSoftTakeoverTarget(inputNodeId, 0.5);

        await runtime.InjectMidiControlChangeAsync(inputNodeId, channel: 1, controller: 0, value: 10);
        await runtime.InjectMidiControlChangeAsync(inputNodeId, channel: 1, controller: 0, value: 64);
        await runtime.InjectMidiControlChangeAsync(inputNodeId, channel: 1, controller: 0, value: 80);

        Assert.Equal(2, sender.Sent.Count);
        Assert.InRange(Assert.Single(sender.Sent[0].Arguments).AsFloat32(), 63.9f, 64.1f);
        Assert.InRange(Assert.Single(sender.Sent[1].Arguments).AsFloat32(), 79.9f, 80.1f);
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

    private sealed class RecordingMidiSender : IControlMidiSender
    {
        public List<SentMidiControlChange> Sent { get; } = new();

        public ValueTask SendControlChangeAsync(
            Guid? endpointId,
            int channel,
            int controller,
            int value,
            bool highResolution14Bit,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiControlChange(endpointId, channel, controller, value, highResolution14Bit));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendNoteAsync(
            Guid? endpointId,
            int channel,
            int note,
            int velocity,
            bool isNoteOn,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendProgramChangeAsync(
            Guid? endpointId,
            int channel,
            int program,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SendPitchBendAsync(
            Guid? endpointId,
            int channel,
            int value,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed record SentMidiControlChange(
        Guid? EndpointId,
        int Channel,
        int Controller,
        int Value,
        bool HighResolution14Bit);
}
