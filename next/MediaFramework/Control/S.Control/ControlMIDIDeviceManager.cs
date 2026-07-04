
namespace S.Control;

public sealed class ControlMIDIDeviceManager
{
    private readonly ControlSystemConfig _config;
    private readonly IControlScriptDispatcher _runtimeSession;
    private readonly IControlMonitorSink _monitor;

    public ControlMIDIDeviceManager(
        ControlSystemConfig config,
        IControlScriptDispatcher runtimeSession,
        IControlMonitorSink? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runtimeSession = runtimeSession ?? throw new ArgumentNullException(nameof(runtimeSession));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
    }

    public async ValueTask<IReadOnlyList<ControlMIDIDeviceDispatchResult>> DispatchControlChangeAsync(
        ControlMIDIInputIdentity input,
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
            _monitor.Record(CreateMIDIInputRecord(input, device: null, channel, controller, value, highResolution14Bit, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMIDIDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMIDIInputRecord(input, device, channel, controller, value, highResolution14Bit, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MIDIControlEvent(
                timestamp,
                device.Id,
                device.Id,
                Guid.NewGuid(),
                channel,
                controller,
                value,
                highResolution14Bit);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(new ControlMIDIDeviceDispatchResult(
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
                MessageType = ControlMIDIMessageType.ControlChange,
            });
        }

        return results;
    }

    public ValueTask<IReadOnlyList<ControlMIDIDeviceDispatchResult>> DispatchControlChangeAsync(
        int? inputDeviceId,
        string? inputDeviceName,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit = false,
        DateTimeOffset? receivedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        DispatchControlChangeAsync(
            new ControlMIDIInputIdentity(inputDeviceId, inputDeviceName),
            channel,
            controller,
            value,
            highResolution14Bit,
            receivedAtUtc,
            rawBytes: null,
            cancellationToken);

    public async ValueTask<IReadOnlyList<ControlMIDIDeviceDispatchResult>> DispatchNoteAsync(
        ControlMIDIInputIdentity input,
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
            _monitor.Record(CreateMIDINoteInputRecord(input, device: null, channel, note, velocity, isNoteOn, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMIDIDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMIDINoteInputRecord(input, device, channel, note, velocity, isNoteOn, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MIDINoteControlEvent(
                timestamp,
                device.Id,
                device.Id,
                Guid.NewGuid(),
                channel,
                note,
                velocity,
                isNoteOn);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(new ControlMIDIDeviceDispatchResult(
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
                MessageType = isNoteOn ? ControlMIDIMessageType.NoteOn : ControlMIDIMessageType.NoteOff,
            });
        }

        return results;
    }

    public ValueTask<IReadOnlyList<ControlMIDIDeviceDispatchResult>> DispatchNoteAsync(
        int? inputDeviceId,
        string? inputDeviceName,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        DateTimeOffset? receivedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        DispatchNoteAsync(
            new ControlMIDIInputIdentity(inputDeviceId, inputDeviceName),
            channel,
            note,
            velocity,
            isNoteOn,
            receivedAtUtc,
            rawBytes: null,
            cancellationToken);

    public async ValueTask<IReadOnlyList<ControlMIDIDeviceDispatchResult>> DispatchMIDIMessageAsync(
        ControlMIDIInputIdentity input,
        ControlMIDIMessagePayload message,
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
            _monitor.Record(CreateMIDIInputRecord(input, device: null, message, ControlMonitorResult.Dropped, monitorRawBytes));
            return [];
        }

        var results = new List<ControlMIDIDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMIDIInputRecord(input, device, message, ControlMonitorResult.Received, monitorRawBytes));
            var evt = new MIDIMessageControlEvent(
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

    private IEnumerable<ControlDeviceInstanceConfig> ResolveDevices(ControlMIDIInputIdentity input)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled && HasInputBinding(d.Binding))
            .ToArray();

        if (input.DeviceId.HasValue)
        {
            var byId = candidates
                .Where(d => d.Binding.MIDIInputDeviceId == input.DeviceId.Value)
                .ToArray();
            if (byId.Length > 0)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(input.DeviceName))
        {
            var byName = candidates
                .Where(d => string.Equals(d.Binding.MIDIInputDeviceName, input.DeviceName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (byName.Length > 0)
                return byName;

            var inputPort = new ControlMIDIPortInfo(input.DeviceId ?? -1, input.DeviceName);
            var byFuzzyName = candidates
                .Where(d => ControlDeviceMatcher.MatchMIDIInput(d, [inputPort]).IsMatched)
                .ToArray();
            if (byFuzzyName.Length > 0)
                return byFuzzyName;
        }

        return [];
    }

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIInputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName);

    private static ControlMonitorRecord CreateMIDIInputRecord(
        ControlMIDIInputIdentity input,
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
            Protocol = ControlMonitorProtocol.MIDI,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MIDIChannel = channel,
            MIDIMessageType = ControlMIDIMessageType.ControlChange,
            MIDIController = controller,
            MIDIValue = value,
            MIDIHighResolution14Bit = highResolution14Bit,
            Message = result == ControlMonitorResult.Dropped ? "No matching MIDI device" : nameof(ControlScriptMIDIMessageKind.ControlChange),
            RawBytes = rawBytes,
        };

    private static ControlMonitorRecord CreateMIDINoteInputRecord(
        ControlMIDIInputIdentity input,
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
            Protocol = ControlMonitorProtocol.MIDI,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MIDIChannel = channel,
            MIDIMessageType = isNoteOn ? ControlMIDIMessageType.NoteOn : ControlMIDIMessageType.NoteOff,
            MIDINote = note,
            MIDIValue = velocity,
            Message = result == ControlMonitorResult.Dropped
                ? "No matching MIDI device"
                : isNoteOn ? nameof(ControlScriptMIDIMessageKind.NoteOn) : nameof(ControlScriptMIDIMessageKind.NoteOff),
            RawBytes = rawBytes,
        };

    private static ControlMonitorRecord CreateMIDIInputRecord(
        ControlMIDIInputIdentity input,
        ControlDeviceInstanceConfig? device,
        ControlMIDIMessagePayload message,
        ControlMonitorResult result,
        byte[]? rawBytes) =>
        new()
        {
            Direction = result == ControlMonitorResult.Dropped ? ControlMonitorDirection.Dropped : ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.MIDI,
            Result = result,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            Endpoint = device is null ? FormatInput(input) : FormatDeviceInput(device.Binding),
            MIDIChannel = message.Channel,
            MIDIMessageType = message.MessageType,
            MIDIController = message.Controller,
            MIDINote = message.Note,
            MIDIValue = message.Value,
            MIDIParameter = message.Parameter,
            MIDIHighResolution14Bit = message.HighResolution14Bit,
            Message = result == ControlMonitorResult.Dropped ? "No matching MIDI device" : message.MessageType.ToString(),
            RawBytes = rawBytes,
        };

    private static ControlMIDIDeviceDispatchResult CreateDispatchResult(
        ControlMIDIInputIdentity input,
        Guid deviceInstanceId,
        ControlMIDIMessagePayload message,
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
        if (!string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName))
            return binding.MIDIInputDeviceName;
        return binding.MIDIInputDeviceId?.ToString();
    }

    private static string? FormatInput(ControlMIDIInputIdentity input)
    {
        if (!string.IsNullOrWhiteSpace(input.DeviceName))
            return input.DeviceName;
        return input.DeviceId?.ToString();
    }
}

public sealed record ControlMIDIInputIdentity(int? DeviceId = null, string? DeviceName = null);

public sealed record ControlMIDIDeviceDispatchResult(
    ControlMIDIInputIdentity Input,
    Guid DeviceInstanceId,
    int Channel,
    int? Controller,
    int? Note,
    int Value,
    bool HighResolution14Bit,
    bool? IsNoteOn,
    ControlScriptRuntimeSessionResult ScriptResult)
{
    public ControlMIDIMessageType MessageType { get; init; }

    public int? Program { get; init; }

    public int? Pressure { get; init; }

    public int? PitchBend { get; init; }

    public int? Parameter { get; init; }
}
