using OSCLib;

namespace S.Control;

public sealed class ControlPeriodicOSCSendManager
{
    private static readonly TimeSpan FailedRetryInterval = TimeSpan.FromSeconds(2);

    private readonly ControlSystemConfig _config;
    private readonly IControlOSCSender _sender;
    private readonly IControlMonitorSink _monitor;
    private readonly Dictionary<(Guid DeviceId, Guid SendId), DateTimeOffset> _lastSuccessfulUtc = new();
    private readonly Dictionary<(Guid DeviceId, Guid SendId), DateTimeOffset> _lastFailedAttemptUtc = new();

    public ControlPeriodicOSCSendManager(
        ControlSystemConfig config,
        IControlOSCSender sender,
        IControlMonitorSink? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
    }

    public async ValueTask<IReadOnlyList<ControlPeriodicOSCSendResult>> TickAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_config.IsArmed)
            return [];

        var results = new List<ControlPeriodicOSCSendResult>();
        foreach (var device in _config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.OSC && d.IsEnabled))
        {
            foreach (var send in device.PeriodicOSCSends.Where(s => s.IsEnabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDue(device.Id, send, utcNow))
                    continue;

                var result = await SendAsync(device, send, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    _lastSuccessfulUtc[(device.Id, send.Id)] = utcNow;
                    _lastFailedAttemptUtc.Remove((device.Id, send.Id));
                }
                else
                {
                    _lastFailedAttemptUtc[(device.Id, send.Id)] = utcNow;
                }

                Record(result);
                results.Add(result);
            }
        }

        return results;
    }

    public void Reset()
    {
        _lastSuccessfulUtc.Clear();
        _lastFailedAttemptUtc.Clear();
    }

    private bool IsDue(Guid deviceId, ControlPeriodicOSCSendConfig send, DateTimeOffset utcNow)
    {
        var key = (deviceId, send.Id);
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, send.IntervalMs));

        if (_lastSuccessfulUtc.TryGetValue(key, out var lastSuccess))
            return utcNow - lastSuccess >= interval;

        if (!_lastFailedAttemptUtc.ContainsKey(key))
            return true;

        return _lastFailedAttemptUtc.TryGetValue(key, out var lastFail)
            && utcNow - lastFail >= FailedRetryInterval;
    }

    private async ValueTask<ControlPeriodicOSCSendResult> SendAsync(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOSCSendConfig send,
        CancellationToken cancellationToken)
    {
        var host = device.Binding.OSCHost;
        var port = device.Binding.OSCPort;
        var arguments = BuildArguments(send.Arguments);

        if (string.IsNullOrWhiteSpace(host) || !port.HasValue)
        {
            return ControlPeriodicOSCSendResult.Failed(
                device,
                send,
                arguments,
                $"OSC device '{device.Name}' does not have a host and port binding.");
        }

        if (string.IsNullOrWhiteSpace(send.Address))
        {
            return ControlPeriodicOSCSendResult.Failed(
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
            return ControlPeriodicOSCSendResult.Sent(device, send, arguments, host, port.Value);
        }
        catch (Exception ex)
        {
            return ControlPeriodicOSCSendResult.Failed(device, send, arguments, ex.Message, host, port.Value);
        }
    }

    private void Record(ControlPeriodicOSCSendResult result)
    {
        _monitor.Record(new ControlMonitorRecord
        {
            Direction = result.Succeeded ? ControlMonitorDirection.Output : ControlMonitorDirection.Error,
            Protocol = ControlMonitorProtocol.OSC,
            Result = result.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
            DeviceInstanceId = result.DeviceInstanceId,
            DeviceKey = result.DeviceKey,
            ProfileId = result.ProfileId,
            Endpoint = result.Host is not null && result.Port.HasValue ? $"{result.Host}:{result.Port}" : null,
            RemoteHost = result.Host,
            RemotePort = result.Port,
            Address = result.Send.Address,
            OSCArguments = result.Arguments.Select(ControlMonitorOSCArgumentRecord.FromOSCArgument).ToList(),
            Message = result.Send.Name,
            ErrorMessage = result.ErrorMessage,
        });
    }

    private static IReadOnlyList<OSCArgument> BuildArguments(IEnumerable<ControlOSCArgumentConfig> arguments) =>
        arguments.Select(ToOSCArgument).ToArray();

    private static OSCArgument ToOSCArgument(ControlOSCArgumentConfig argument) =>
        argument.Kind switch
        {
            ControlOSCArgumentKind.Int32 => OSCArgument.Int32(checked((int)argument.IntegerValue)),
            ControlOSCArgumentKind.Int64 => OSCArgument.Int64(argument.IntegerValue),
            ControlOSCArgumentKind.Float32 => OSCArgument.Float32((float)argument.FloatValue),
            ControlOSCArgumentKind.Double64 => OSCArgument.Double64(argument.FloatValue),
            ControlOSCArgumentKind.String => OSCArgument.String(argument.StringValue ?? string.Empty),
            ControlOSCArgumentKind.Symbol => OSCArgument.Symbol(argument.StringValue ?? string.Empty),
            ControlOSCArgumentKind.True => OSCArgument.True(),
            ControlOSCArgumentKind.False => OSCArgument.False(),
            ControlOSCArgumentKind.Blob => OSCArgument.Blob(argument.BlobValue ?? []),
            _ => OSCArgument.Nil(),
        };
}

public sealed record ControlPeriodicOSCSendResult(
    ControlDeviceInstanceConfig Device,
    ControlPeriodicOSCSendConfig Send,
    IReadOnlyList<OSCArgument> Arguments,
    bool Succeeded,
    string? Host,
    int? Port,
    string? ErrorMessage)
{
    public Guid DeviceInstanceId => Device.Id;

    public string? DeviceKey => Device.Binding.Alias ?? Device.Name;

    public string? ProfileId => Device.ProfileId;

    public static ControlPeriodicOSCSendResult Sent(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOSCSendConfig send,
        IReadOnlyList<OSCArgument> arguments,
        string host,
        int port) =>
        new(device, send, arguments, Succeeded: true, host, port, ErrorMessage: null);

    public static ControlPeriodicOSCSendResult Failed(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOSCSendConfig send,
        IReadOnlyList<OSCArgument> arguments,
        string errorMessage,
        string? host = null,
        int? port = null) =>
        new(device, send, arguments, Succeeded: false, host, port, errorMessage);
}
