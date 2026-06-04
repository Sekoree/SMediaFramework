using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlSystemRuntimeSessionTests
{
    [Fact]
    public async Task TickAsync_RunsScriptPeriodicTriggersAndDevicePeriodicOscSends()
    {
        var x32Id = Guid.NewGuid();
        var scriptId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = new ControlSystemRuntimeSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = x32Id,
                        Name = "X32",
                        Protocol = ControlDeviceProtocol.Osc,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "x32",
                            OscHost = "192.168.2.76",
                            OscPort = 10023,
                        },
                        PeriodicOscSends =
                        [
                            new ControlPeriodicOscSendConfig
                            {
                                Name = "/xremote",
                                Address = "/xremote",
                                IntervalMs = 8000,
                            },
                        ],
                    },
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = scriptId,
                        Name = "Heartbeat",
                        ScriptPath = "Scripts/heartbeat.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Id = triggerId,
                                Kind = ControlScriptTriggerKind.Periodic,
                                FunctionName = "tick",
                                IntervalMs = 8000,
                            },
                        ],
                    },
                ],
            },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/heartbeat.mnd"] =
                    """
                    export fun tick(event, context) {
                        osc.send("x32", "/heartbeat");
                    }
                    """,
            }),
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = await session.TickAsync(now);
        var tooSoon = await session.TickAsync(now.AddMilliseconds(7999));
        var second = await session.TickAsync(now.AddMilliseconds(8000));

        Assert.True(Assert.Single(first.ScriptResult.Invocations).Succeeded);
        Assert.True(Assert.Single(first.PeriodicOscResults).Succeeded);
        Assert.Empty(tooSoon.ScriptResult.Invocations);
        Assert.Empty(tooSoon.PeriodicOscResults);
        Assert.True(Assert.Single(second.ScriptResult.Invocations).Succeeded);
        Assert.True(Assert.Single(second.PeriodicOscResults).Succeeded);
        Assert.Collection(
            sender.Sent,
            sent => Assert.Equal("/heartbeat", sent.Address),
            sent => Assert.Equal("/xremote", sent.Address),
            sent => Assert.Equal("/heartbeat", sent.Address),
            sent => Assert.Equal("/xremote", sent.Address));
    }

    [Fact]
    public async Task ExposesMidiDispatcherForDecodedInput()
    {
        var midiId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = new ControlSystemRuntimeSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = midiId,
                        Name = "X-Touch Mini",
                        Protocol = ControlDeviceProtocol.Midi,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "xtouch",
                            MidiInputDeviceName = "X-Touch MINI",
                        },
                    },
                    new ControlDeviceInstanceConfig
                    {
                        Id = x32Id,
                        Name = "X32",
                        Protocol = ControlDeviceProtocol.Osc,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "x32",
                            OscHost = "192.168.2.76",
                            OscPort = 10023,
                        },
                    },
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Layer button",
                        ScriptPath = "Scripts/layer-button.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = midiId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiNote,
                                FunctionName = "onLayerButton",
                                DeviceInstanceId = midiId,
                                MidiChannel = 1,
                                MidiNote = 84,
                            },
                        ],
                    },
                ],
            },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/layer-button.mnd"] =
                    """
                    export fun onLayerButton(event, context) {
                        osc.send("x32", "/layer", osc.int32(event.midi.note));
                    }
                    """,
            }),
            sender);

        var results = await session.MidiDevices.DispatchNoteAsync(
            new ControlMidiInputIdentity(DeviceName: "X-Touch MINI"),
            channel: 1,
            note: 84,
            velocity: 127,
            isNoteOn: true);

        Assert.True(Assert.Single(results).ScriptResult.Invocations.Single().Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/layer", sent.Address);
        Assert.Equal(84, Assert.Single(sent.Arguments).AsInt32());
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
