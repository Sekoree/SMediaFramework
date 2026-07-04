using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlMIDIDeviceManagerTests
{
    [Fact]
    public async Task DispatchControlChangeAsync_RoutesMatchingMIDIInputToDeviceScript()
    {
        var xtouchId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOSCSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MIDIDevice(xtouchId, "X-Touch Mini", "xtouch", inputName: "X-Touch MINI"),
                MIDIDevice(backupId, "Backup Surface", "backup", inputName: "Backup MIDI In"),
                OSCDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts =
            [
                MIDIScript(xtouchId, "Scripts/xtouch.mnd", controller: 16),
                MIDIScript(backupId, "Scripts/backup.mnd", controller: 16),
            ],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMIDIDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMIDIInputIdentity(DeviceName: "X-Touch MINI"),
            channel: 1,
            controller: 16,
            value: 10);

        var result = Assert.Single(results);
        Assert.Equal(xtouchId, result.DeviceInstanceId);
        Assert.True(Assert.Single(result.ScriptResult.Invocations).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(10, Assert.Single(sent.Arguments).AsInt32());

        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.MIDI && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
        Assert.Equal("xtouch", input.DeviceKey);
        Assert.Equal("X-Touch MINI", input.Endpoint);
        Assert.Equal(1, input.MIDIChannel);
        Assert.Equal(16, input.MIDIController);
        Assert.Equal(10, input.MIDIValue);
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
                MIDIDevice(firstId, "First", "first", inputId: 1, inputName: "Shared Name"),
                MIDIDevice(secondId, "Second", "second", inputId: 2, inputName: "Shared Name"),
            ],
            Scripts = [MIDIScript(secondId, "Scripts/second.mnd", controller: 17)],
        };
        var session = CreateRuntimeSession(config, new RecordingOSCSender(), new ControlMonitorBuffer(maxRecords: 10));
        var manager = new ControlMIDIDeviceManager(config, session);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMIDIInputIdentity(DeviceId: 2, DeviceName: "Shared Name"),
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
        var sender = new RecordingOSCSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MIDIDevice(xtouchId, "X-Touch Mini", "xtouch", inputId: 4, inputName: "XTouch Mini"),
                OSCDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts = [MIDIScript(xtouchId, "Scripts/xtouch.mnd", controller: 16)],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMIDIDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMIDIInputIdentity(DeviceId: 9, DeviceName: "X-Touch MINI"),
            channel: 1,
            controller: 16,
            value: 10);

        Assert.Equal(xtouchId, Assert.Single(results).DeviceInstanceId);
        Assert.Equal("/seen", Assert.Single(sender.Sent).Address);
        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.MIDI && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
    }

    [Fact]
    public async Task DispatchControlChangeAsync_RecordsDroppedInputWhenNoDeviceMatches()
    {
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices = [MIDIDevice(Guid.NewGuid(), "X-Touch Mini", "xtouch", inputName: "X-Touch MINI")],
        };
        var session = CreateRuntimeSession(config, new RecordingOSCSender(), monitor);
        var manager = new ControlMIDIDeviceManager(config, session, monitor);

        var results = await manager.DispatchControlChangeAsync(
            new ControlMIDIInputIdentity(DeviceName: "Other MIDI"),
            channel: 1,
            controller: 16,
            value: 10);

        Assert.Empty(results);
        var dropped = Assert.Single(monitor.Records);
        Assert.Equal(ControlMonitorDirection.Dropped, dropped.Direction);
        Assert.Equal(ControlMonitorProtocol.MIDI, dropped.Protocol);
        Assert.Equal("No matching MIDI device", dropped.Message);
        Assert.Equal("Other MIDI", dropped.Endpoint);
    }

    [Fact]
    public async Task DispatchNoteAsync_RoutesMatchingMIDIInputToNoteScript()
    {
        var xtouchId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOSCSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                MIDIDevice(xtouchId, "X-Touch Mini", "xtouch", inputName: "X-Touch MINI"),
                OSCDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts = [MIDINoteScript(xtouchId, "Scripts/layer-button.mnd", note: 84)],
        };
        var session = CreateRuntimeSession(config, sender, monitor);
        var manager = new ControlMIDIDeviceManager(config, session, monitor);

        var results = await manager.DispatchNoteAsync(
            new ControlMIDIInputIdentity(DeviceName: "X-Touch MINI"),
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

        var input = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.MIDI && r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(xtouchId, input.DeviceInstanceId);
        Assert.Equal("xtouch", input.DeviceKey);
        Assert.Equal(84, input.MIDINote);
        Assert.Equal(127, input.MIDIValue);
        Assert.Equal(nameof(ControlScriptMIDIMessageKind.NoteOn), input.Message);
    }

    private static ControlScriptRuntimeSession CreateRuntimeSession(
        ControlSystemConfig config,
        RecordingOSCSender sender,
        IControlMonitorSink monitor)
    {
        var sources = config.Scripts.ToDictionary(
            s => s.ScriptPath,
            _ =>
                """
                export fun onMIDI(event, context) {
                    osc.send("x32", "/seen", osc.int32(event.value));
                }
                """);
        return new ControlScriptRuntimeSession(config, new InMemoryControlScriptSourceProvider(sources), sender, monitor: monitor);
    }

    private static ControlDeviceInstanceConfig MIDIDevice(
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
            Protocol = ControlDeviceProtocol.MIDI,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MIDIInputDeviceId = inputId,
                MIDIInputDeviceName = inputName,
            },
        };

    private static ControlDeviceInstanceConfig OSCDevice(
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
            Protocol = ControlDeviceProtocol.OSC,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OSCHost = host,
                OSCPort = port,
            },
        };

    private static ControlScriptConfig MIDIScript(
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
                    Kind = ControlScriptTriggerKind.MIDIControlChange,
                    FunctionName = "onMIDI",
                    DeviceInstanceId = deviceId,
                    MIDIChannel = 1,
                    MIDIController = controller,
                },
            ],
        };

    private static ControlScriptConfig MIDINoteScript(
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
                    Kind = ControlScriptTriggerKind.MIDINote,
                    FunctionName = "onMIDI",
                    DeviceInstanceId = deviceId,
                    MIDIChannel = 1,
                    MIDINote = note,
                },
            ],
        };

    private sealed class RecordingOSCSender : IControlOSCSender
    {
        public List<SentOSCMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOSCMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOSCMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
