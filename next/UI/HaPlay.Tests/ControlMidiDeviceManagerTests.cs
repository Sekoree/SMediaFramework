using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlMidiDeviceManagerTests
{
    [Fact]
    public async Task DispatchControlChangeAsync_RoutesMatchingMidiInputToDeviceScript()
    {
        var xtouchId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOscSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", inputName: "X-Touch MINI"),
                MidiDevice(backupId, "Backup Surface", "backup", inputName: "Backup MIDI In"),
                OscDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts =
            [
                MidiScript(xtouchId, "Scripts/xtouch.mnd", controller: 16),
                MidiScript(backupId, "Scripts/backup.mnd", controller: 16),
            ],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMidiDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMidiInputIdentity(DeviceName: "X-Touch MINI"),
            channel: 1,
            controller: 16,
            value: 10);

        var result = Assert.Single(results);
        Assert.Equal(xtouchId, result.DeviceInstanceId);
        Assert.True(Assert.Single(result.ScriptResult.Invocations).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(10, Assert.Single(sent.Arguments).AsInt32());

        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.Midi && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
        Assert.Equal("xtouch", input.DeviceKey);
        Assert.Equal("X-Touch MINI", input.Endpoint);
        Assert.Equal(1, input.MidiChannel);
        Assert.Equal(16, input.MidiController);
        Assert.Equal(10, input.MidiValue);
    }

    [Fact]
    public async Task DispatchControlChangeAsync_UsesDeviceIdBeforeNameWhenBothAreAvailable()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MidiDevice(firstId, "First", "first", inputId: 1, inputName: "Shared Name"),
                MidiDevice(secondId, "Second", "second", inputId: 2, inputName: "Shared Name"),
            ],
            Scripts = [MidiScript(secondId, "Scripts/second.mnd", controller: 17)],
        };
        var session = CreateRuntimeSession(config, new RecordingOscSender(), new ControlMonitorBuffer(maxRecords: 10));
        var manager = new ControlMidiDeviceManager(config, session);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMidiInputIdentity(DeviceId: 2, DeviceName: "Shared Name"),
            channel: 1,
            controller: 17,
            value: 64);

        Assert.Equal(secondId, Assert.Single(results).DeviceInstanceId);
    }

    [Fact]
    public async Task DispatchControlChangeAsync_FallsBackToFuzzyNameWhenRememberedDeviceIdChanged()
    {
        var xtouchId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOscSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", inputId: 4, inputName: "XTouch Mini"),
                OscDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts = [MidiScript(xtouchId, "Scripts/xtouch.mnd", controller: 16)],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMidiDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMidiInputIdentity(DeviceId: 9, DeviceName: "X-Touch MINI"),
            channel: 1,
            controller: 16,
            value: 10);

        Assert.Equal(xtouchId, Assert.Single(results).DeviceInstanceId);
        Assert.Equal("/seen", Assert.Single(sender.Sent).Address);
        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.Midi && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
    }

    [Fact]
    public async Task DispatchControlChangeAsync_RecordsDroppedInputWhenNoDeviceMatches()
    {
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices = [MidiDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", inputName: "X-Touch MINI")],
        };
        var session = CreateRuntimeSession(config, new RecordingOscSender(), monitor);
        var manager = new ControlMidiDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMidiInputIdentity(DeviceName: "Other MIDI"),
            channel: 1,
            controller: 16,
            value: 10);

        Assert.Empty(results);
        var dropped = Assert.Single(monitor.Records);
        Assert.Equal(ControlMonitorDirection.Dropped, dropped.Direction);
        Assert.Equal(ControlMonitorProtocol.Midi, dropped.Protocol);
        Assert.Equal("No matching MIDI device", dropped.Message);
        Assert.Equal("Other MIDI", dropped.Endpoint);
    }

    [Fact]
    public async Task DispatchNoteAsync_RoutesMatchingMidiInputToNoteScript()
    {
        var xtouchId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOscSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", inputName: "X-Touch MINI"),
                OscDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts = [MidiNoteScript(xtouchId, "Scripts/layer-button.mnd", note: 84)],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMidiDeviceManager(config, session, monitor);

        var results = await manager.DispatchNoteAsync(
            new ControlMidiInputIdentity(DeviceName: "X-Touch MINI"),
            channel: 1,
            note: 84,
            velocity: 127,
            isNoteOn: true);

        var result = Assert.Single(results);
        Assert.Equal(xtouchId, result.DeviceInstanceId);
        Assert.Equal(84, result.Note);
        Assert.True(result.IsNoteOn);
        Assert.True(Assert.Single(result.ScriptResult.Invocations).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(127, Assert.Single(sent.Arguments).AsInt32());

        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.Midi && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
        Assert.Equal("xtouch", input.DeviceKey);
        Assert.Equal(84, input.MidiNote);
        Assert.Equal(127, input.MidiValue);
        Assert.Equal(nameof(ControlScriptMidiMessageKind.NoteOn), input.Message);
    }

    private static ControlScriptRuntimeSession CreateRuntimeSession(
        ControlSystemConfig config,
        RecordingOscSender sender,
        IControlMonitorSink monitor)
    {
        var sources = config.Scripts.ToDictionary(
            s => s.ScriptPath,
            _ =>
                """
                export fun onMidi(event, context) {
                    osc.send("x32", "/seen", osc.int32(event.value));
                }
                """);
        return new ControlScriptRuntimeSession(config, new InMemoryControlScriptSourceProvider(sources), sender, monitor: monitor);
    }

    private static ControlDeviceInstanceConfig MidiDevice(
        Guid id,
        string name,
        string alias,
        int? inputId = null,
        string? inputName = null) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "generic-midi",
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MidiInputDeviceId = inputId,
                MidiInputDeviceName = inputName,
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

    private static ControlScriptConfig MidiScript(
        Guid deviceId,
        string scriptPath,
        int controller) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "MIDI script",
            ScriptPath = scriptPath,
            Scope = ControlScriptScope.Device,
            DeviceInstanceId = deviceId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.MidiControlChange,
                    FunctionName = "onMidi",
                    DeviceInstanceId = deviceId,
                    MidiChannel = 1,
                    MidiController = controller,
                },
            ],
        };

    private static ControlScriptConfig MidiNoteScript(
        Guid deviceId,
        string scriptPath,
        int note) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "MIDI note script",
            ScriptPath = scriptPath,
            Scope = ControlScriptScope.Device,
            DeviceInstanceId = deviceId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.MidiNote,
                    FunctionName = "onMidi",
                    DeviceInstanceId = deviceId,
                    MidiChannel = 1,
                    MidiNote = note,
                },
            ],
        };

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
