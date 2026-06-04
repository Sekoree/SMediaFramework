using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptRuntimeSessionTests
{
    [Fact]
    public async Task DispatchControlEventAsync_RunsStarterScriptAndRoutesOscToConfiguredX32()
    {
        var midiDeviceId = Guid.NewGuid();
        var x32DeviceId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    MidiDevice(midiDeviceId, "X-Touch Mini"),
                    OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023),
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "X-Touch faders",
                        ScriptPath = template.SuggestedPath,
                        DeviceInstanceId = midiDeviceId,
                        Scope = ControlScriptScope.Device,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiControlChange,
                                FunctionName = "onXTouchFaderEncoder",
                                DeviceInstanceId = midiDeviceId,
                                MidiChannel = 1,
                                MidiController = 16,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sender);

        var result = await session.DispatchControlEventAsync(MidiCcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.True(Assert.Single(result.OscRoutes).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("192.168.2.76", sent.Host);
        Assert.Equal(10023, sent.Port);
        Assert.Equal("/ch/01/mix/fader", sent.Address);
        Assert.Equal(0.75 + 10.0 / 1023.0, Assert.Single(sent.Arguments).AsFloat32(), precision: 6);
    }

    [Fact]
    public async Task TickPeriodicAsync_RunsDuePeriodicTriggerAndRoutesAtInterval()
    {
        var x32DeviceId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "XRemote",
                        ScriptPath = "Scripts/xremote.mnd",
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
            new Dictionary<string, string>
            {
                ["Scripts/xremote.mnd"] =
                    """
                    export fun tick(event, context) {
                        osc.send("x32", "/xremote");
                    }
                    """,
            },
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = await session.TickPeriodicAsync(now);
        var tooSoon = await session.TickPeriodicAsync(now.AddMilliseconds(7999));
        var second = await session.TickPeriodicAsync(now.AddMilliseconds(8000));

        Assert.True(Assert.Single(first.Invocations).Succeeded);
        Assert.Empty(tooSoon.Invocations);
        Assert.True(Assert.Single(second.Invocations).Succeeded);
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, sent => Assert.Equal("/xremote", sent.Address));
    }

    [Fact]
    public async Task DispatchDeviceEnabledAsync_RoutesStartupRequestScript()
    {
        var x32DeviceId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Startup request",
                        ScriptPath = "Scripts/startup.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = x32DeviceId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.DeviceEnabled,
                                FunctionName = "onDeviceEnabled",
                                DeviceInstanceId = x32DeviceId,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/startup.mnd"] =
                    """
                    export fun onDeviceEnabled(event, context) {
                        osc.send("x32", "/ch/01/mix/fader");
                    }
                    """,
            },
            sender);

        var result = await session.DispatchDeviceEnabledAsync(x32DeviceId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.True(Assert.Single(result.OscRoutes).Succeeded);
        Assert.Equal("/ch/01/mix/fader", Assert.Single(sender.Sent).Address);
    }

    [Fact]
    public async Task DispatchManualAsync_ReturnsRouteFailureForUnknownOscDevice()
    {
        var sender = new RecordingOscSender();
        var scriptId = Guid.NewGuid();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = scriptId,
                        Name = "Bad route",
                        ScriptPath = "Scripts/bad-route.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/bad-route.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("missing", "/test", osc.float32(1));
                    }
                    """,
            },
            sender);

        var result = await session.DispatchManualAsync(scriptId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var route = Assert.Single(result.OscRoutes);
        Assert.False(route.Succeeded);
        Assert.Contains("No enabled OSC device matches key 'missing'", route.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchManualAsync_SharesOptimisticCacheBetweenRuntimeAndRouter()
    {
        var x32Id = Guid.NewGuid();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                Devices = [OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Cache send",
                        ScriptPath = "Scripts/cache-send.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/cache-send.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/ch/01/mix/fader", osc.float32(0.42));
                    }
                    """,
            },
            new RecordingOscSender());

        await session.DispatchManualAsync();

        Assert.True(session.OscCache.TryGetNumber("x32", "/ch/01/mix/fader", out var value));
        Assert.Equal(0.42, value, precision: 12);
    }

    [Fact]
    public async Task DispatchManualAsync_RoutesMidiOutputAndRecordsMonitorEntry()
    {
        var xtouchId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var midiSender = new RecordingMidiSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(xtouchId, "X-Touch Mini", alias: "xtouch", outputName: "X-Touch MINI")],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "MIDI send",
                        ScriptPath = "Scripts/midi-send.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/midi-send.mnd"] =
                    """
                    export fun run(event, context) {
                        midi.sendNoteOn("xtouch", 1, 89, 127);
                        midi.sendCc("xtouch", 1, 16, 64);
                    }
                    """,
            },
            new RecordingOscSender(),
            monitor,
            midiSender);

        var result = await session.DispatchManualAsync();

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Empty(result.OscRoutes);
        Assert.All(result.MidiRoutes, route => Assert.True(route.Succeeded));
        Assert.Collection(
            midiSender.Sent,
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.NoteOn, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(1, sent.Channel);
                Assert.Equal(89, sent.Note);
                Assert.Equal(127, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.ControlChange, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(16, sent.Controller);
                Assert.Equal(64, sent.Value);
            });

        var midiOutputRecords = monitor.Records
            .Where(r => r.Protocol == ControlMonitorProtocol.Midi && r.Direction == ControlMonitorDirection.Output)
            .ToArray();
        Assert.Equal(2, midiOutputRecords.Length);
        Assert.Contains(midiOutputRecords, r => r.Message == nameof(ControlScriptMidiMessageKind.NoteOn) && r.MidiNote == 89);
        Assert.Contains(midiOutputRecords, r => r.Message == nameof(ControlScriptMidiMessageKind.ControlChange) && r.MidiController == 16);
    }

    private static ControlScriptRuntimeSession CreateSession(
        ControlSystemConfig config,
        IReadOnlyDictionary<string, string> scripts,
        RecordingOscSender sender,
        IControlMonitorSink? monitor = null,
        IControlMidiSender? midiSender = null) =>
        new(config, new InMemoryControlScriptSourceProvider(scripts), sender, monitor: monitor, midiSender: midiSender);

    private static ControlDeviceInstanceConfig MidiDevice(
        Guid id,
        string name,
        string? alias = null,
        string? outputName = null) =>
        new()
        {
            Id = id,
            Name = name,
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MidiOutputDeviceName = outputName,
            },
        };

    private static ControlDeviceInstanceConfig OscDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "x32",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OscHost = host,
                OscPort = port,
            },
        };

    private static MidiControlEvent MidiCcEvent(Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

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
        public List<SentMidiMessage> Sent { get; } = new();

        public ValueTask SendControlChangeAsync(
            Guid? endpointId,
            int channel,
            int controller,
            int value,
            bool highResolution14Bit,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ControlChange,
                endpointId,
                channel,
                Controller: controller,
                Note: null,
                Value: value,
                HighResolution14Bit: highResolution14Bit));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendNoteAsync(
            Guid? endpointId,
            int channel,
            int note,
            int velocity,
            bool isNoteOn,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                isNoteOn ? ControlScriptMidiMessageKind.NoteOn : ControlScriptMidiMessageKind.NoteOff,
                endpointId,
                channel,
                Controller: null,
                Note: note,
                Value: velocity,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendProgramChangeAsync(
            Guid? endpointId,
            int channel,
            int program,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ProgramChange,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: program,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendPitchBendAsync(
            Guid? endpointId,
            int channel,
            int value,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.PitchBend,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: value,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentMidiMessage(
        ControlScriptMidiMessageKind Kind,
        Guid? EndpointId,
        int Channel,
        int? Controller,
        int? Note,
        int Value,
        bool HighResolution14Bit);
}
