using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace S.Control;

public interface IControlMIDIDeviceSessionRunner
{
    void Start(ControlMIDIDeviceManager deviceManager, CancellationToken cancellationToken = default);

    void Stop();
}

public sealed class ControlSystemMIDIDeviceSessionManager : IControlMIDISender, IControlMIDIDeviceSessionRunner, IDisposable
{
    private readonly object _gate = new();
    private readonly ControlSystemConfig _config;
    private readonly IControlMonitorSink _monitor;
    private readonly IControlMIDIDeviceProvider _provider;
    private readonly Dictionary<int, LiveInputSession> _inputsByPortId = new();
    private readonly Dictionary<int, IControlMIDIOutputDevice> _outputsByPortId = new();
    private readonly Dictionary<Guid, IControlMIDIOutputDevice> _outputsByDeviceId = new();
    private ControlMIDILibraryLease? _midiLease;
    private bool _started;
    private bool _disposed;

    public ControlSystemMIDIDeviceSessionManager(ControlSystemConfig config, IControlMonitorSink? monitor = null)
        : this(config, monitor, RealControlMIDIDeviceProvider.Instance)
    {
    }

    internal ControlSystemMIDIDeviceSessionManager(
        ControlSystemConfig config,
        IControlMonitorSink? monitor,
        IControlMIDIDeviceProvider provider)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void Start(ControlMIDIDeviceManager deviceManager, CancellationToken cancellationToken = default)
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
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled && HasInputBinding(d.Binding))
            .ToArray();
        if (inputDevices.Length == 0)
            return;

        IReadOnlyList<ControlMIDIPortInfo> inputs;
        try
        {
            EnsureMIDIInitialized();
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

        var profileRepository = CompositeControlDeviceProfileRepository.ForProject(_config);

        foreach (var device in inputDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = ControlDeviceMatcher.MatchMIDIInput(device, inputs);
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
                var session = new LiveInputSession(
                    port, input, deviceManager, _monitor,
                    ResolveHighResolution14BitControllers(profileRepository, device));
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
                    Protocol = ControlMonitorProtocol.MIDI,
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
        List<IControlMIDIOutputDevice> outputs;
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
            ? ControlChange.HighRes(ToZeroBasedMIDIChannel(channel), checked((byte)controller), checked((ushort)value))
            : new ControlChange(ToZeroBasedMIDIChannel(channel), checked((byte)controller), checked((byte)value));
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
            ? new NoteOn(ToZeroBasedMIDIChannel(channel), checked((byte)note), checked((byte)velocity))
            : new NoteOff(ToZeroBasedMIDIChannel(channel), checked((byte)note), checked((byte)velocity));
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
        SendMessage(endpointId, new ProgramChange(ToZeroBasedMIDIChannel(channel), checked((byte)program)));
        return ValueTask.CompletedTask;
    }

    public ValueTask SendPitchBendAsync(
        Guid? endpointId,
        int channel,
        int value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendMessage(endpointId, new PitchBend(ToZeroBasedMIDIChannel(channel), value));
        return ValueTask.CompletedTask;
    }

    public ValueTask SendMIDIMessageAsync(
        Guid? endpointId,
        ControlMIDIMessagePayload message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        SendMessage(endpointId, ToMIDIMessage(message));
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

        var device = _config.Devices.FirstOrDefault(d => d.Id == id && d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled)
            ?? throw new InvalidOperationException($"MIDI device instance '{id}' was not found.");
        if (!HasOutputBinding(device.Binding))
            throw new InvalidOperationException($"MIDI device '{device.Name}' does not have an output binding.");

        lock (_gate)
        {
            var output = GetOutputLocked(device);
            output.Write(message);
        }
    }

    private IControlMIDIOutputDevice GetOutputLocked(ControlDeviceInstanceConfig device)
    {
        if (_outputsByDeviceId.TryGetValue(device.Id, out var existing))
            return existing;

        EnsureMIDIInitialized();
        var outputs = _provider.GetOutputDevices();
        var match = ControlDeviceMatcher.MatchMIDIOutput(device, outputs);
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

    private void EnsureMIDIInitialized()
    {
        if (_provider.RequiresPortMIDILibraryLease)
            _midiLease ??= ControlMIDILibraryLease.Acquire();
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
            Protocol = ControlMonitorProtocol.MIDI,
            Result = ControlMonitorResult.Failed,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = endpoint,
            Message = message,
            ErrorMessage = errorMessage,
        };

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIInputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName);

    /// <summary>Coarse CC numbers (0–31) the device's profile marks as 14-bit (high-resolution) faders/encoders.
    /// Empty when the device has no profile or no 14-bit controls — the combiner then passes everything through.</summary>
    private static IReadOnlyList<int> ResolveHighResolution14BitControllers(
        IControlDeviceProfileRepository profileRepository,
        ControlDeviceInstanceConfig device)
    {
        if (string.IsNullOrWhiteSpace(device.ProfileId))
            return [];

        var profile = profileRepository.FindById(device.ProfileId);
        if (profile is null)
            return [];

        var controllers = new List<int>();
        foreach (var control in profile.Controls)
        {
            var is14Bit = control.ValueMode == ControlProfileValueMode.Absolute14Bit || control.MIDIHighResolution14Bit;
            if (is14Bit && control.MIDIController is int controller && controller is >= 0 and <= 31)
                controllers.Add(controller);
        }

        return controllers;
    }

    private static bool HasOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIOutputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName);

    private static string? FormatConfiguredInput(ControlDeviceBindingConfig binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName))
            return binding.MIDIInputDeviceName;
        return binding.MIDIInputDeviceId?.ToString();
    }

    private static string FormatPort(ControlMIDIPortInfo port) =>
        string.IsNullOrWhiteSpace(port.Name) ? port.Id.ToString() : $"{port.Name} ({port.Id})";

    private static byte ToZeroBasedMIDIChannel(int channel) =>
        checked((byte)Math.Clamp(channel - 1, 0, 15));

    private static IMIDIMessage ToMIDIMessage(ControlMIDIMessagePayload message) =>
        message.MessageType switch
        {
            ControlMIDIMessageType.ControlChange => message.HighResolution14Bit
                ? ControlChange.HighRes(
                    ToZeroBasedMIDIChannel(RequireChannel(message)),
                    ToSevenBit(nameof(message.Controller), Require(nameof(message.Controller), message.Controller)),
                    ToFourteenBit(nameof(message.Value), Require(nameof(message.Value), message.Value)))
                : new ControlChange(
                    ToZeroBasedMIDIChannel(RequireChannel(message)),
                    ToSevenBit(nameof(message.Controller), Require(nameof(message.Controller), message.Controller)),
                    ToSevenBit(nameof(message.Value), Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.NoteOn => new NoteOn(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToSevenBit(nameof(message.Note), Require(nameof(message.Note), message.Note)),
                ToSevenBit(nameof(message.Velocity), message.Velocity ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.NoteOff => new NoteOff(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToSevenBit(nameof(message.Note), Require(nameof(message.Note), message.Note)),
                ToSevenBit(nameof(message.Velocity), message.Velocity ?? message.Value ?? 0)),
            ControlMIDIMessageType.PolyphonicAftertouch => new PolyphonicAftertouch(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToSevenBit(nameof(message.Note), Require(nameof(message.Note), message.Note)),
                ToSevenBit(nameof(message.Pressure), message.Pressure ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.ProgramChange => new ProgramChange(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToSevenBit(nameof(message.Program), message.Program ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.ChannelAftertouch => new ChannelAftertouch(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToSevenBit(nameof(message.Pressure), message.Pressure ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.PitchBend => new PitchBend(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                message.PitchBend ?? Require(nameof(message.Value), message.Value)),
            ControlMIDIMessageType.SysEx => new SysEx(NormalizeSysExBytes(RequireData(message))),
            ControlMIDIMessageType.MIDITimeCode => new MIDITimeCode(
                ToSevenBit(nameof(message.DataByte), message.DataByte ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.SongPosition => new SongPosition(
                ToFourteenBit(nameof(message.SongPosition), message.SongPosition ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.SongSelect => new SongSelect(
                ToSevenBit(nameof(message.Song), message.Song ?? Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.TuneRequest => new TuneRequest(),
            ControlMIDIMessageType.TimingClock => new TimingClock(),
            ControlMIDIMessageType.Start => new MIDIStart(),
            ControlMIDIMessageType.Continue => new MIDIContinue(),
            ControlMIDIMessageType.Stop => new MIDIStop(),
            ControlMIDIMessageType.ActiveSensing => new ActiveSensing(),
            ControlMIDIMessageType.Reset => new MIDIReset(),
            ControlMIDIMessageType.NRPN => new NRPN(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToFourteenBit(nameof(message.Parameter), Require(nameof(message.Parameter), message.Parameter)),
                ToFourteenBit(nameof(message.Value), Require(nameof(message.Value), message.Value))),
            ControlMIDIMessageType.RPN => new RPN(
                ToZeroBasedMIDIChannel(RequireChannel(message)),
                ToFourteenBit(nameof(message.Parameter), Require(nameof(message.Parameter), message.Parameter)),
                ToFourteenBit(nameof(message.Value), Require(nameof(message.Value), message.Value))),
            _ => throw new InvalidOperationException($"Unsupported MIDI message type '{message.MessageType}'."),
        };

    private static int RequireChannel(ControlMIDIMessagePayload message) =>
        Require(nameof(message.Channel), message.Channel);

    private static int Require(string name, int? value) =>
        value ?? throw new InvalidOperationException($"MIDI message is missing {name}.");

    private static byte[] RequireData(ControlMIDIMessagePayload message) =>
        message.Data is { Length: > 0 } data
            ? data
            : throw new InvalidOperationException("MIDI message is missing SysEx data.");

    private static byte ToSevenBit(string name, int value)
    {
        if (value is < 0 or > 127)
            throw new InvalidOperationException($"MIDI {name} must be in the range 0..127.");

        return (byte)value;
    }

    private static ushort ToFourteenBit(string name, int value)
    {
        if (value is < 0 or > 16383)
            throw new InvalidOperationException($"MIDI {name} must be in the range 0..16383.");

        return (ushort)value;
    }

    private static byte[] NormalizeSysExBytes(byte[] data)
    {
        var needsStart = data[0] != 0xF0;
        var needsEnd = data[^1] != 0xF7;
        if (!needsStart && !needsEnd)
            return data;

        var result = new byte[data.Length + (needsStart ? 1 : 0) + (needsEnd ? 1 : 0)];
        var index = 0;
        if (needsStart)
            result[index++] = 0xF0;
        Array.Copy(data, 0, result, index, data.Length);
        if (needsEnd)
            result[^1] = 0xF7;
        return result;
    }

    private sealed class LiveInputSession : IDisposable
    {
        private readonly ControlMIDIPortInfo _port;
        private readonly IControlMIDIInputDevice _input;
        private readonly ControlMIDIDeviceManager _deviceManager;
        private readonly IControlMonitorSink _monitor;
        private readonly MIDIHighResolution14BitCombiner _highResCombiner;
        private bool _disposed;

        public LiveInputSession(
            ControlMIDIPortInfo port,
            IControlMIDIInputDevice input,
            ControlMIDIDeviceManager deviceManager,
            IControlMonitorSink monitor,
            IEnumerable<int>? highResolution14BitControllers = null)
        {
            _port = port;
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _monitor = monitor ?? NullControlMonitorSink.Instance;
            _highResCombiner = new MIDIHighResolution14BitCombiner(highResolution14BitControllers ?? []);
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
            // Reassemble 14-bit CC pairs (coarse + fine) into one high-resolution message for the controllers
            // the device profile marks as 14-bit. A held coarse byte returns null until its fine partner lands.
            var resolved = _highResCombiner.Process(message);
            if (resolved is null)
                return;
            _ = DispatchMIDIMessageAsync(resolved);
        }

        private async Task DispatchMIDIMessageAsync(IMIDIMessage message)
        {
            try
            {
                await _deviceManager.DispatchMIDIMessageAsync(
                    new ControlMIDIInputIdentity(_port.Id, _port.Name),
                    ControlMIDIMessagePayload.FromMIDIMessage(message),
                    rawBytes: EncodeRawBytes(message)).ConfigureAwait(false);
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
                Protocol = ControlMonitorProtocol.MIDI,
                Result = ControlMonitorResult.Failed,
                Endpoint = FormatPort(_port),
                Message = "MIDI input dispatch failed.",
                ErrorMessage = errorMessage,
            });

        private static byte[]? EncodeRawBytes(IMIDIMessage message) =>
            message switch
            {
                ControlChange cc => EncodeRawBytes(cc),
                NoteOn note => [(byte)(0x90 | (note.Channel & 0x0F)), note.Note, note.Velocity],
                NoteOff note => [(byte)(0x80 | (note.Channel & 0x0F)), note.Note, note.Velocity],
                PolyphonicAftertouch aftertouch => [(byte)(0xA0 | (aftertouch.Channel & 0x0F)), aftertouch.Note, aftertouch.Pressure],
                ProgramChange program => [(byte)(0xC0 | (program.Channel & 0x0F)), program.Program],
                ChannelAftertouch aftertouch => [(byte)(0xD0 | (aftertouch.Channel & 0x0F)), aftertouch.Pressure],
                PitchBend bend => EncodePitchBendRawBytes(bend),
                SysEx sysEx => sysEx.Data.ToArray(),
                MIDITimeCode timeCode => [0xF1, timeCode.DataByte],
                SongPosition position => [(byte)0xF2, (byte)(position.Beats & 0x7F), (byte)((position.Beats >> 7) & 0x7F)],
                SongSelect song => [0xF3, song.Song],
                TuneRequest => [0xF6],
                TimingClock => [0xF8],
                MIDIStart => [0xFA],
                MIDIContinue => [0xFB],
                MIDIStop => [0xFC],
                ActiveSensing => [0xFE],
                MIDIReset => [0xFF],
                NRPN nrpn => EncodeNrpnRawBytes(nrpn.Channel, isRegistered: false, nrpn.ParameterMsb, nrpn.ParameterLsb, nrpn.ValueMsb, nrpn.ValueLsb),
                RPN rpn => EncodeNrpnRawBytes(rpn.Channel, isRegistered: true, rpn.ParameterMsb, rpn.ParameterLsb, rpn.ValueMsb, rpn.ValueLsb),
                _ => null,
            };

        private static byte[] EncodeRawBytes(ControlChange message)
        {
            var status = (byte)(0xB0 | (message.Channel & 0x0F));
            if (!message.IsHighResolution)
                return [status, message.Controller, (byte)(message.Value & 0x7F)];

            var msb = (byte)((message.Value >> 7) & 0x7F);
            var lsb = (byte)(message.Value & 0x7F);
            return [status, message.Controller, msb, status, (byte)(message.Controller + 32), lsb];
        }

        private static byte[] EncodePitchBendRawBytes(PitchBend message)
        {
            var rawValue = Math.Clamp(message.Value + 8192, 0, 16383);
            return
            [
                (byte)(0xE0 | (message.Channel & 0x0F)),
                (byte)(rawValue & 0x7F),
                (byte)((rawValue >> 7) & 0x7F),
            ];
        }

        private static byte[] EncodeNrpnRawBytes(
            byte channel,
            bool isRegistered,
            byte parameterMsb,
            byte parameterLsb,
            byte valueMsb,
            byte valueLsb)
        {
            var status = (byte)(0xB0 | (channel & 0x0F));
            var parameterMsbController = isRegistered ? (byte)101 : (byte)99;
            var parameterLsbController = isRegistered ? (byte)100 : (byte)98;
            return
            [
                status, parameterMsbController, parameterMsb,
                status, parameterLsbController, parameterLsb,
                status, 6, valueMsb,
                status, 38, valueLsb,
            ];
        }
    }
}

internal interface IControlMIDIDeviceProvider
{
    bool RequiresPortMIDILibraryLease { get; }

    void EnsureInitialized();

    IReadOnlyList<ControlMIDIPortInfo> GetInputDevices();

    IReadOnlyList<ControlMIDIPortInfo> GetOutputDevices();

    IControlMIDIInputDevice OpenInput(ControlMIDIPortInfo port);

    IControlMIDIOutputDevice OpenOutput(ControlMIDIPortInfo port);
}

internal interface IControlMIDIInputDevice : IDisposable
{
    event EventHandler<IMIDIMessage>? MessageReceived;
}

internal interface IControlMIDIOutputDevice : IDisposable
{
    void Write(IMIDIMessage message);
}

internal sealed class RealControlMIDIDeviceProvider : IControlMIDIDeviceProvider
{
    public static RealControlMIDIDeviceProvider Instance { get; } = new();

    private RealControlMIDIDeviceProvider()
    {
    }

    public bool RequiresPortMIDILibraryLease => true;

    public void EnsureInitialized()
    {
    }

    public IReadOnlyList<ControlMIDIPortInfo> GetInputDevices() =>
        PMUtil.GetInputDevices().Select(d => new ControlMIDIPortInfo(d.Id, d.Name)).ToArray();

    public IReadOnlyList<ControlMIDIPortInfo> GetOutputDevices() =>
        PMUtil.GetOutputDevices().Select(d => new ControlMIDIPortInfo(d.Id, d.Name)).ToArray();

    public IControlMIDIInputDevice OpenInput(ControlMIDIPortInfo port)
    {
        var device = new MIDIInputDevice(port.Id);
        var wrapper = new RealControlMIDIInputDevice(device);
        var err = device.Open();
        if (err != PmError.NoError)
        {
            wrapper.Dispose();
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        return wrapper;
    }

    public IControlMIDIOutputDevice OpenOutput(ControlMIDIPortInfo port)
    {
        var device = new MIDIOutputDevice(port.Id);
        var err = device.Open();
        if (err != PmError.NoError)
        {
            device.Dispose();
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        return new RealControlMIDIOutputDevice(device);
    }

    private sealed class RealControlMIDIInputDevice : IControlMIDIInputDevice
    {
        private readonly MIDIInputDevice _device;

        public RealControlMIDIInputDevice(MIDIInputDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _device.MessageReceived += OnMessageReceived;
            _device.SysExReceived += OnSysExReceived;
        }

        public event EventHandler<IMIDIMessage>? MessageReceived;

        public void Dispose()
        {
            _device.MessageReceived -= OnMessageReceived;
            _device.SysExReceived -= OnSysExReceived;
            _device.Dispose();
        }

        private void OnMessageReceived(object? sender, IMIDIMessage message) =>
            MessageReceived?.Invoke(this, message);

        private void OnSysExReceived(object? sender, SysEx message) =>
            MessageReceived?.Invoke(this, message);
    }

    private sealed class RealControlMIDIOutputDevice : IControlMIDIOutputDevice
    {
        private readonly MIDIOutputDevice _device;

        public RealControlMIDIOutputDevice(MIDIOutputDevice device)
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
