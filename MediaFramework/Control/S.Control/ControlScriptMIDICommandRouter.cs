
namespace S.Control;

public sealed class ControlScriptMIDICommandRouter
{
    private readonly ControlSystemConfig _config;
    private readonly IControlMIDISender? _sender;

    public ControlScriptMIDICommandRouter(ControlSystemConfig config, IControlMIDISender? sender)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender;
    }

    public async ValueTask<IReadOnlyList<ControlScriptMIDICommandRouteResult>> SendAllAsync(
        IEnumerable<ControlScriptMIDIMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var results = new List<ControlScriptMIDICommandRouteResult>();
        foreach (var message in messages)
        {
            results.Add(await SendAsync(message, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<ControlScriptMIDICommandRouteResult> SendAsync(
        ControlScriptMIDIMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveDevice(message.DeviceKey, out var device, out var errorMessage))
        {
            return ControlScriptMIDICommandRouteResult.Failed(message, errorMessage);
        }

        if (!HasOutputBinding(device.Binding))
        {
            return ControlScriptMIDICommandRouteResult.Failed(
                message,
                $"MIDI device '{device.Name}' does not have an output binding.",
                device);
        }

        if (_sender is null)
        {
            return ControlScriptMIDICommandRouteResult.Failed(
                message,
                "No MIDI sender is configured.",
                device);
        }

        try
        {
            switch (message.Kind)
            {
                case ControlScriptMIDIMessageKind.ControlChange:
                    await _sender.SendControlChangeAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Controller), message.Controller),
                        Require(nameof(message.Value), message.Value),
                        message.HighResolution14Bit,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMIDIMessageKind.NoteOn:
                    await _sender.SendNoteAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Note), message.Note),
                        Require(nameof(message.Velocity), message.Velocity),
                        isNoteOn: true,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMIDIMessageKind.NoteOff:
                    await _sender.SendNoteAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Note), message.Note),
                        message.Velocity ?? 0,
                        isNoteOn: false,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMIDIMessageKind.ProgramChange:
                    await _sender.SendProgramChangeAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Value), message.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMIDIMessageKind.PitchBend:
                    await _sender.SendPitchBendAsync(
                        device.Id,
                        Require(nameof(message.Channel), message.Channel),
                        Require(nameof(message.Value), message.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ControlScriptMIDIMessageKind.PolyphonicAftertouch:
                case ControlScriptMIDIMessageKind.ChannelAftertouch:
                case ControlScriptMIDIMessageKind.SysEx:
                case ControlScriptMIDIMessageKind.MIDITimeCode:
                case ControlScriptMIDIMessageKind.SongPosition:
                case ControlScriptMIDIMessageKind.SongSelect:
                case ControlScriptMIDIMessageKind.TuneRequest:
                case ControlScriptMIDIMessageKind.TimingClock:
                case ControlScriptMIDIMessageKind.Start:
                case ControlScriptMIDIMessageKind.Continue:
                case ControlScriptMIDIMessageKind.Stop:
                case ControlScriptMIDIMessageKind.ActiveSensing:
                case ControlScriptMIDIMessageKind.Reset:
                case ControlScriptMIDIMessageKind.NRPN:
                case ControlScriptMIDIMessageKind.RPN:
                    await _sender.SendMIDIMessageAsync(
                        device.Id,
                        ToMIDIPayload(message),
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported MIDI message kind '{message.Kind}'.");
            }

            return ControlScriptMIDICommandRouteResult.Sent(message, device);
        }
        catch (Exception ex)
        {
            return ControlScriptMIDICommandRouteResult.Failed(message, ex.Message, device);
        }
    }

    private bool TryResolveDevice(string deviceKey, out ControlDeviceInstanceConfig device, out string errorMessage)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled)
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
        binding.MIDIOutputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName);

    private static int Require(string name, int? value) =>
        value ?? throw new InvalidOperationException($"MIDI message is missing {name}.");

    private static ControlMIDIMessagePayload ToMIDIPayload(ControlScriptMIDIMessage message) =>
        message.Kind switch
        {
            ControlScriptMIDIMessageKind.ControlChange => new()
            {
                MessageType = ControlMIDIMessageType.ControlChange,
                Channel = message.Channel,
                Controller = message.Controller,
                Value = message.Value,
                HighResolution14Bit = message.HighResolution14Bit,
            },
            ControlScriptMIDIMessageKind.NoteOn => new()
            {
                MessageType = ControlMIDIMessageType.NoteOn,
                Channel = message.Channel,
                Note = message.Note,
                Velocity = message.Velocity,
                Value = message.Velocity ?? message.Value,
                IsNoteOn = true,
            },
            ControlScriptMIDIMessageKind.NoteOff => new()
            {
                MessageType = ControlMIDIMessageType.NoteOff,
                Channel = message.Channel,
                Note = message.Note,
                Velocity = message.Velocity,
                Value = message.Velocity ?? message.Value,
                IsNoteOn = false,
            },
            ControlScriptMIDIMessageKind.ProgramChange => new()
            {
                MessageType = ControlMIDIMessageType.ProgramChange,
                Channel = message.Channel,
                Program = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.PitchBend => new()
            {
                MessageType = ControlMIDIMessageType.PitchBend,
                Channel = message.Channel,
                PitchBend = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.PolyphonicAftertouch => new()
            {
                MessageType = ControlMIDIMessageType.PolyphonicAftertouch,
                Channel = message.Channel,
                Note = message.Note,
                Pressure = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.ChannelAftertouch => new()
            {
                MessageType = ControlMIDIMessageType.ChannelAftertouch,
                Channel = message.Channel,
                Pressure = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.SysEx => new()
            {
                MessageType = ControlMIDIMessageType.SysEx,
                Data = message.Data,
                Value = message.Data?.Length,
            },
            ControlScriptMIDIMessageKind.MIDITimeCode => new()
            {
                MessageType = ControlMIDIMessageType.MIDITimeCode,
                DataByte = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.SongPosition => new()
            {
                MessageType = ControlMIDIMessageType.SongPosition,
                SongPosition = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.SongSelect => new()
            {
                MessageType = ControlMIDIMessageType.SongSelect,
                Song = message.Value,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.TuneRequest => new() { MessageType = ControlMIDIMessageType.TuneRequest },
            ControlScriptMIDIMessageKind.TimingClock => new() { MessageType = ControlMIDIMessageType.TimingClock },
            ControlScriptMIDIMessageKind.Start => new() { MessageType = ControlMIDIMessageType.Start },
            ControlScriptMIDIMessageKind.Continue => new() { MessageType = ControlMIDIMessageType.Continue },
            ControlScriptMIDIMessageKind.Stop => new() { MessageType = ControlMIDIMessageType.Stop },
            ControlScriptMIDIMessageKind.ActiveSensing => new() { MessageType = ControlMIDIMessageType.ActiveSensing },
            ControlScriptMIDIMessageKind.Reset => new() { MessageType = ControlMIDIMessageType.Reset },
            ControlScriptMIDIMessageKind.NRPN => new()
            {
                MessageType = ControlMIDIMessageType.NRPN,
                Channel = message.Channel,
                Parameter = message.Parameter,
                Value = message.Value,
            },
            ControlScriptMIDIMessageKind.RPN => new()
            {
                MessageType = ControlMIDIMessageType.RPN,
                Channel = message.Channel,
                Parameter = message.Parameter,
                Value = message.Value,
            },
            _ => throw new InvalidOperationException($"Unsupported MIDI message kind '{message.Kind}'."),
        };
}

public sealed record ControlScriptMIDICommandRouteResult(
    ControlScriptMIDIMessage Message,
    bool Succeeded,
    Guid? DeviceInstanceId,
    int? OutputDeviceId,
    string? OutputDeviceName,
    string? ErrorMessage)
{
    public static ControlScriptMIDICommandRouteResult Sent(
        ControlScriptMIDIMessage message,
        ControlDeviceInstanceConfig device) =>
        new(
            message,
            Succeeded: true,
            device.Id,
            device.Binding.MIDIOutputDeviceId,
            device.Binding.MIDIOutputDeviceName,
            ErrorMessage: null);

    public static ControlScriptMIDICommandRouteResult Failed(
        ControlScriptMIDIMessage message,
        string errorMessage,
        ControlDeviceInstanceConfig? device = null) =>
        new(
            message,
            Succeeded: false,
            device?.Id,
            device?.Binding.MIDIOutputDeviceId,
            device?.Binding.MIDIOutputDeviceName,
            errorMessage);
}
