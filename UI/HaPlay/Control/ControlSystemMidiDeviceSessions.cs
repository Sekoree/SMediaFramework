using HaPlay.Models;
using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ControlGraph;

public interface IControlMidiDeviceSessionRunner
{
    void Start(ControlMidiDeviceManager deviceManager, CancellationToken cancellationToken = default);

    void Stop();
}

public sealed class ControlSystemMidiDeviceSessionManager : IControlMidiSender, IControlMidiDeviceSessionRunner, IDisposable
{
    private readonly object _gate = new();
    private readonly ControlSystemConfig _config;
    private readonly IControlMonitorSink _monitor;
    private readonly IControlMidiDeviceProvider _provider;
    private readonly Dictionary<int, LiveInputSession> _inputsByPortId = new();
    private readonly Dictionary<int, IControlMidiOutputDevice> _outputsByPortId = new();
    private readonly Dictionary<Guid, IControlMidiOutputDevice> _outputsByDeviceId = new();
    private ControlMidiLibraryLease? _midiLease;
    private bool _started;
    private bool _disposed;

    public ControlSystemMidiDeviceSessionManager(ControlSystemConfig config, IControlMonitorSink? monitor = null)
        : this(config, monitor, RealControlMidiDeviceProvider.Instance)
    {
    }

    internal ControlSystemMidiDeviceSessionManager(
        ControlSystemConfig config,
        IControlMonitorSink? monitor,
        IControlMidiDeviceProvider provider)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void Start(ControlMidiDeviceManager deviceManager, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(deviceManager);

        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
        }

        var inputDevices = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi && d.IsEnabled && HasInputBinding(d.Binding))
            .ToArray();
        if (inputDevices.Length == 0)
            return;

        IReadOnlyList<ControlMidiPortInfo> inputs;
        try
        {
            EnsureMidiInitialized();
            inputs = _provider.GetInputDevices();
        }
        catch (Exception ex)
        {
            _monitor.Record(CreateErrorRecord(
                device: null,
                endpoint: null,
                message: "MIDI input catalog failed.",
                errorMessage: ex.Message));
            return;
        }

        foreach (var device in inputDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = ControlDeviceMatcher.MatchMidiInput(device, inputs);
            if (!match.IsMatched)
            {
                _monitor.Record(CreateErrorRecord(
                    device,
                    FormatConfiguredInput(device.Binding),
                    "MIDI input device was not found.",
                    match.Message));
                continue;
            }

            var port = match.Port!;
            lock (_gate)
            {
                if (_inputsByPortId.ContainsKey(port.Id))
                    continue;
            }

            try
            {
                var input = _provider.OpenInput(port);
                var session = new LiveInputSession(port, input, deviceManager, _monitor);
                lock (_gate)
                {
                    if (_inputsByPortId.ContainsKey(port.Id))
                    {
                        session.Dispose();
                        continue;
                    }

                    _inputsByPortId.Add(port.Id, session);
                }

                _monitor.Record(new ControlMonitorRecord
                {
                    Direction = ControlMonitorDirection.Internal,
                    Protocol = ControlMonitorProtocol.Midi,
                    Result = ControlMonitorResult.Received,
                    DeviceInstanceId = device.Id,
                    DeviceKey = device.Binding.Alias ?? device.Name,
                    ProfileId = device.ProfileId,
                    Endpoint = FormatPort(port),
                    Message = "MIDI input opened.",
                });
            }
            catch (Exception ex)
            {
                _monitor.Record(CreateErrorRecord(
                    device,
                    FormatPort(port),
                    "MIDI input open failed.",
                    ex.Message));
            }
        }
    }

    public void Stop()
    {
        List<LiveInputSession> inputs;
        List<IControlMidiOutputDevice> outputs;
        lock (_gate)
        {
            inputs = _inputsByPortId.Values.ToList();
            outputs = _outputsByPortId.Values.ToList();
            _inputsByPortId.Clear();
            _outputsByPortId.Clear();
            _outputsByDeviceId.Clear();
            _started = false;
        }

        foreach (var input in inputs)
            input.Dispose();

        foreach (var output in outputs)
            output.Dispose();

        _midiLease?.Dispose();
        _midiLease = null;
    }

    public ValueTask SendControlChangeAsync(
        Guid? endpointId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = highResolution14Bit
            ? ControlChange.HighRes(ToZeroBasedMidiChannel(channel), checked((byte)controller), checked((ushort)value))
            : new ControlChange(ToZeroBasedMidiChannel(channel), checked((byte)controller), checked((byte)value));
        SendMessage(endpointId, message);
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
        cancellationToken.ThrowIfCancellationRequested();
        IMIDIMessage message = isNoteOn
            ? new NoteOn(ToZeroBasedMidiChannel(channel), checked((byte)note), checked((byte)velocity))
            : new NoteOff(ToZeroBasedMidiChannel(channel), checked((byte)note), checked((byte)velocity));
        SendMessage(endpointId, message);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendProgramChangeAsync(
        Guid? endpointId,
        int channel,
        int program,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendMessage(endpointId, new ProgramChange(ToZeroBasedMidiChannel(channel), checked((byte)program)));
        return ValueTask.CompletedTask;
    }

    public ValueTask SendPitchBendAsync(
        Guid? endpointId,
        int channel,
        int value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendMessage(endpointId, new PitchBend(ToZeroBasedMidiChannel(channel), value));
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void SendMessage(Guid? deviceInstanceId, IMIDIMessage message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (deviceInstanceId is not { } id)
            throw new InvalidOperationException("MIDI output requires a control device instance id.");

        var device = _config.Devices.FirstOrDefault(d => d.Id == id && d.Protocol == ControlDeviceProtocol.Midi && d.IsEnabled)
            ?? throw new InvalidOperationException($"MIDI device instance '{id}' was not found.");
        if (!HasOutputBinding(device.Binding))
            throw new InvalidOperationException($"MIDI device '{device.Name}' does not have an output binding.");

        lock (_gate)
        {
            var output = GetOutputLocked(device);
            output.Write(message);
        }
    }

    private IControlMidiOutputDevice GetOutputLocked(ControlDeviceInstanceConfig device)
    {
        if (_outputsByDeviceId.TryGetValue(device.Id, out var existing))
            return existing;

        EnsureMidiInitialized();
        var outputs = _provider.GetOutputDevices();
        var match = ControlDeviceMatcher.MatchMidiOutput(device, outputs);
        if (!match.IsMatched)
            throw new InvalidOperationException(match.Message);

        var port = match.Port!;

        if (!_outputsByPortId.TryGetValue(port.Id, out var output))
        {
            output = _provider.OpenOutput(port);
            _outputsByPortId.Add(port.Id, output);
        }

        _outputsByDeviceId.Add(device.Id, output);
        return output;
    }

    private void EnsureMidiInitialized()
    {
        if (_provider.RequiresPortMidiLibraryLease)
            _midiLease ??= ControlMidiLibraryLease.Acquire();
        _provider.EnsureInitialized();
    }

    private static ControlMonitorRecord CreateErrorRecord(
        ControlDeviceInstanceConfig? device,
        string? endpoint,
        string message,
        string errorMessage) =>
        new()
        {
            Direction = ControlMonitorDirection.Error,
            Protocol = ControlMonitorProtocol.Midi,
            Result = ControlMonitorResult.Failed,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = endpoint,
            Message = message,
            ErrorMessage = errorMessage,
        };

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiInputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MidiInputDeviceName);

    private static bool HasOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiOutputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MidiOutputDeviceName);

    private static string? FormatConfiguredInput(ControlDeviceBindingConfig binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.MidiInputDeviceName))
            return binding.MidiInputDeviceName;
        return binding.MidiInputDeviceId?.ToString();
    }

    private static string FormatPort(ControlMidiPortInfo port) =>
        string.IsNullOrWhiteSpace(port.Name) ? port.Id.ToString() : $"{port.Name} ({port.Id})";

    private static byte ToZeroBasedMidiChannel(int channel) =>
        checked((byte)Math.Clamp(channel - 1, 0, 15));

    private sealed class LiveInputSession : IDisposable
    {
        private readonly ControlMidiPortInfo _port;
        private readonly IControlMidiInputDevice _input;
        private readonly ControlMidiDeviceManager _deviceManager;
        private readonly IControlMonitorSink _monitor;
        private bool _disposed;

        public LiveInputSession(
            ControlMidiPortInfo port,
            IControlMidiInputDevice input,
            ControlMidiDeviceManager deviceManager,
            IControlMonitorSink monitor)
        {
            _port = port;
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _monitor = monitor ?? NullControlMonitorSink.Instance;
            _input.MessageReceived += OnMessageReceived;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _input.MessageReceived -= OnMessageReceived;
            _input.Dispose();
        }

        private void OnMessageReceived(object? sender, IMIDIMessage message)
        {
            switch (message)
            {
                case ControlChange cc:
                    _ = DispatchControlChangeAsync(cc);
                    break;
                case NoteOn noteOn:
                    _ = DispatchNoteAsync(noteOn);
                    break;
                case NoteOff noteOff:
                    _ = DispatchNoteAsync(noteOff);
                    break;
            }
        }

        private async Task DispatchControlChangeAsync(ControlChange message)
        {
            try
            {
                await _deviceManager.DispatchControlChangeAsync(
                    new ControlMidiInputIdentity(_port.Id, _port.Name),
                    message.Channel + 1,
                    message.Controller,
                    message.Value,
                    message.IsHighResolution).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordDispatchFailure(ex.Message);
            }
        }

        private Task DispatchNoteAsync(NoteOn message) =>
            DispatchNoteAsync(message.Channel, message.Note, message.Velocity, message.Velocity > 0);

        private Task DispatchNoteAsync(NoteOff message) =>
            DispatchNoteAsync(message.Channel, message.Note, message.Velocity, isNoteOn: false);

        private async Task DispatchNoteAsync(byte channel, byte note, byte velocity, bool isNoteOn)
        {
            try
            {
                await _deviceManager.DispatchNoteAsync(
                    new ControlMidiInputIdentity(_port.Id, _port.Name),
                    channel + 1,
                    note,
                    velocity,
                    isNoteOn).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordDispatchFailure(ex.Message);
            }
        }

        private void RecordDispatchFailure(string errorMessage) =>
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Midi,
                Result = ControlMonitorResult.Failed,
                Endpoint = FormatPort(_port),
                Message = "MIDI input dispatch failed.",
                ErrorMessage = errorMessage,
            });
    }
}

internal interface IControlMidiDeviceProvider
{
    bool RequiresPortMidiLibraryLease { get; }

    void EnsureInitialized();

    IReadOnlyList<ControlMidiPortInfo> GetInputDevices();

    IReadOnlyList<ControlMidiPortInfo> GetOutputDevices();

    IControlMidiInputDevice OpenInput(ControlMidiPortInfo port);

    IControlMidiOutputDevice OpenOutput(ControlMidiPortInfo port);
}

internal interface IControlMidiInputDevice : IDisposable
{
    event EventHandler<IMIDIMessage>? MessageReceived;
}

internal interface IControlMidiOutputDevice : IDisposable
{
    void Write(IMIDIMessage message);
}

internal sealed class RealControlMidiDeviceProvider : IControlMidiDeviceProvider
{
    public static RealControlMidiDeviceProvider Instance { get; } = new();

    private RealControlMidiDeviceProvider()
    {
    }

    public bool RequiresPortMidiLibraryLease => true;

    public void EnsureInitialized()
    {
    }

    public IReadOnlyList<ControlMidiPortInfo> GetInputDevices() =>
        PMUtil.GetInputDevices().Select(d => new ControlMidiPortInfo(d.Id, d.Name)).ToArray();

    public IReadOnlyList<ControlMidiPortInfo> GetOutputDevices() =>
        PMUtil.GetOutputDevices().Select(d => new ControlMidiPortInfo(d.Id, d.Name)).ToArray();

    public IControlMidiInputDevice OpenInput(ControlMidiPortInfo port)
    {
        var device = new MIDIInputDevice(port.Id);
        var wrapper = new RealControlMidiInputDevice(device);
        var err = device.Open();
        if (err != PmError.NoError)
        {
            wrapper.Dispose();
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        return wrapper;
    }

    public IControlMidiOutputDevice OpenOutput(ControlMidiPortInfo port)
    {
        var device = new MIDIOutputDevice(port.Id);
        var err = device.Open();
        if (err != PmError.NoError)
        {
            device.Dispose();
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        return new RealControlMidiOutputDevice(device);
    }

    private sealed class RealControlMidiInputDevice : IControlMidiInputDevice
    {
        private readonly MIDIInputDevice _device;

        public RealControlMidiInputDevice(MIDIInputDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _device.MessageReceived += OnMessageReceived;
        }

        public event EventHandler<IMIDIMessage>? MessageReceived;

        public void Dispose()
        {
            _device.MessageReceived -= OnMessageReceived;
            _device.Dispose();
        }

        private void OnMessageReceived(object? sender, IMIDIMessage message) =>
            MessageReceived?.Invoke(this, message);
    }

    private sealed class RealControlMidiOutputDevice : IControlMidiOutputDevice
    {
        private readonly MIDIOutputDevice _device;

        public RealControlMidiOutputDevice(MIDIOutputDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public void Write(IMIDIMessage message)
        {
            var err = _device.Write(message);
            if (err != PmError.NoError)
                throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        public void Dispose() => _device.Dispose();
    }
}
