using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public sealed class BufferingControlScriptCommandSink : IControlScriptCommandSink
{
    private readonly List<ControlScriptOscMessage> _oscMessages = new();
    private readonly List<ControlScriptMidiMessage> _midiMessages = new();
    private readonly List<string> _layerActivations = new();

    public IReadOnlyList<ControlScriptOscMessage> OscMessages => _oscMessages;

    public IReadOnlyList<ControlScriptMidiMessage> MidiMessages => _midiMessages;

    public void SendOsc(ControlScriptOscMessage message)
    {
        _oscMessages.Add(message);
    }

    public void SendMidi(ControlScriptMidiMessage message)
    {
        _midiMessages.Add(message);
    }

    public void RequestActivateLayer(string layerKey)
    {
        _layerActivations.Add(layerKey);
    }

    public IReadOnlyList<string> DrainLayerActivations()
    {
        var drained = _layerActivations.ToArray();
        _layerActivations.Clear();
        return drained;
    }

    public IReadOnlyList<ControlScriptOscMessage> DrainOscMessages()
    {
        var drained = _oscMessages.ToArray();
        _oscMessages.Clear();
        return drained;
    }

    public IReadOnlyList<ControlScriptMidiMessage> DrainMidiMessages()
    {
        var drained = _midiMessages.ToArray();
        _midiMessages.Clear();
        return drained;
    }
}

public sealed class ControlScriptOscCommandRouter
{
    private readonly ControlSystemConfig _config;
    private readonly IControlOscSender _sender;
    private readonly ControlValueCache _cache;

    public ControlScriptOscCommandRouter(
        ControlSystemConfig config,
        IControlOscSender sender,
        ControlValueCache? cache = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _cache = cache ?? new ControlValueCache();
    }

    public async ValueTask<IReadOnlyList<ControlScriptOscCommandRouteResult>> SendAllAsync(
        IEnumerable<ControlScriptOscMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var results = new List<ControlScriptOscCommandRouteResult>();
        foreach (var message in messages)
        {
            results.Add(await SendAsync(message, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<ControlScriptOscCommandRouteResult> SendAsync(
        ControlScriptOscMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveDevice(message.DeviceKey, out var device, out var errorMessage))
        {
            return ControlScriptOscCommandRouteResult.Failed(message, errorMessage);
        }

        var host = device.Binding.OscHost;
        var port = device.Binding.OscPort;
        if (string.IsNullOrWhiteSpace(host) || !port.HasValue)
        {
            return ControlScriptOscCommandRouteResult.Failed(
                message,
                $"OSC device '{device.Name}' does not have a host and port binding.",
                device.Id);
        }

        try
        {
            var arguments = message.Arguments.Select(ToOscArgument).ToArray();
            await _sender.SendAsync(host, port.Value, message.Address, arguments, cancellationToken).ConfigureAwait(false);
            UpdateOptimisticCache(device, message);

            return ControlScriptOscCommandRouteResult.Sent(message, device.Id, host, port.Value);
        }
        catch (Exception ex)
        {
            return ControlScriptOscCommandRouteResult.Failed(message, ex.Message, device.Id, host, port.Value);
        }
    }

    private bool TryResolveDevice(string deviceKey, out ControlDeviceInstanceConfig device, out string errorMessage)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Osc && d.IsEnabled)
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
                errorMessage = $"OSC device key '{deviceKey}' is ambiguous and matches {matches.Length} enabled devices.";
                return false;
            }
        }

        device = null!;
        errorMessage = $"No enabled OSC device matches key '{deviceKey}'.";
        return false;
    }

    private void UpdateOptimisticCache(ControlDeviceInstanceConfig device, ControlScriptOscMessage message)
    {
        if (ResolveCacheUpdateMode(device, message.Address) != ControlOscCacheUpdateMode.OptimisticSendAndIncoming)
            return;

        for (var i = 0; i < message.Arguments.Count; i++)
        {
            var argument = message.Arguments[i];
            foreach (var deviceKey in GetDeviceCacheKeys(device))
            {
                switch (argument.Type)
                {
                    case ControlScriptOscArgumentType.Float32:
                    case ControlScriptOscArgumentType.Double64:
                    case ControlScriptOscArgumentType.Int32:
                    case ControlScriptOscArgumentType.Int64:
                        _cache.SetNumber(deviceKey, message.Address, argument.NumberValue, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOscArgumentType.String:
                    case ControlScriptOscArgumentType.Symbol:
                        _cache.SetString(deviceKey, message.Address, argument.StringValue ?? string.Empty, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOscArgumentType.True:
                        _cache.SetBoolean(deviceKey, message.Address, true, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOscArgumentType.False:
                        _cache.SetBoolean(deviceKey, message.Address, false, ControlValueCacheSource.OptimisticSend, i);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Resolves the effective cache update mode for one OSC send. A per-command override whose
    /// address pattern matches and whose device scope includes <paramref name="device"/> wins over the
    /// project default; the most specific override wins (device-scoped over any-device, exact address
    /// over wildcard), with declaration order breaking ties.
    /// </summary>
    private ControlOscCacheUpdateMode ResolveCacheUpdateMode(ControlDeviceInstanceConfig device, string address)
    {
        ControlOscCacheCommandOverride? best = null;
        var bestScore = -1;
        foreach (var candidate in _config.OscCacheOverrides)
        {
            if (candidate.DeviceInstanceId.HasValue && candidate.DeviceInstanceId.Value != device.Id)
                continue;
            if (!ControlOscAddressPattern.Matches(candidate.AddressPattern, address))
                continue;

            var score = OverrideSpecificity(candidate, address);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best?.Mode ?? _config.OscCacheUpdateMode;
    }

    private static int OverrideSpecificity(ControlOscCacheCommandOverride candidate, string address)
    {
        var score = 0;
        if (candidate.DeviceInstanceId.HasValue)
            score += 2;
        if (string.Equals(candidate.AddressPattern, address, StringComparison.Ordinal))
            score += 1;

        return score;
    }

    private static OSCArgument ToOscArgument(ControlScriptOscArgument argument) =>
        argument.Type switch
        {
            ControlScriptOscArgumentType.Float32 => OSCArgument.Float32((float)argument.NumberValue),
            ControlScriptOscArgumentType.Double64 => OSCArgument.Double64(argument.NumberValue),
            ControlScriptOscArgumentType.Int32 => OSCArgument.Int32((int)argument.NumberValue),
            ControlScriptOscArgumentType.Int64 => OSCArgument.Int64((long)argument.NumberValue),
            ControlScriptOscArgumentType.String => OSCArgument.String(argument.StringValue ?? string.Empty),
            ControlScriptOscArgumentType.Symbol => OSCArgument.Symbol(argument.StringValue ?? string.Empty),
            ControlScriptOscArgumentType.True => OSCArgument.True(),
            ControlScriptOscArgumentType.False => OSCArgument.False(),
            _ => OSCArgument.Nil(),
        };

    private static IEnumerable<string> GetDeviceCacheKeys(ControlDeviceInstanceConfig device)
    {
        yield return device.Id.ToString();
        if (!string.IsNullOrWhiteSpace(device.Name))
            yield return device.Name;
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            yield return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            yield return device.ProfileId;
    }
}

public sealed record ControlScriptOscCommandRouteResult(
    ControlScriptOscMessage Message,
    bool Succeeded,
    Guid? DeviceInstanceId,
    string? Host,
    int? Port,
    string? ErrorMessage)
{
    public static ControlScriptOscCommandRouteResult Sent(
        ControlScriptOscMessage message,
        Guid deviceInstanceId,
        string host,
        int port) =>
        new(message, Succeeded: true, deviceInstanceId, host, port, ErrorMessage: null);

    public static ControlScriptOscCommandRouteResult Failed(
        ControlScriptOscMessage message,
        string errorMessage,
        Guid? deviceInstanceId = null,
        string? host = null,
        int? port = null) =>
        new(message, Succeeded: false, deviceInstanceId, host, port, errorMessage);
}
