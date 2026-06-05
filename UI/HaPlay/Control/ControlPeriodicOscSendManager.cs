using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public sealed class ControlPeriodicOscSendManager
{
    private readonly ControlSystemConfig _config;
    private readonly IControlOscSender _sender;
    private readonly IControlMonitorSink _monitor;
    private readonly Dictionary<(Guid DeviceId, Guid SendId), DateTimeOffset> _lastSentUtc = new();

    public ControlPeriodicOscSendManager(
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
            foreach (var send in device.PeriodicOscSends.Where(s => s.IsEnabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDue(device.Id, send, utcNow))
                    continue;

                var result = await SendAsync(device, send, cancellationToken).ConfigureAwait(false);
                _lastSentUtc[(device.Id, send.Id)] = utcNow;
                Record(result);
                results.Add(result);
            }
        }

        return results;
    }

    public void Reset()
    {
        _lastSentUtc.Clear();
    }

    private bool IsDue(Guid deviceId, ControlPeriodicOscSendConfig send, DateTimeOffset utcNow)
    {
        if (!_lastSentUtc.TryGetValue((deviceId, send.Id), out var last))
            return true;

        var interval = TimeSpan.FromMilliseconds(Math.Max(1, send.IntervalMs));
        return utcNow - last >= interval;
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

public sealed record ControlPeriodicOscSendResult(
    ControlDeviceInstanceConfig Device,
    ControlPeriodicOscSendConfig Send,
    IReadOnlyList<OSCArgument> Arguments,
    bool Succeeded,
    string? Host,
    int? Port,
    string? ErrorMessage)
{
    public Guid DeviceInstanceId => Device.Id;

    public string? DeviceKey => Device.Binding.Alias ?? Device.Name;

    public string? ProfileId => Device.ProfileId;

    public static ControlPeriodicOscSendResult Sent(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOscSendConfig send,
        IReadOnlyList<OSCArgument> arguments,
        string host,
        int port) =>
        new(device, send, arguments, Succeeded: true, host, port, ErrorMessage: null);

    public static ControlPeriodicOscSendResult Failed(
        ControlDeviceInstanceConfig device,
        ControlPeriodicOscSendConfig send,
        IReadOnlyList<OSCArgument> arguments,
        string errorMessage,
        string? host = null,
        int? port = null) =>
        new(device, send, arguments, Succeeded: false, host, port, errorMessage);
}
