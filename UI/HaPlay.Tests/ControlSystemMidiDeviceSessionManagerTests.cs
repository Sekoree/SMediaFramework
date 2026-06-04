using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using PMLib.MessageTypes;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlSystemMidiDeviceSessionManagerTests
{
    [Fact]
    public async Task Start_OpensMultipleMidiInputsAndDispatchesDecodedMessages()
    {
        var xtouchId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 50);
        var oscSender = new RecordingOscSender();
        var provider = new FakeMidiDeviceProvider
        {
            Inputs =
            [
                new ControlMidiPortInfo(1, "X-Touch MINI"),
                new ControlMidiPortInfo(2, "Backup MIDI In"),
            ],
        };
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OscListeners = [],
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", inputId: 1, inputName: "X-Touch MINI"),
                MidiDevice(backupId, "Backup Surface", "backup", inputId: 2, inputName: "Backup MIDI In"),
                OscDevice(x32Id, "X32", "x32", "127.0.0.1", 10023),
            ],
            Scripts =
            [
                MidiControlScript(xtouchId, "Scripts/xtouch.mnd", controller: 16, address: "/xtouch"),
                MidiNoteScript(backupId, "Scripts/backup.mnd", note: 84, address: "/backup"),
            ],
        };
        var runtime = CreateRuntimeSession(config, oscSender, monitor);
        var manager = new ControlMidiDeviceManager(config, runtime, monitor);
        using var midi = new ControlSystemMidiDeviceSessionManager(config, monitor, provider);

        midi.Start(manager);
        provider.Input(1).Raise(new ControlChange(0, 16, 10));
        provider.Input(2).Raise(new NoteOn(1, 84, 127));

        await WaitUntilAsync(() => oscSender.Sent.Count == 2);

        Assert.Equal([1, 2], provider.OpenedInputIds);
        Assert.Collection(
            oscSender.Sent,
            sent =>
            {
                Assert.Equal("/xtouch", sent.Address);
                Assert.Equal(10, Assert.Single(sent.Arguments).AsInt32());
            },
            sent =>
            {
                Assert.Equal("/backup", sent.Address);
                Assert.Equal(127, Assert.Single(sent.Arguments).AsInt32());
            });
        Assert.Contains(monitor.Records, r =>
            r.Protocol == ControlMonitorProtocol.Midi
            && r.Direction == ControlMonitorDirection.Input
            && r.DeviceInstanceId == xtouchId
            && r.MidiController == 16
            && r.MidiValue == 10
            && r.RawBytes is [0xB0, 16, 10]);
        Assert.Contains(monitor.Records, r =>
            r.Protocol == ControlMonitorProtocol.Midi
            && r.Direction == ControlMonitorDirection.Input
            && r.DeviceInstanceId == backupId
            && r.MidiNote == 84
            && r.MidiValue == 127
            && r.RawBytes is [0x91, 84, 127]);
    }

    [Fact]
    public async Task SendAsync_RoutesMidiOutputByControlDeviceInstanceId()
    {
        var xtouchId = Guid.NewGuid();
        var provider = new FakeMidiDeviceProvider
        {
            Outputs = [new ControlMidiPortInfo(7, "X-Touch MIDI Out")],
        };
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", outputId: 7, outputName: "X-Touch MIDI Out"),
            ],
        };
        using var midi = new ControlSystemMidiDeviceSessionManager(config, new ControlMonitorBuffer(maxRecords: 10), provider);

        await midi.SendControlChangeAsync(xtouchId, channel: 2, controller: 16, value: 64, highResolution14Bit: false);
        await midi.SendNoteAsync(xtouchId, channel: 2, note: 89, velocity: 127, isNoteOn: true);
        await midi.SendProgramChangeAsync(xtouchId, channel: 2, program: 5);
        await midi.SendPitchBendAsync(xtouchId, channel: 2, value: 1234);

        Assert.Equal([7], provider.OpenedOutputIds);
        Assert.Collection(
            provider.Output(7).Messages,
            message =>
            {
                var cc = Assert.IsType<ControlChange>(message);
                Assert.Equal(1, cc.Channel);
                Assert.Equal(16, cc.Controller);
                Assert.Equal(64, cc.Value);
            },
            message =>
            {
                var note = Assert.IsType<NoteOn>(message);
                Assert.Equal(1, note.Channel);
                Assert.Equal(89, note.Note);
                Assert.Equal(127, note.Velocity);
            },
            message =>
            {
                var program = Assert.IsType<ProgramChange>(message);
                Assert.Equal(1, program.Channel);
                Assert.Equal(5, program.Program);
            },
            message =>
            {
                var bend = Assert.IsType<PitchBend>(message);
                Assert.Equal(1, bend.Channel);
                Assert.Equal(1234, bend.Value);
            });
    }

    [Fact]
    public async Task SendAsync_RejectsAmbiguousOutputName()
    {
        var xtouchId = Guid.NewGuid();
        var provider = new FakeMidiDeviceProvider
        {
            Outputs =
            [
                new ControlMidiPortInfo(7, "X-Touch MIDI Out"),
                new ControlMidiPortInfo(8, "X-Touch MIDI Out"),
            ],
        };
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MidiDevice(xtouchId, "X-Touch Mini", "xtouch", outputName: "X-Touch MIDI Out"),
            ],
        };
        using var midi = new ControlSystemMidiDeviceSessionManager(config, new ControlMonitorBuffer(maxRecords: 10), provider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await midi.SendControlChangeAsync(xtouchId, channel: 1, controller: 16, value: 64, highResolution14Bit: false));

        Assert.Contains("ambiguous", ex.Message);
        Assert.Empty(provider.OpenedOutputIds);
    }

    [Fact]
    public async Task RuntimeSession_StartAndStop_OwnMidiSessionRunner()
    {
        var runner = new RecordingMidiSessionRunner();
        await using var session = new ControlSystemRuntimeSession(
            new ControlSystemConfig { IsArmed = true, OscListeners = [] },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>()),
            new RecordingOscSender(),
            monitor: new ControlMonitorBuffer(maxRecords: 10),
            tickInterval: TimeSpan.FromMilliseconds(1000),
            midiSessions: runner);

        await session.StartAsync();

        Assert.True(runner.Started);
        Assert.Same(session.MidiDevices, runner.DeviceManager);

        await session.StopAsync();

        Assert.True(runner.Stopped);
    }

    private static ControlScriptRuntimeSession CreateRuntimeSession(
        ControlSystemConfig config,
        RecordingOscSender sender,
        IControlMonitorSink monitor)
    {
        var sources = config.Scripts.ToDictionary(
            s => s.ScriptPath,
            script => $$"""
                export fun onMidi(event, context) {
                    osc.send("x32", "{{script.Triggers[0].OscAddressPattern}}", osc.int32(event.value));
                }
                """);
        return new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(sources),
            sender,
            monitor: monitor);
    }

    private static ControlDeviceInstanceConfig MidiDevice(
        Guid id,
        string name,
        string alias,
        int? inputId = null,
        string? inputName = null,
        int? outputId = null,
        string? outputName = null) =>
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
                MidiOutputDeviceId = outputId,
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

    private static ControlScriptConfig MidiControlScript(Guid deviceId, string path, int controller, string address) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "MIDI control script",
            ScriptPath = path,
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
                    OscAddressPattern = address,
                },
            ],
        };

    private static ControlScriptConfig MidiNoteScript(Guid deviceId, string path, int note, string address) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "MIDI note script",
            ScriptPath = path,
            Scope = ControlScriptScope.Device,
            DeviceInstanceId = deviceId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.MidiNote,
                    FunctionName = "onMidi",
                    DeviceInstanceId = deviceId,
                    MidiChannel = 2,
                    MidiNote = note,
                    OscAddressPattern = address,
                },
            ],
        };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100; i++)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        Assert.True(predicate());
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

    private sealed class RecordingMidiSessionRunner : IControlMidiDeviceSessionRunner
    {
        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public ControlMidiDeviceManager? DeviceManager { get; private set; }

        public void Start(ControlMidiDeviceManager deviceManager, CancellationToken cancellationToken = default)
        {
            Started = true;
            DeviceManager = deviceManager;
        }

        public void Stop() => Stopped = true;
    }

    private sealed class FakeMidiDeviceProvider : IControlMidiDeviceProvider
    {
        private readonly Dictionary<int, FakeMidiInputDevice> _inputs = new();
        private readonly Dictionary<int, FakeMidiOutputDevice> _outputs = new();

        public bool RequiresPortMidiLibraryLease => false;

        public IReadOnlyList<ControlMidiPortInfo> Inputs { get; init; } = [];

        public IReadOnlyList<ControlMidiPortInfo> Outputs { get; init; } = [];

        public List<int> OpenedInputIds { get; } = new();

        public List<int> OpenedOutputIds { get; } = new();

        public void EnsureInitialized()
        {
        }

        public IReadOnlyList<ControlMidiPortInfo> GetInputDevices() => Inputs;

        public IReadOnlyList<ControlMidiPortInfo> GetOutputDevices() => Outputs;

        public IControlMidiInputDevice OpenInput(ControlMidiPortInfo port)
        {
            OpenedInputIds.Add(port.Id);
            var input = new FakeMidiInputDevice();
            _inputs.Add(port.Id, input);
            return input;
        }

        public IControlMidiOutputDevice OpenOutput(ControlMidiPortInfo port)
        {
            OpenedOutputIds.Add(port.Id);
            var output = new FakeMidiOutputDevice();
            _outputs.Add(port.Id, output);
            return output;
        }

        public FakeMidiInputDevice Input(int id) => _inputs[id];

        public FakeMidiOutputDevice Output(int id) => _outputs[id];
    }

    private sealed class FakeMidiInputDevice : IControlMidiInputDevice
    {
        public event EventHandler<IMIDIMessage>? MessageReceived;

        public void Raise(IMIDIMessage message) => MessageReceived?.Invoke(this, message);

        public void Dispose()
        {
        }
    }

    private sealed class FakeMidiOutputDevice : IControlMidiOutputDevice
    {
        public List<IMIDIMessage> Messages { get; } = new();

        public void Write(IMIDIMessage message) => Messages.Add(message);

        public void Dispose()
        {
        }
    }
}
