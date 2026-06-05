
namespace S.Control;

public sealed class ControlMidiDeviceManager
{
    private readonly ControlSystemConfig _config;
    private readonly IControlScriptDispatcher _runtimeSession;
    private readonly IControlMonitorSink _monitor;

    public ControlMidiDeviceManager(
        ControlSystemConfig config,
        IControlScriptDispatcher runtimeSession,
        IControlMonitorSink? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runtimeSession = runtimeSession ?? throw new ArgumentNullException(nameof(runtimeSession));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
    }

    public async ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchControlChangeAsync(
        ControlMidiInputIdentity input,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit = false,
        DateTimeOffset? receivedAtUtc = null,
        byte[]? rawBytes = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var monitorRawBytes = _config.Monitor.IncludeRawBytes ? rawBytes : null;
        var devices = ResolveDevices(input).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateMidiInputRecord(input, device: null, channel, controller, value, highResolution14Bit, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMidiDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMidiInputRecord(input, device, channel, controller, value, highResolution14Bit, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MidiControlEvent(
                timestamp,
                device.Id,
                device.Id,
                Guid.NewGuid(),
                channel,
                controller,
                value,
                highResolution14Bit);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(new ControlMidiDeviceDispatchResult(
                input,
                device.Id,
                channel,
                Controller: controller,
                Note: null,
                Value: value,
                HighResolution14Bit: highResolution14Bit,
                IsNoteOn: null,
                scriptResult)
            {
                MessageType = ControlMidiMessageType.ControlChange,
            });
        }

        return results;
    }

    public ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchControlChangeAsync(
        int? inputDeviceId,
        string? inputDeviceName,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit = false,
        DateTimeOffset? receivedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        DispatchControlChangeAsync(
            new ControlMidiInputIdentity(inputDeviceId, inputDeviceName),
            channel,
            controller,
            value,
            highResolution14Bit,
            receivedAtUtc,
            rawBytes: null,
            cancellationToken);

    public async ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchNoteAsync(
        ControlMidiInputIdentity input,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        DateTimeOffset? receivedAtUtc = null,
        byte[]? rawBytes = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var monitorRawBytes = _config.Monitor.IncludeRawBytes ? rawBytes : null;
        var devices = ResolveDevices(input).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateMidiNoteInputRecord(input, device: null, channel, note, velocity, isNoteOn, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMidiDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMidiNoteInputRecord(input, device, channel, note, velocity, isNoteOn, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MidiNoteControlEvent(
                timestamp,
                device.Id,
                device.Id,
                Guid.NewGuid(),
                channel,
                note,
                velocity,
                isNoteOn);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(new ControlMidiDeviceDispatchResult(
                input,
                device.Id,
                channel,
                Controller: null,
                Note: note,
                Value: velocity,
                HighResolution14Bit: false,
                IsNoteOn: isNoteOn,
                scriptResult)
            {
                MessageType = isNoteOn ? ControlMidiMessageType.NoteOn : ControlMidiMessageType.NoteOff,
            });
        }

        return results;
    }

    public ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchNoteAsync(
        int? inputDeviceId,
        string? inputDeviceName,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        DateTimeOffset? receivedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        DispatchNoteAsync(
            new ControlMidiInputIdentity(inputDeviceId, inputDeviceName),
            channel,
            note,
            velocity,
            isNoteOn,
            receivedAtUtc,
            rawBytes: null,
            cancellationToken);

    public async ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchMidiMessageAsync(
        ControlMidiInputIdentity input,
        ControlMidiMessagePayload message,
        DateTimeOffset? receivedAtUtc = null,
        byte[]? rawBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var timestamp = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var monitorRawBytes = _config.Monitor.IncludeRawBytes ? rawBytes : null;
        var devices = ResolveDevices(input).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateMidiInputRecord(input, device: null, message, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMidiDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMidiInputRecord(input, device, message, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MidiMessageControlEvent(
                timestamp,
                device.Id,
                device.Id,
                Guid.NewGuid(),
                message);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(CreateDispatchResult(input, device.Id, message, scriptResult));
        }

        return results;
    }

    private IEnumerable<ControlDeviceInstanceConfig> ResolveDevices(ControlMidiInputIdentity input)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi && d.IsEnabled && HasInputBinding(d.Binding))
            .ToArray();

        if (input.DeviceId.HasValue)
        {
            var byId = candidates
                .Where(d => d.Binding.MidiInputDeviceId == input.DeviceId.Value)
                .ToArray();
            if (byId.Length > 0)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(input.DeviceName))
        {
            var byName = candidates
                .Where(d => string.Equals(d.Binding.MidiInputDeviceName, input.DeviceName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (byName.Length > 0)
                return byName;

            var inputPort = new ControlMidiPortInfo(input.DeviceId ?? -1, input.DeviceName);
            var byFuzzyName = candidates
                .Where(d => ControlDeviceMatcher.MatchMidiInput(d, [inputPort]).IsMatched)
                .ToArray();
            if (byFuzzyName.Length > 0)
                return byFuzzyName;
        }

        return [];
    }

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiInputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MidiInputDeviceName);

    private static ControlMonitorRecord CreateMidiInputRecord(
        ControlMidiInputIdentity input,
        ControlDeviceInstanceConfig? device,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        ControlMonitorResult result,
        byte[]? rawBytes) =>
        new()
        {
            Direction = result == ControlMonitorResult.Dropped ? ControlMonitorDirection.Dropped : ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MidiChannel = channel,
            MidiMessageType = ControlMidiMessageType.ControlChange,
            MidiController = controller,
            MidiValue = value,
            MidiHighResolution14Bit = highResolution14Bit,
            Message = result == ControlMonitorResult.Dropped ? "No matching MIDI device" : nameof(ControlScriptMidiMessageKind.ControlChange),
            RawBytes = rawBytes,
        };

    private static ControlMonitorRecord CreateMidiNoteInputRecord(
        ControlMidiInputIdentity input,
        ControlDeviceInstanceConfig? device,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        ControlMonitorResult result,
        byte[]? rawBytes) =>
        new()
        {
            Direction = result == ControlMonitorResult.Dropped ? ControlMonitorDirection.Dropped : ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MidiChannel = channel,
            MidiMessageType = isNoteOn ? ControlMidiMessageType.NoteOn : ControlMidiMessageType.NoteOff,
            MidiNote = note,
            MidiValue = velocity,
            Message = result == ControlMonitorResult.Dropped
                ? "No matching MIDI device"
                : isNoteOn ? nameof(ControlScriptMidiMessageKind.NoteOn) : nameof(ControlScriptMidiMessageKind.NoteOff),
            RawBytes = rawBytes,
        };

    private static ControlMonitorRecord CreateMidiInputRecord(
        ControlMidiInputIdentity input,
        ControlDeviceInstanceConfig? device,
        ControlMidiMessagePayload message,
        ControlMonitorResult result,
        byte[]? rawBytes) =>
        new()
        {
            Direction = result == ControlMonitorResult.Dropped ? ControlMonitorDirection.Dropped : ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MidiChannel = message.Channel,
            MidiMessageType = message.MessageType,
            MidiController = message.Controller,
            MidiNote = message.Note,
            MidiValue = message.Value,
            MidiParameter = message.Parameter,
            MidiHighResolution14Bit = message.HighResolution14Bit,
            Message = result == ControlMonitorResult.Dropped ? "No matching MIDI device" : message.MessageType.ToString(),
            RawBytes = rawBytes,
        };

    private static ControlMidiDeviceDispatchResult CreateDispatchResult(
        ControlMidiInputIdentity input,
        Guid deviceInstanceId,
        ControlMidiMessagePayload message,
        ControlScriptRuntimeSessionResult scriptResult) =>
        new(
            input,
            deviceInstanceId,
            message.Channel ?? 0,
            message.Controller,
            message.Note,
            message.Value ?? 0,
            message.HighResolution14Bit,
            message.IsNoteOn,
            scriptResult)
        {
            MessageType = message.MessageType,
            Program = message.Program,
            Pressure = message.Pressure,
            PitchBend = message.PitchBend,
            Parameter = message.Parameter,
        };

    private static string? FormatDeviceInput(ControlDeviceBindingConfig binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.MidiInputDeviceName))
            return binding.MidiInputDeviceName;
        return binding.MidiInputDeviceId?.ToString();
    }

    private static string? FormatInput(ControlMidiInputIdentity input)
    {
        if (!string.IsNullOrWhiteSpace(input.DeviceName))
            return input.DeviceName;
        return input.DeviceId?.ToString();
    }
}

public sealed record ControlMidiInputIdentity(int? DeviceId = null, string? DeviceName = null);

public sealed record ControlMidiDeviceDispatchResult(
    ControlMidiInputIdentity Input,
    Guid DeviceInstanceId,
    int Channel,
    int? Controller,
    int? Note,
    int Value,
    bool HighResolution14Bit,
    bool? IsNoteOn,
    ControlScriptRuntimeSessionResult ScriptResult)
{
    public ControlMidiMessageType MessageType { get; init; }

    public int? Program { get; init; }

    public int? Pressure { get; init; }

    public int? PitchBend { get; init; }

    public int? Parameter { get; init; }
}
