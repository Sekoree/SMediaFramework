using OSCLib;

namespace S.Control;

public sealed class BufferingControlScriptCommandSink : IControlScriptCommandSink
{
    private readonly List<ControlScriptOSCMessage> _oscMessages = new();
    private readonly List<ControlScriptMIDIMessage> _midiMessages = new();
    private readonly List<string> _layerActivations = new();

    public IReadOnlyList<ControlScriptOSCMessage> OSCMessages => _oscMessages;

    public IReadOnlyList<ControlScriptMIDIMessage> MIDIMessages => _midiMessages;

    public void SendOSC(ControlScriptOSCMessage message)
    {
        _oscMessages.Add(message);
    }

    public void SendMIDI(ControlScriptMIDIMessage message)
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

    public IReadOnlyList<ControlScriptOSCMessage> DrainOSCMessages()
    {
        var drained = _oscMessages.ToArray();
        _oscMessages.Clear();
        return drained;
    }

    public IReadOnlyList<ControlScriptMIDIMessage> DrainMIDIMessages()
    {
        var drained = _midiMessages.ToArray();
        _midiMessages.Clear();
        return drained;
    }
}

public sealed class ControlScriptOSCCommandRouter
{
    private readonly ControlSystemConfig _config;
    private readonly IControlOSCSender _sender;
    private readonly ControlValueCache _cache;

    public ControlScriptOSCCommandRouter(
        ControlSystemConfig config,
        IControlOSCSender sender,
        ControlValueCache? cache = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _cache = cache ?? new ControlValueCache();
    }

    public async ValueTask<IReadOnlyList<ControlScriptOSCCommandRouteResult>> SendAllAsync(
        IEnumerable<ControlScriptOSCMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var results = new List<ControlScriptOSCCommandRouteResult>();
        foreach (var message in messages)
        {
            results.Add(await SendAsync(message, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<ControlScriptOSCCommandRouteResult> SendAsync(
        ControlScriptOSCMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveDevice(message.DeviceKey, out var device, out var errorMessage))
        {
            return ControlScriptOSCCommandRouteResult.Failed(message, errorMessage);
        }

        var host = device.Binding.OSCHost;
        var port = device.Binding.OSCPort;
        if (string.IsNullOrWhiteSpace(host) || !port.HasValue)
        {
            return ControlScriptOSCCommandRouteResult.Failed(
                message,
                $"OSC device '{device.Name}' does not have a host and port binding.",
                device.Id);
        }

        try
        {
            var arguments = message.Arguments.Select(ToOSCArgument).ToArray();
            await _sender.SendAsync(host, port.Value, message.Address, arguments, cancellationToken).ConfigureAwait(false);
            UpdateOptimisticCache(device, message);

            return ControlScriptOSCCommandRouteResult.Sent(message, device.Id, host, port.Value);
        }
        catch (Exception ex)
        {
            return ControlScriptOSCCommandRouteResult.Failed(message, ex.Message, device.Id, host, port.Value);
        }
    }

    private bool TryResolveDevice(string deviceKey, out ControlDeviceInstanceConfig device, out string errorMessage)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.OSC && d.IsEnabled)
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

    private void UpdateOptimisticCache(ControlDeviceInstanceConfig device, ControlScriptOSCMessage message)
    {
        if (ResolveCacheUpdateMode(device, message.Address) != ControlOSCCacheUpdateMode.OptimisticSendAndIncoming)
            return;

        for (var i = 0; i < message.Arguments.Count; i++)
        {
            var argument = message.Arguments[i];
            foreach (var deviceKey in GetDeviceCacheKeys(device))
            {
                switch (argument.Type)
                {
                    case ControlScriptOSCArgumentType.Float32:
                    case ControlScriptOSCArgumentType.Double64:
                    case ControlScriptOSCArgumentType.Int32:
                    case ControlScriptOSCArgumentType.Int64:
                        _cache.SetNumber(deviceKey, message.Address, argument.NumberValue, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOSCArgumentType.String:
                    case ControlScriptOSCArgumentType.Symbol:
                        _cache.SetString(deviceKey, message.Address, argument.StringValue ?? string.Empty, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOSCArgumentType.True:
                        _cache.SetBoolean(deviceKey, message.Address, true, ControlValueCacheSource.OptimisticSend, i);
                        break;
                    case ControlScriptOSCArgumentType.False:
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
    private ControlOSCCacheUpdateMode ResolveCacheUpdateMode(ControlDeviceInstanceConfig device, string address)
    {
        ControlOSCCacheCommandOverride? best = null;
        var bestScore = -1;
        foreach (var candidate in _config.OSCCacheOverrides)
        {
            if (candidate.DeviceInstanceId.HasValue && candidate.DeviceInstanceId.Value != device.Id)
                continue;
            if (!ControlOSCAddressPattern.Matches(candidate.AddressPattern, address))
                continue;

            var score = OverrideSpecificity(candidate, address);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best?.Mode ?? _config.OSCCacheUpdateMode;
    }

    private static int OverrideSpecificity(ControlOSCCacheCommandOverride candidate, string address)
    {
        var score = 0;
        if (candidate.DeviceInstanceId.HasValue)
            score += 2;
        if (string.Equals(candidate.AddressPattern, address, StringComparison.Ordinal))
            score += 1;

        return score;
    }

    private static OSCArgument ToOSCArgument(ControlScriptOSCArgument argument) =>
        argument.Type switch
        {
            ControlScriptOSCArgumentType.Float32 => OSCArgument.Float32((float)argument.NumberValue),
            ControlScriptOSCArgumentType.Double64 => OSCArgument.Double64(argument.NumberValue),
            ControlScriptOSCArgumentType.Int32 => OSCArgument.Int32((int)argument.NumberValue),
            ControlScriptOSCArgumentType.Int64 => OSCArgument.Int64((long)argument.NumberValue),
            ControlScriptOSCArgumentType.String => OSCArgument.String(argument.StringValue ?? string.Empty),
            ControlScriptOSCArgumentType.Symbol => OSCArgument.Symbol(argument.StringValue ?? string.Empty),
            ControlScriptOSCArgumentType.True => OSCArgument.True(),
            ControlScriptOSCArgumentType.False => OSCArgument.False(),
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

public sealed record ControlScriptOSCCommandRouteResult(
    ControlScriptOSCMessage Message,
    bool Succeeded,
    Guid? DeviceInstanceId,
    string? Host,
    int? Port,
    string? ErrorMessage)
{
    public static ControlScriptOSCCommandRouteResult Sent(
        ControlScriptOSCMessage message,
        Guid deviceInstanceId,
        string host,
        int port) =>
        new(message, Succeeded: true, deviceInstanceId, host, port, ErrorMessage: null);

    public static ControlScriptOSCCommandRouteResult Failed(
        ControlScriptOSCMessage message,
        string errorMessage,
        Guid? deviceInstanceId = null,
        string? host = null,
        int? port = null) =>
        new(message, Succeeded: false, deviceInstanceId, host, port, errorMessage);
}
