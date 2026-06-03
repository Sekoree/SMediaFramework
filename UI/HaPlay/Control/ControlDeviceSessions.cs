using HaPlay.Models;
using OSCLib;
using PMLib;
using PMLib.Accumulators;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ControlGraph;

public enum ControlSessionState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

public sealed record ControlSessionHealth(
    ControlSessionState State,
    string Detail = "",
    DateTimeOffset UpdatedAtUtc = default)
{
    public static ControlSessionHealth Stopped(string detail = "") =>
        new(ControlSessionState.Stopped, detail, DateTimeOffset.UtcNow);

    public static ControlSessionHealth Running(string detail = "") =>
        new(ControlSessionState.Running, detail, DateTimeOffset.UtcNow);

    public static ControlSessionHealth Faulted(string detail) =>
        new(ControlSessionState.Faulted, detail, DateTimeOffset.UtcNow);
}

public sealed class ControlEndpointSessionManager : IControlOscSender, IControlMidiSender, IAsyncDisposable, IDisposable
{
    private readonly IReadOnlyDictionary<Guid, ActionEndpoint> _endpoints;
    private readonly Dictionary<(string Host, int Port), OSCClient> _oscClients = new();
    private readonly Dictionary<Guid, MIDIOutputDevice> _midiOutputs = new();
    private bool _midiInitialized;
    private bool _disposed;

    public ControlEndpointSessionManager(IEnumerable<ActionEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = endpoints.ToDictionary(e => e.Id);
        Health = ControlSessionHealth.Stopped();
    }

    public ControlSessionHealth Health { get; private set; }

    public async ValueTask SendAsync(
        string host,
        int port,
        string address,
        IReadOnlyList<OSCArgument> arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = await GetOscClientAsync(host, port, cancellationToken).ConfigureAwait(false);
        await client.SendMessageAsync(address, arguments, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendControlChangeAsync(
        Guid? endpointId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (endpointId is not { } id)
            throw new InvalidOperationException("MIDI output node requires an endpoint id for real device output.");
        if (!_endpoints.TryGetValue(id, out var endpoint) || endpoint is not MidiActionEndpoint midiEndpoint)
            throw new InvalidOperationException($"MIDI endpoint '{id}' was not found.");

        var device = GetMidiOutput(midiEndpoint);
        var midiChannel = ToZeroBasedMidiChannel(channel);
        var message = highResolution14Bit
            ? ControlChange.HighRes(midiChannel, checked((byte)controller), checked((ushort)value))
            : new ControlChange(midiChannel, checked((byte)controller), checked((byte)value));
        var err = device.Write(message);
        if (err != PmError.NoError)
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());

        return ValueTask.CompletedTask;
    }

    private async ValueTask<OSCClient> GetOscClientAsync(string host, int port, CancellationToken cancellationToken)
    {
        var key = (host, port);
        if (_oscClients.TryGetValue(key, out var existing))
            return existing;

        try
        {
            Health = new ControlSessionHealth(ControlSessionState.Starting, $"Connecting OSC {host}:{port}", DateTimeOffset.UtcNow);
            var client = await OSCClient.CreateAsync(host, port, cancellationToken: cancellationToken).ConfigureAwait(false);
            _oscClients.Add(key, client);
            Health = ControlSessionHealth.Running($"OSC {host}:{port}");
            return client;
        }
        catch (Exception ex)
        {
            Health = ControlSessionHealth.Faulted(ex.Message);
            throw;
        }
    }

    private MIDIOutputDevice GetMidiOutput(MidiActionEndpoint endpoint)
    {
        if (_midiOutputs.TryGetValue(endpoint.Id, out var existing))
            return existing;

        EnsureMidiInitialized();
        var deviceEntry = ResolveMidiOutput(endpoint)
            ?? throw new InvalidOperationException($"MIDI output device '{endpoint.DeviceName ?? endpoint.DeviceId?.ToString() ?? "(auto)"}' was not found.");

        var device = new MIDIOutputDevice(deviceEntry.Id);
        var err = device.Open();
        if (err != PmError.NoError)
        {
            device.Dispose();
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        }

        _midiOutputs.Add(endpoint.Id, device);
        Health = ControlSessionHealth.Running($"MIDI {deviceEntry.Name ?? deviceEntry.Id.ToString()}");
        return device;
    }

    private void EnsureMidiInitialized()
    {
        if (_midiInitialized)
            return;

        var err = PMUtil.Initialize();
        if (err != PmError.NoError)
            throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
        _midiInitialized = true;
    }

    private static PmDeviceEntry? ResolveMidiOutput(MidiActionEndpoint endpoint)
    {
        var outputs = PMUtil.GetOutputDevices();
        if (endpoint.DeviceId is { } id)
        {
            foreach (var output in outputs)
            {
                if (output.Id == id)
                    return output;
            }
        }

        if (!string.IsNullOrWhiteSpace(endpoint.DeviceName))
        {
            foreach (var output in outputs)
            {
                if (string.Equals(output.Name, endpoint.DeviceName, StringComparison.OrdinalIgnoreCase))
                    return output;
            }
        }

        return outputs.Count > 0 ? outputs[0] : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var client in _oscClients.Values)
            await client.DisposeAsync().ConfigureAwait(false);
        _oscClients.Clear();

        foreach (var output in _midiOutputs.Values)
            output.Dispose();
        _midiOutputs.Clear();

        if (_midiInitialized)
        {
            PMUtil.Terminate();
            _midiInitialized = false;
        }

        Health = ControlSessionHealth.Stopped();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var client in _oscClients.Values)
            client.Dispose();
        _oscClients.Clear();

        foreach (var output in _midiOutputs.Values)
            output.Dispose();
        _midiOutputs.Clear();

        if (_midiInitialized)
        {
            PMUtil.Terminate();
            _midiInitialized = false;
        }

        Health = ControlSessionHealth.Stopped();
    }

    private static byte ToZeroBasedMidiChannel(int channel) =>
        checked((byte)Math.Clamp(channel - 1, 0, 15));
}

public sealed class ControlOscInputSession : IAsyncDisposable, IDisposable
{
    private readonly Guid _nodeId;
    private readonly OscInputControlNodeSettings _settings;
    private readonly Func<Guid, string, IReadOnlyList<OSCArgument>, Guid?, CancellationToken, Task> _dispatchAsync;
    private OSCServer? _server;
    private IDisposable? _registration;
    private bool _disposed;

    public ControlOscInputSession(
        Guid nodeId,
        OscInputControlNodeSettings settings,
        Func<Guid, string, IReadOnlyList<OSCArgument>, Guid?, CancellationToken, Task> dispatchAsync)
    {
        _nodeId = nodeId;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        Health = ControlSessionHealth.Stopped();
    }

    public ControlSessionHealth Health { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is not null)
            return;

        try
        {
            Health = new ControlSessionHealth(ControlSessionState.Starting, $"Listening OSC {_settings.LocalPort}", DateTimeOffset.UtcNow);
            var server = new OSCServer(new OSCServerOptions { Port = _settings.LocalPort });
            _registration = server.RegisterHandler(
                _settings.AddressPattern,
                (context, ct) => new ValueTask(_dispatchAsync(
                    _nodeId,
                    context.Message.Address,
                    context.Message.Arguments,
                    _settings.EndpointId,
                    ct)));
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            _server = server;
            Health = ControlSessionHealth.Running($"OSC listen {_settings.LocalPort}");
        }
        catch (Exception ex)
        {
            Health = ControlSessionHealth.Faulted(ex.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var server = _server;
        if (server is null)
            return;

        _registration?.Dispose();
        _registration = null;
        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        server.Dispose();
        _server = null;
        Health = ControlSessionHealth.Stopped();
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
        _registration?.Dispose();
        _server?.Dispose();
        _registration = null;
        _server = null;
        Health = ControlSessionHealth.Stopped();
    }
}

public sealed class ControlMidiInputSession : IDisposable
{
    private readonly Guid _nodeId;
    private readonly MidiInputControlNodeSettings _settings;
    private readonly Func<Guid, int, int, int, bool, CancellationToken, Task> _dispatchAsync;
    private readonly HighResCCAccumulator _highResAccumulator = new();
    private MIDIInputDevice? _device;
    private bool _disposed;

    public ControlMidiInputSession(
        Guid nodeId,
        int deviceId,
        MidiInputControlNodeSettings settings,
        Func<Guid, int, int, int, bool, CancellationToken, Task> dispatchAsync)
    {
        _nodeId = nodeId;
        DeviceId = deviceId;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        _highResAccumulator.HighResChanged += OnHighResChanged;
        Health = ControlSessionHealth.Stopped();
    }

    public int DeviceId { get; }

    public ControlSessionHealth Health { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_device is not null)
            return;

        try
        {
            Health = new ControlSessionHealth(ControlSessionState.Starting, $"Opening MIDI input {DeviceId}", DateTimeOffset.UtcNow);
            var device = new MIDIInputDevice(DeviceId);
            device.MessageReceived += OnMessageReceived;
            var err = device.Open();
            if (err != PmError.NoError)
            {
                device.MessageReceived -= OnMessageReceived;
                device.Dispose();
                throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
            }

            _device = device;
            Health = ControlSessionHealth.Running($"MIDI input {device.Name ?? DeviceId.ToString()}");
        }
        catch (Exception ex)
        {
            Health = ControlSessionHealth.Faulted(ex.Message);
            throw;
        }
    }

    private void OnMessageReceived(object? sender, IMIDIMessage message)
    {
        if (message is not ControlChange cc)
            return;

        if (_settings.HighResolution14Bit)
        {
            _highResAccumulator.Process(cc);
            return;
        }

        if (Matches(cc))
            _ = _dispatchAsync(_nodeId, cc.Channel + 1, cc.Controller, cc.Value, false, CancellationToken.None);
    }

    private void OnHighResChanged(ControlChange cc)
    {
        if (Matches(cc))
            _ = _dispatchAsync(_nodeId, cc.Channel + 1, cc.Controller, cc.Value, true, CancellationToken.None);
    }

    private bool Matches(ControlChange cc) =>
        (_settings.Channel <= 0 || cc.Channel + 1 == _settings.Channel)
        && cc.Controller == _settings.Controller;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _highResAccumulator.HighResChanged -= OnHighResChanged;
        if (_device is not null)
        {
            _device.MessageReceived -= OnMessageReceived;
            _device.Dispose();
            _device = null;
        }

        Health = ControlSessionHealth.Stopped();
    }
}
