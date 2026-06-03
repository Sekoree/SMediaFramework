using HaPlay.Models;
using PMLib;
using PMLib.Types;

namespace HaPlay.ControlGraph;

public sealed class ControlGraphSession : IAsyncDisposable, IDisposable
{
    private readonly ControlGraphConfig _graph;
    private readonly IReadOnlyDictionary<Guid, ActionEndpoint> _endpoints;
    private readonly List<ControlOscInputSession> _oscInputs = new();
    private readonly List<ControlMidiInputSession> _midiInputs = new();
    private ControlEndpointSessionManager? _endpointSessions;
    private ControlMidiLibraryLease? _midiResolveLease;
    private bool _disposed;

    public ControlGraphSession(ControlGraphConfig graph, IEnumerable<ActionEndpoint> endpoints)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = endpoints.ToDictionary(e => e.Id);
        Health = ControlSessionHealth.Stopped();
    }

    public ControlSessionHealth Health { get; private set; }

    public ControlGraphRuntime? Runtime { get; private set; }

    public bool IsRunning => Health.State == ControlSessionState.Running;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        try
        {
            Health = new ControlSessionHealth(ControlSessionState.Starting, $"Starting graph '{_graph.Name}'", DateTimeOffset.UtcNow);
            var endpointSessions = new ControlEndpointSessionManager(_endpoints.Values);
            var runtime = new ControlGraphRuntime(_graph, endpointSessions, endpointSessions);

            foreach (var node in _graph.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (node.Settings)
                {
                    case OscInputControlNodeSettings osc:
                        var oscSession = new ControlOscInputSession(node.Id, osc, runtime.InjectOscMessageAsync);
                        await oscSession.StartAsync(cancellationToken).ConfigureAwait(false);
                        _oscInputs.Add(oscSession);
                        break;
                    case MidiInputControlNodeSettings midi:
                        var midiSession = new ControlMidiInputSession(
                            node.Id,
                            ResolveMidiInputDeviceId(midi),
                            midi,
                            runtime.InjectMidiControlChangeAsync);
                        midiSession.Start();
                        _midiInputs.Add(midiSession);
                        break;
                }
            }

            _endpointSessions = endpointSessions;
            Runtime = runtime;
            Health = ControlSessionHealth.Running($"Graph '{_graph.Name}' running");
        }
        catch (Exception ex)
        {
            await StopAsync().ConfigureAwait(false);
            Health = ControlSessionHealth.Faulted(ex.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        for (var i = _oscInputs.Count - 1; i >= 0; i--)
            await _oscInputs[i].DisposeAsync().ConfigureAwait(false);
        _oscInputs.Clear();

        for (var i = _midiInputs.Count - 1; i >= 0; i--)
            _midiInputs[i].Dispose();
        _midiInputs.Clear();

        if (_endpointSessions is not null)
        {
            await _endpointSessions.DisposeAsync().ConfigureAwait(false);
            _endpointSessions = null;
        }

        _midiResolveLease?.Dispose();
        _midiResolveLease = null;
        Runtime = null;
        Health = ControlSessionHealth.Stopped();
    }

    private int ResolveMidiInputDeviceId(MidiInputControlNodeSettings settings)
    {
        if (settings.EndpointId is not { } endpointId)
            throw new InvalidOperationException("MIDI input node requires an endpoint id for real device input.");
        if (!_endpoints.TryGetValue(endpointId, out var endpoint) || endpoint is not MidiActionEndpoint midiEndpoint)
            throw new InvalidOperationException($"MIDI input endpoint '{endpointId}' was not found.");

        _midiResolveLease ??= ControlMidiLibraryLease.Acquire();
        var device = ResolveMidiInput(midiEndpoint)
            ?? throw new InvalidOperationException($"MIDI input device '{midiEndpoint.DeviceName ?? midiEndpoint.DeviceId?.ToString() ?? "(auto)"}' was not found.");
        return device.Id;
    }

    private static PmDeviceEntry? ResolveMidiInput(MidiActionEndpoint endpoint)
    {
        var inputs = PMUtil.GetInputDevices();
        if (endpoint.DeviceId is { } id)
        {
            foreach (var input in inputs)
            {
                if (input.Id == id)
                    return input;
            }
        }

        if (!string.IsNullOrWhiteSpace(endpoint.DeviceName))
        {
            foreach (var input in inputs)
            {
                if (string.Equals(input.Name, endpoint.DeviceName, StringComparison.OrdinalIgnoreCase))
                    return input;
            }
        }

        return inputs.Count > 0 ? inputs[0] : null;
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

        for (var i = _midiInputs.Count - 1; i >= 0; i--)
            _midiInputs[i].Dispose();
        _midiInputs.Clear();

        for (var i = _oscInputs.Count - 1; i >= 0; i--)
            _oscInputs[i].Dispose();
        _oscInputs.Clear();

        _endpointSessions?.Dispose();
        _endpointSessions = null;
        _midiResolveLease?.Dispose();
        _midiResolveLease = null;
        Runtime = null;
        Health = ControlSessionHealth.Stopped();
    }
}
