
namespace S.Control;

public sealed class ControlScriptMidiCommandRouter
{
    private readonly ControlSystemConfig _config;
    private readonly IControlMidiSender? _sender;

    public ControlScriptMidiCommandRouter(ControlSystemConfig config, IControlMidiSender? sender)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender;
    }

    public async ValueTask<IReadOnlyList<ControlScriptMidiCommandRouteResult>> SendAllAsync(
        IEnumerable<ControlScriptMidiMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var results = new List<ControlScriptMidiCommandRouteResult>();
        foreach (var message in messages)
        {
            results.Add(await SendAsync(message, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<ControlScriptMidiCommandRouteResult> SendAsync(
        ControlScriptMidiMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveDevice(message.DeviceKey, out var device, out var errorMessage))
        {
            return ControlScriptMidiCommandRouteResult.Failed(message, errorMessage);
        }

        if (!HasOutputBinding(device.Binding))
        {
            return ControlScriptMidiCommandRouteResult.Failed(
                message,
                $"MIDI device '{device.Name}' does not have an output binding.",
                device);
        }

        if (_sender is null)
        {
            return ControlScriptMidiCommandRouteResult.Failed(
                message,
                "No MIDI sender is configured.",
                device);
        }

        try
        {
            switch (message.Kind)
            {
                case ControlScriptMidiMessageKind.ControlChange:
                    await _sender.SendControlChangeAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Controller), message.Controller),
                        Require(nameof(message.Value), message.Value),
                        message.HighResolution14Bit,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMidiMessageKind.NoteOn:
                    await _sender.SendNoteAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Note), message.Note),
                        Require(nameof(message.Velocity), message.Velocity),
                        isNoteOn: true,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMidiMessageKind.NoteOff:
                    await _sender.SendNoteAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Note), message.Note),
                        message.Velocity ?? 0,
                        isNoteOn: false,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMidiMessageKind.ProgramChange:
                    await _sender.SendProgramChangeAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Value), message.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMidiMessageKind.PitchBend:
                    await _sender.SendPitchBendAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Value), message.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMidiMessageKind.PolyphonicAftertouch:
                case ControlScriptMidiMessageKind.ChannelAftertouch:
                case ControlScriptMidiMessageKind.SysEx:
                case ControlScriptMidiMessageKind.MIDITimeCode:
                case ControlScriptMidiMessageKind.SongPosition:
                case ControlScriptMidiMessageKind.SongSelect:
                case ControlScriptMidiMessageKind.TuneRequest:
                case ControlScriptMidiMessageKind.TimingClock:
                case ControlScriptMidiMessageKind.Start:
                case ControlScriptMidiMessageKind.Continue:
                case ControlScriptMidiMessageKind.Stop:
                case ControlScriptMidiMessageKind.ActiveSensing:
                case ControlScriptMidiMessageKind.Reset:
                case ControlScriptMidiMessageKind.NRPN:
                case ControlScriptMidiMessageKind.RPN:
                    await _sender.SendMidiMessageAsync(
                        device.Id,
                        ToMidiPayload(message),
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported MIDI message kind '{message.Kind}'.");
            }

            return ControlScriptMidiCommandRouteResult.Sent(message, device);
        }
        catch (Exception ex)
        {
            return ControlScriptMidiCommandRouteResult.Failed(message, ex.Message, device);
        }
    }

    private bool TryResolveDevice(string deviceKey, out ControlDeviceInstanceConfig device, out string errorMessage)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi && d.IsEnabled)
            .ToArray();

        if (Guid.TryParse(deviceKey, out var deviceId))
        {
            device = candidates.FirstOrDefault(d => d.Id == deviceId)!;
            if (device is not null)
            {
                errorMessage = string.Empty;
                return true;
            }
        }

        foreach (var selector in new Func<ControlDeviceInstanceConfig, string?>[]
                 {
                     d => d.Binding.Alias,
                     d => d.Name,
                     d => d.ProfileId,
                 })
        {
            var matches = candidates
                .Where(d => string.Equals(selector(d), deviceKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 1)
            {
                device = matches[0];
                errorMessage = string.Empty;
                return true;
            }

            if (matches.Length > 1)
            {
                device = null!;
                errorMessage = $"MIDI device key '{deviceKey}' is ambiguous and matches {matches.Length} enabled devices.";
                return false;
            }
        }

        device = null!;
        errorMessage = $"No enabled MIDI device matches key '{deviceKey}'.";
        return false;
    }

    private static bool HasOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiOutputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MidiOutputDeviceName);

    private static int Require(string name, int? value) =>
        value ?? throw new InvalidOperationException($"MIDI message is missing {name}.");

    private static ControlMidiMessagePayload ToMidiPayload(ControlScriptMidiMessage message) =>
        message.Kind switch
        {
            ControlScriptMidiMessageKind.ControlChange => new()
            {
                MessageType = ControlMidiMessageType.ControlChange,
                Channel = message.Channel,
                Controller = message.Controller,
                Value = message.Value,
                HighResolution14Bit = message.HighResolution14Bit,
            },
            ControlScriptMidiMessageKind.NoteOn => new()
            {
                MessageType = ControlMidiMessageType.NoteOn,
                Channel = message.Channel,
                Note = message.Note,
                Velocity = message.Velocity,
                Value = message.Velocity ?? message.Value,
                IsNoteOn = true,
            },
            ControlScriptMidiMessageKind.NoteOff => new()
            {
                MessageType = ControlMidiMessageType.NoteOff,
                Channel = message.Channel,
                Note = message.Note,
                Velocity = message.Velocity,
                Value = message.Velocity ?? message.Value,
                IsNoteOn = false,
            },
            ControlScriptMidiMessageKind.ProgramChange => new()
            {
                MessageType = ControlMidiMessageType.ProgramChange,
                Channel = message.Channel,
                Program = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.PitchBend => new()
            {
                MessageType = ControlMidiMessageType.PitchBend,
                Channel = message.Channel,
                PitchBend = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.PolyphonicAftertouch => new()
            {
                MessageType = ControlMidiMessageType.PolyphonicAftertouch,
                Channel = message.Channel,
                Note = message.Note,
                Pressure = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.ChannelAftertouch => new()
            {
                MessageType = ControlMidiMessageType.ChannelAftertouch,
                Channel = message.Channel,
                Pressure = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.SysEx => new()
            {
                MessageType = ControlMidiMessageType.SysEx,
                Data = message.Data,
                Value = message.Data?.Length,
            },
            ControlScriptMidiMessageKind.MIDITimeCode => new()
            {
                MessageType = ControlMidiMessageType.MIDITimeCode,
                DataByte = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.SongPosition => new()
            {
                MessageType = ControlMidiMessageType.SongPosition,
                SongPosition = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.SongSelect => new()
            {
                MessageType = ControlMidiMessageType.SongSelect,
                Song = message.Value,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.TuneRequest => new() { MessageType = ControlMidiMessageType.TuneRequest },
            ControlScriptMidiMessageKind.TimingClock => new() { MessageType = ControlMidiMessageType.TimingClock },
            ControlScriptMidiMessageKind.Start => new() { MessageType = ControlMidiMessageType.Start },
            ControlScriptMidiMessageKind.Continue => new() { MessageType = ControlMidiMessageType.Continue },
            ControlScriptMidiMessageKind.Stop => new() { MessageType = ControlMidiMessageType.Stop },
            ControlScriptMidiMessageKind.ActiveSensing => new() { MessageType = ControlMidiMessageType.ActiveSensing },
            ControlScriptMidiMessageKind.Reset => new() { MessageType = ControlMidiMessageType.Reset },
            ControlScriptMidiMessageKind.NRPN => new()
            {
                MessageType = ControlMidiMessageType.NRPN,
                Channel = message.Channel,
                Parameter = message.Parameter,
                Value = message.Value,
            },
            ControlScriptMidiMessageKind.RPN => new()
            {
                MessageType = ControlMidiMessageType.RPN,
                Channel = message.Channel,
                Parameter = message.Parameter,
                Value = message.Value,
            },
            _ => throw new InvalidOperationException($"Unsupported MIDI message kind '{message.Kind}'."),
        };
}

public sealed record ControlScriptMidiCommandRouteResult(
    ControlScriptMidiMessage Message,
    bool Succeeded,
    Guid? DeviceInstanceId,
    int? OutputDeviceId,
    string? OutputDeviceName,
    string? ErrorMessage)
{
    public static ControlScriptMidiCommandRouteResult Sent(
        ControlScriptMidiMessage message,
        ControlDeviceInstanceConfig device) =>
        new(
            message,
            Succeeded: true,
            device.Id,
            device.Binding.MidiOutputDeviceId,
            device.Binding.MidiOutputDeviceName,
            ErrorMessage: null);

    public static ControlScriptMidiCommandRouteResult Failed(
        ControlScriptMidiMessage message,
        string errorMessage,
        ControlDeviceInstanceConfig? device = null) =>
        new(
            message,
            Succeeded: false,
            device?.Id,
            device?.Binding.MidiOutputDeviceId,
            device?.Binding.MidiOutputDeviceName,
            errorMessage);
}
