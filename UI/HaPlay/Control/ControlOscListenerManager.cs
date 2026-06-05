using System.Net;
using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public sealed class ControlOscListenerManager : IAsyncDisposable, IDisposable
{
    private readonly ControlSystemConfig _config;
    private readonly IControlScriptDispatcher _runtimeSession;
    private readonly IControlMonitorSink _monitor;
    private readonly Dictionary<Guid, ListenerRuntimeState> _listeners = new();
    private bool _disposed;

    public ControlOscListenerManager(
        ControlSystemConfig config,
        IControlScriptDispatcher runtimeSession,
        IControlMonitorSink? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runtimeSession = runtimeSession ?? throw new ArgumentNullException(nameof(runtimeSession));
        _monitor = monitor ?? NullControlMonitorSink.Instance;

        foreach (var listener in config.OscListeners)
        {
            _listeners[listener.Id] = new ListenerRuntimeState(listener);
        }
    }

    public IReadOnlyDictionary<Guid, ControlSessionHealth> ListenerHealth =>
        _listeners.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Health);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfDuplicateEnabledPorts();

        foreach (var state in _listeners.Values)
        {
            if (!state.Config.IsEnabled || state.Server is not null)
                continue;

            try
            {
                state.Health = new ControlSessionHealth(
                    ControlSessionState.Starting,
                    $"Listening OSC {state.Config.LocalPort}",
                    DateTimeOffset.UtcNow);

                var server = new OSCServer(new OSCServerOptions { Port = state.Config.LocalPort });
                state.Registration = server.RegisterHandler("//", (context, ct) => OnOscMessageAsync(state.Config.Id, context, ct));
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
                state.Server = server;
                state.Health = ControlSessionHealth.Running($"OSC listen {state.Config.LocalPort}");
            }
            catch (Exception ex)
            {
                state.Health = ControlSessionHealth.Faulted(ex.Message);
                throw;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var state in _listeners.Values)
        {
            state.Registration?.Dispose();
            state.Registration = null;

            if (state.Server is null)
                continue;

            await state.Server.StopAsync(cancellationToken).ConfigureAwait(false);
            state.Server.Dispose();
            state.Server = null;
            state.Health = ControlSessionHealth.Stopped();
        }
    }

    public async ValueTask<IReadOnlyList<ControlOscListenerDispatchResult>> DispatchMessageAsync(
        Guid listenerId,
        OSCMessageContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_listeners.TryGetValue(listenerId, out var listener) || !listener.Config.IsEnabled)
            return [];

        var devices = ResolveDevices(listenerId, context.RemoteEndPoint).ToArray();
        if (devices.Length == 0)
        {
            _monitor.Record(CreateOscInputRecord(listenerId, device: null, context, ControlMonitorResult.Dropped, _config.Monitor.IncludeRawBytes));
            return [];
        }

        var results = new List<ControlOscListenerDispatchResult>(devices.Length);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.Record(CreateOscInputRecord(listenerId, device, context, ControlMonitorResult.Received, _config.Monitor.IncludeRawBytes));
            var evt = new OscControlEvent(
                context.ReceivedAtUtc,
                listenerId,
                device.Id,
                Guid.NewGuid(),
                context.Message.Address,
                context.Message.Arguments);
            var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
            results.Add(new ControlOscListenerDispatchResult(listenerId, device.Id, context.RemoteEndPoint, context.Message.Address, scriptResult));
        }

        return results;
    }

    /// <summary>
    /// Dispatches an OSC message that arrived on a device's own client socket (e.g. an X32 reply to our
    /// request, or an <c>/xremote</c> push). The device is already resolved by the caller from the source
    /// host/port, so this needs no app-level listener — it records the input and routes the same control
    /// event the listener path uses (cache update, triggers, monitor).
    /// </summary>
    public async ValueTask<ControlOscListenerDispatchResult> DispatchDeviceMessageAsync(
        ControlDeviceInstanceConfig device,
        OSCMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var listenerId = device.Binding.OscListenerId ?? Guid.Empty;
        _monitor.Record(CreateOscInputRecord(listenerId, device, context, ControlMonitorResult.Received, _config.Monitor.IncludeRawBytes));
        var evt = new OscControlEvent(
            context.ReceivedAtUtc,
            listenerId,
            device.Id,
            Guid.NewGuid(),
            context.Message.Address,
            context.Message.Arguments);
        var scriptResult = await _runtimeSession.DispatchControlEventAsync(evt, cancellationToken).ConfigureAwait(false);
        return new ControlOscListenerDispatchResult(listenerId, device.Id, context.RemoteEndPoint, context.Message.Address, scriptResult);
    }

    private static ControlMonitorRecord CreateOscInputRecord(
        Guid listenerId,
        ControlDeviceInstanceConfig? device,
        OSCMessageContext context,
        ControlMonitorResult result,
        bool includeRawBytes) =>
        new()
        {
            Direction = result == ControlMonitorResult.Dropped ? ControlMonitorDirection.Dropped : ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Osc,
            Result = result,
            ListenerId = listenerId,
            DeviceInstanceId = device?.Id,
            DeviceKey = device?.Binding.Alias ?? device?.Name,
            ProfileId = device?.ProfileId,
            RemoteHost = context.RemoteEndPoint.Address.ToString(),
            RemotePort = context.RemoteEndPoint.Port,
            Address = context.Message.Address,
            OscArguments = context.Message.Arguments.Select(ControlMonitorOscArgumentRecord.FromOscArgument).ToList(),
            RawBytes = includeRawBytes ? TryEncodeRawBytes(context.Message) : null,
            Message = result == ControlMonitorResult.Dropped ? "No matching OSC device" : null,
        };

    /// <summary>
    /// Re-encodes a decoded incoming message to its OSC wire bytes for the monitor's raw view. The server
    /// decodes packets before building the dispatch context, so the original bytes are no longer available;
    /// a single-message re-encode is a faithful representation (bundle framing aside) and never throws.
    /// </summary>
    private static byte[]? TryEncodeRawBytes(OSCMessage message)
    {
        try
        {
            using var rented = OSCPacketCodec.EncodeToRented(OSCPacket.FromMessage(message));
            return rented.Memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private async ValueTask OnOscMessageAsync(
        Guid listenerId,
        OSCMessageContext context,
        CancellationToken cancellationToken)
    {
        await DispatchMessageAsync(listenerId, context, cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<ControlDeviceInstanceConfig> ResolveDevices(Guid listenerId, IPEndPoint remoteEndPoint)
    {
        var candidates = _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Osc && d.IsEnabled && BelongsToListener(d, listenerId))
            .Where(d => HostMatches(d.Binding.OscHost, remoteEndPoint.Address))
            .ToArray();

        if (candidates.Length == 0)
            return [];

        var portMatches = candidates
            .Where(d => d.Binding.OscPort.HasValue && d.Binding.OscPort.Value == remoteEndPoint.Port)
            .ToArray();

        return portMatches.Length > 0 ? portMatches : candidates;
    }

    private bool BelongsToListener(ControlDeviceInstanceConfig device, Guid listenerId)
    {
        if (device.Binding.OscListenerId.HasValue)
            return device.Binding.OscListenerId.Value == listenerId;

        return GetDefaultListenerId() == listenerId;
    }

    private Guid? GetDefaultListenerId() =>
        _config.OscListeners.FirstOrDefault(l => l.IsEnabled)?.Id
        ?? _config.OscListeners.FirstOrDefault()?.Id;

    private static bool HostMatches(string? configuredHost, IPAddress remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(configuredHost))
            return true;

        var host = configuredHost.Trim();
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.IsLoopback(remoteAddress);

        if (IPAddress.TryParse(host, out var parsed))
            return AddressEquals(parsed, remoteAddress);

        return string.Equals(host, remoteAddress.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddressEquals(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
            return true;

        if (left.IsIPv4MappedToIPv6)
            left = left.MapToIPv4();
        if (right.IsIPv4MappedToIPv6)
            right = right.MapToIPv4();

        return left.Equals(right);
    }

    private void ThrowIfDuplicateEnabledPorts()
    {
        var duplicate = _config.OscListeners
            .Where(l => l.IsEnabled)
            .GroupBy(l => l.LocalPort)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            throw new InvalidOperationException($"Multiple enabled OSC listeners use local port {duplicate.Key}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var state in _listeners.Values)
        {
            state.Registration?.Dispose();
            state.Registration = null;
            state.Server?.Dispose();
            state.Server = null;
            state.Health = ControlSessionHealth.Stopped();
        }
    }

    private sealed class ListenerRuntimeState
    {
        public ListenerRuntimeState(ControlOscListenerConfig config)
        {
            Config = config;
            Health = ControlSessionHealth.Stopped();
        }

        public ControlOscListenerConfig Config { get; }

        public OSCServer? Server { get; set; }

        public IDisposable? Registration { get; set; }

        public ControlSessionHealth Health { get; set; }
    }
}

public sealed record ControlOscListenerDispatchResult(
    Guid ListenerId,
    Guid DeviceInstanceId,
    IPEndPoint RemoteEndPoint,
    string Address,
    ControlScriptRuntimeSessionResult ScriptResult);
