using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed class ControlMidiDeviceManager
{
    private readonly ControlSystemConfig _config;
    private readonly ControlScriptRuntimeSession _runtimeSession;
    private readonly IControlMonitorSink _monitor;

    public ControlMidiDeviceManager(
        ControlSystemConfig config,
        ControlScriptRuntimeSession runtimeSession,
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
        CancellationToken cancellationToken = default)
    {
        var timestamp = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var devices = ResolveDevices(input).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateMidiInputRecord(input, device: null, channel, controller, value, highResolution14Bit, ControlMonitorResult.Dropped));
            return [];
        }

        var results = new List<ControlMidiDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMidiInputRecord(input, device, channel, controller, value, highResolution14Bit, ControlMonitorResult.Received));
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
                scriptResult));
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
            cancellationToken);

    public async ValueTask<IReadOnlyList<ControlMidiDeviceDispatchResult>> DispatchNoteAsync(
        ControlMidiInputIdentity input,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        DateTimeOffset? receivedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var devices = ResolveDevices(input).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateMidiNoteInputRecord(input, device: null, channel, note, velocity, isNoteOn, ControlMonitorResult.Dropped));
            return [];
        }

        var results = new List<ControlMidiDeviceDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateMidiNoteInputRecord(input, device, channel, note, velocity, isNoteOn, ControlMonitorResult.Received));
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
                scriptResult));
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
            cancellationToken);

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
        ControlMonitorResult result) =>
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
            MidiController = controller,
            MidiValue = value,
            MidiHighResolution14Bit = highResolution14Bit,
            Message = result == ControlMonitorResult.Dropped ? "No matching MIDI device" : nameof(ControlScriptMidiMessageKind.ControlChange),
        };

    private static ControlMonitorRecord CreateMidiNoteInputRecord(
        ControlMidiInputIdentity input,
        ControlDeviceInstanceConfig? device,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        ControlMonitorResult result) =>
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
            MidiNote = note,
            MidiValue = velocity,
            Message = result == ControlMonitorResult.Dropped
                ? "No matching MIDI device"
                : isNoteOn ? nameof(ControlScriptMidiMessageKind.NoteOn) : nameof(ControlScriptMidiMessageKind.NoteOff),
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
    ControlScriptRuntimeSessionResult ScriptResult);
