using OSCLib;

namespace S.Control;

/// <summary>
/// Renews X32/X-Air <c>/xremote</c>, <c>/subscribe</c>, and <c>/meters</c> periodic sends with
/// protocol-aware timing: the renewal clock advances only after a full successful renew cycle.
/// </summary>
public sealed class ControlX32ProtocolMaintenanceManager
{
    private static readonly TimeSpan FailedRetryInterval = TimeSpan.FromSeconds(2);

    private readonly ControlSystemConfig _config;
    private readonly IControlOscSender _sender;
    private readonly IControlMonitorSink _monitor;
    private readonly Dictionary<Guid, DateTimeOffset> _lastSuccessfulRenewUtc = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastFailedAttemptUtc = new();

    public ControlX32ProtocolMaintenanceManager(
        ControlSystemConfig config,
        IControlOscSender sender,
        IControlMonitorSink? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
    }

    public async ValueTask<IReadOnlyList<ControlPeriodicOscSendResult>> TickAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_config.IsArmed)
            return [];

        var results = new List<ControlPeriodicOscSendResult>();
        foreach (var device in _config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Osc && d.IsEnabled))
        {
            var sends = device.PeriodicOscSends
                .Where(s => ControlX32ProtocolAddresses.UsesProtocolMaintenance(device, s))
                .OrderBy(s => MaintenanceOrder(s.Address))
                .ToArray();
            if (sends.Length == 0)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDue(device.Id, sends, utcNow))
                continue;

            var renewResults = await RenewDeviceAsync(device, sends, cancellationToken).ConfigureAwait(false);
            results.AddRange(renewResults);

            if (renewResults.All(r => r.Succeeded))
            {
                _lastSuccessfulRenewUtc[device.Id] = utcNow;
                _lastFailedAttemptUtc.Remove(device.Id);
            }
            else
            {
                _lastFailedAttemptUtc[device.Id] = utcNow;
            }
        }

        return results;
    }

    public void Reset()
    {
        _lastSuccessfulRenewUtc.Clear();
        _lastFailedAttemptUtc.Clear();
    }

    private bool IsDue(Guid deviceId, ControlPeriodicOscSendConfig[] sends, DateTimeOffset utcNow)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, sends.Max(s => s.IntervalMs)));
        if (_lastSuccessfulRenewUtc.TryGetValue(deviceId, out var lastSuccess))
        {
            if (utcNow - lastSuccess >= interval)
                return true;
        }
        else if (!_lastFailedAttemptUtc.ContainsKey(deviceId))
        {
            return true;
        }

        if (!_lastFailedAttemptUtc.TryGetValue(deviceId, out var lastFail))
            return false;

        return utcNow - lastFail >= FailedRetryInterval;
    }

    private async ValueTask<IReadOnlyList<ControlPeriodicOscSendResult>> RenewDeviceAsync(
        ControlDeviceInstanceConfig device,
        IReadOnlyList<ControlPeriodicOscSendConfig> sends,
        CancellationToken cancellationToken)
    {
        var results = new List<ControlPeriodicOscSendResult>(sends.Count);
        foreach (var send in sends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SendAsync(device, send, cancellationToken).ConfigureAwait(false);
            Record(result);
            results.Add(result);
            if (!result.Succeeded)
                break;
        }

        return results;
    }

    private async ValueTask<ControlPeriodicOscSendResult> SendAsync(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOscSendConfig send,
        CancellationToken cancellationToken)
    {
        var host = device.Binding.OscHost;
        var port = device.Binding.OscPort;
        var arguments = BuildArguments(send.Arguments);

        if (string.IsNullOrWhiteSpace(host) || !port.HasValue)
        {
            return ControlPeriodicOscSendResult.Failed(
                device,
                send,
                arguments,
                $"OSC device '{device.Name}' does not have a host and port binding.");
        }

        if (string.IsNullOrWhiteSpace(send.Address))
        {
            return ControlPeriodicOscSendResult.Failed(
                device,
                send,
                arguments,
                $"Periodic OSC send '{send.Name}' does not have an address.",
                host,
                port.Value);
        }

        try
        {
            await _sender.SendAsync(host, port.Value, send.Address, arguments, cancellationToken).ConfigureAwait(false);
            return ControlPeriodicOscSendResult.Sent(device, send, arguments, host, port.Value);
        }
        catch (Exception ex)
        {
            return ControlPeriodicOscSendResult.Failed(device, send, arguments, ex.Message, host, port.Value);
        }
    }

    private void Record(ControlPeriodicOscSendResult result)
    {
        _monitor.Record(new ControlMonitorRecord
        {
            Direction = result.Succeeded ? ControlMonitorDirection.Output : ControlMonitorDirection.Error,
            Protocol = ControlMonitorProtocol.Osc,
            Result = result.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
            DeviceInstanceId = result.DeviceInstanceId,
            DeviceKey = result.DeviceKey,
            ProfileId = result.ProfileId,
            Endpoint = result.Host is not null && result.Port.HasValue ? $"{result.Host}:{result.Port}" : null,
            RemoteHost = result.Host,
            RemotePort = result.Port,
            Address = result.Send.Address,
            OscArguments = result.Arguments.Select(ControlMonitorOscArgumentRecord.FromOscArgument).ToList(),
            Message = result.Send.Name,
            ErrorMessage = result.ErrorMessage,
        });
    }

    private static int MaintenanceOrder(string address) =>
        address switch
        {
            "/xremote" => 0,
            "/subscribe" => 1,
            "/meters" => 2,
            _ => 99,
        };

    private static IReadOnlyList<OSCArgument> BuildArguments(IEnumerable<ControlOscArgumentConfig> arguments) =>
        arguments.Select(ToOscArgument).ToArray();

    private static OSCArgument ToOscArgument(ControlOscArgumentConfig argument) =>
        argument.Kind switch
        {
            ControlOscArgumentKind.Int32 => OSCArgument.Int32(checked((int)argument.IntegerValue)),
            ControlOscArgumentKind.Int64 => OSCArgument.Int64(argument.IntegerValue),
            ControlOscArgumentKind.Float32 => OSCArgument.Float32((float)argument.FloatValue),
            ControlOscArgumentKind.Double64 => OSCArgument.Double64(argument.FloatValue),
            ControlOscArgumentKind.String => OSCArgument.String(argument.StringValue ?? string.Empty),
            ControlOscArgumentKind.Symbol => OSCArgument.Symbol(argument.StringValue ?? string.Empty),
            ControlOscArgumentKind.True => OSCArgument.True(),
            ControlOscArgumentKind.False => OSCArgument.False(),
            ControlOscArgumentKind.Blob => OSCArgument.Blob(argument.BlobValue ?? []),
            _ => OSCArgument.Nil(),
        };
}
