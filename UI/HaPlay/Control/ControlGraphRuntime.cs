using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public interface IControlOscSender
{
    ValueTask SendAsync(string host, int port, string address, IReadOnlyList<OSCArgument> arguments, CancellationToken cancellationToken = default);
}

public interface IControlMidiSender
{
    ValueTask SendControlChangeAsync(
        Guid? endpointId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        CancellationToken cancellationToken = default);
}

public sealed record ControlGraphValidationIssue(string Code, string Message, Guid? NodeId = null, Guid? ConnectionId = null);

public sealed record ControlGraphValidationResult(IReadOnlyList<ControlGraphValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class ControlGraphRuntime
{
    private readonly ControlGraphConfig _graph;
    private readonly IControlOscSender _oscSender;
    private readonly IControlMidiSender _midiSender;
    private readonly Dictionary<Guid, ControlNodeConfig> _nodes;
    private readonly Dictionary<Guid, List<ControlConnectionConfig>> _outgoing;
    private readonly Dictionary<Guid, DateTimeOffset> _lastOutputSendUtc = new();
    private readonly Dictionary<Guid, SoftTakeoverState> _softTakeover = new();
    private readonly ControlScriptHost _scriptHost = new();
    private readonly List<ControlScriptDiagnostic> _scriptDiagnostics = new();

    public ControlGraphRuntime(ControlGraphConfig graph, IControlOscSender oscSender, IControlMidiSender? midiSender = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _oscSender = oscSender ?? throw new ArgumentNullException(nameof(oscSender));
        _midiSender = midiSender ?? NullControlMidiSender.Instance;
        _nodes = graph.Nodes.ToDictionary(n => n.Id);
        _outgoing = graph.Connections
            .GroupBy(c => c.FromNodeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var validation = Validate(graph);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", validation.Issues.Select(i => i.Message)));
    }

    public IReadOnlyList<ControlScriptDiagnostic> ScriptDiagnostics => _scriptDiagnostics;
    public event EventHandler<ControlEvent>? EventDispatched;

    public void SetSoftTakeoverTarget(Guid midiInputNodeId, double normalizedValue)
    {
        if (!_nodes.TryGetValue(midiInputNodeId, out var node))
            throw new ArgumentException($"Unknown control node '{midiInputNodeId}'.", nameof(midiInputNodeId));
        if (node.Settings is not MidiInputControlNodeSettings)
            throw new ArgumentException($"Node '{midiInputNodeId}' is not a MIDI input node.", nameof(midiInputNodeId));

        _softTakeover[midiInputNodeId] = new SoftTakeoverState(Math.Clamp(normalizedValue, 0.0, 1.0), Captured: false);
    }

    public static ControlGraphValidationResult Validate(ControlGraphConfig graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<ControlGraphValidationIssue>();
        var nodes = graph.Nodes.ToDictionary(n => n.Id);

        foreach (var connection in graph.Connections)
        {
            if (!nodes.TryGetValue(connection.FromNodeId, out var from))
            {
                issues.Add(new ControlGraphValidationIssue(
                    "missing-from-node",
                    $"Connection '{connection.Id}' references missing source node '{connection.FromNodeId}'.",
                    ConnectionId: connection.Id));
                continue;
            }

            if (!nodes.TryGetValue(connection.ToNodeId, out var to))
            {
                issues.Add(new ControlGraphValidationIssue(
                    "missing-to-node",
                    $"Connection '{connection.Id}' references missing target node '{connection.ToNodeId}'.",
                    ConnectionId: connection.Id));
                continue;
            }

            var fromType = GetOutputPortType(from, connection.FromPortId);
            var toType = GetInputPortType(to, connection.ToPortId);
            if (!PortsCompatible(fromType, toType))
            {
                issues.Add(new ControlGraphValidationIssue(
                    "incompatible-ports",
                    $"Connection '{connection.Id}' connects {fromType} to {toType}.",
                    NodeId: to.Id,
                    ConnectionId: connection.Id));
            }
        }

        return new ControlGraphValidationResult(issues);
    }

    public Task InjectMidiControlChangeAsync(
        Guid nodeId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit = false,
        CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
            throw new ArgumentException($"Unknown control node '{nodeId}'.", nameof(nodeId));
        if (node.Settings is not MidiInputControlNodeSettings settings)
            throw new ArgumentException($"Node '{nodeId}' is not a MIDI input node.", nameof(nodeId));
        if (settings.Channel > 0 && channel != settings.Channel)
            return Task.CompletedTask;
        if (controller != settings.Controller)
            return Task.CompletedTask;
        if (!AllowsSoftTakeover(nodeId, settings, value))
            return Task.CompletedTask;

        var evt = new MidiControlEvent(
            DateTimeOffset.UtcNow,
            nodeId,
            settings.EndpointId ?? nodeId,
            Guid.NewGuid(),
            channel,
            controller,
            value,
            highResolution14Bit);
        return DispatchAsync(evt, cancellationToken);
    }

    public Task InjectOscMessageAsync(
        Guid nodeId,
        string address,
        IReadOnlyList<OSCArgument> arguments,
        Guid? originId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
            throw new ArgumentException($"Unknown control node '{nodeId}'.", nameof(nodeId));
        if (node.Settings is not OscInputControlNodeSettings settings)
            throw new ArgumentException($"Node '{nodeId}' is not an OSC input node.", nameof(nodeId));
        if (!OscAddressMatches(settings.AddressPattern, address))
            return Task.CompletedTask;

        var evt = new OscControlEvent(
            DateTimeOffset.UtcNow,
            nodeId,
            originId ?? settings.EndpointId ?? nodeId,
            Guid.NewGuid(),
            address,
            arguments);
        return DispatchAsync(evt, cancellationToken);
    }

    private async Task DispatchAsync(ControlEvent evt, CancellationToken cancellationToken)
    {
        EventDispatched?.Invoke(this, evt);
        if (!_outgoing.TryGetValue(evt.SourceNodeId, out var connections))
            return;

        foreach (var connection in connections)
        {
            if (!_nodes.TryGetValue(connection.ToNodeId, out var target))
                continue;

            var forwarded = await ProcessNodeAsync(target, evt, cancellationToken).ConfigureAwait(false);
            foreach (var next in forwarded)
                await DispatchAsync(next, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ControlEvent>> ProcessNodeAsync(
        ControlNodeConfig node,
        ControlEvent input,
        CancellationToken cancellationToken)
    {
        var path = AppendPath(input.Path, node.Id);
        switch (node.Settings)
        {
            case MapRangeControlNodeSettings map:
                if (!TryGetScalar(input, out var scalar))
                    return [];
                var mapped = ControlMath.MapRange(
                    scalar,
                    map.InputMin,
                    map.InputMax,
                    map.OutputMin,
                    map.OutputMax,
                    map.Clamp);
                return
                [
                    new ScalarControlEvent(
                        DateTimeOffset.UtcNow,
                        node.Id,
                        input.OriginId,
                        input.CorrelationId,
                        mapped,
                        path)
                ];

            case ScriptTransformControlNodeSettings script:
                var scriptResult = _scriptHost.ExecuteWithDiagnostics(script, input, node.Id);
                if (scriptResult.Diagnostics.Count > 0)
                    _scriptDiagnostics.AddRange(scriptResult.Diagnostics);
                return scriptResult.Events;

            case OscOutputControlNodeSettings osc:
                if (ShouldSuppressEcho(input, osc.EndpointId, osc.FeedbackMode))
                    return [];
                if (ShouldRateLimit(node.Id, osc.MinSendIntervalMs))
                    return [];
                await _oscSender.SendAsync(
                    osc.Host,
                    osc.Port,
                    osc.Address,
                    BuildOscArguments(osc.ArgumentMode, input),
                    cancellationToken).ConfigureAwait(false);
                return [];

            case X32ChannelFaderControlNodeSettings x32:
                if (ShouldSuppressEcho(input, x32.EndpointId, x32.FeedbackMode))
                    return [];
                if (ShouldRateLimit(node.Id, x32.MinSendIntervalMs))
                    return [];
                await _oscSender.SendAsync(
                    x32.Host,
                    x32.Port,
                    X32Presets.ChannelFaderAddress(x32.Channel),
                    BuildOscArguments(ControlOscArgumentMode.FirstScalarAsFloat, input),
                    cancellationToken).ConfigureAwait(false);
                return [];

            case MidiOutputControlNodeSettings midi:
                if (ShouldSuppressEcho(input, midi.EndpointId, midi.FeedbackMode))
                    return [];
                if (ShouldRateLimit(node.Id, midi.MinSendIntervalMs))
                    return [];
                if (!TryGetScalar(input, out var midiScalar))
                    return [];
                await _midiSender.SendControlChangeAsync(
                    midi.EndpointId,
                    midi.Channel,
                    midi.Controller,
                    ToMidiValue(midiScalar, midi.HighResolution14Bit),
                    midi.HighResolution14Bit,
                    cancellationToken).ConfigureAwait(false);
                return [];

            default:
                return [input with { SourceNodeId = node.Id, Path = path }];
        }
    }

    private static ControlPortType GetOutputPortType(ControlNodeConfig node, string portId) =>
        node.Settings switch
        {
            MidiInputControlNodeSettings => ControlPortType.Midi,
            OscInputControlNodeSettings => ControlPortType.Osc,
            MapRangeControlNodeSettings => ControlPortType.Scalar,
            ScriptTransformControlNodeSettings => ControlPortType.Any,
            PassthroughControlNodeSettings => ControlPortType.Any,
            _ => ControlPortType.Any,
        };

    private static ControlPortType GetInputPortType(ControlNodeConfig node, string portId) =>
        node.Settings switch
        {
            MapRangeControlNodeSettings => ControlPortType.Any,
            ScriptTransformControlNodeSettings => ControlPortType.Any,
            OscOutputControlNodeSettings => ControlPortType.Any,
            MidiOutputControlNodeSettings => ControlPortType.Any,
            X32ChannelFaderControlNodeSettings => ControlPortType.Scalar,
            PassthroughControlNodeSettings => ControlPortType.Any,
            _ => ControlPortType.Any,
        };

    private static bool PortsCompatible(ControlPortType from, ControlPortType to) =>
        from == ControlPortType.Any || to == ControlPortType.Any || from == to;

    private static bool TryGetScalar(ControlEvent evt, out double value)
    {
        switch (evt)
        {
            case ScalarControlEvent scalar:
                value = scalar.Value;
                return true;
            case MidiControlEvent midi:
                value = midi.Value;
                return true;
            case OscControlEvent { Arguments.Count: > 0 } osc:
                return TryGetOscScalar(osc.Arguments[0], out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryGetOscScalar(OSCArgument argument, out double value)
    {
        switch (argument.Type)
        {
            case OSCArgumentType.Float32:
                value = argument.AsFloat32();
                return true;
            case OSCArgumentType.Double64:
                value = argument.AsDouble64();
                return true;
            case OSCArgumentType.Int32:
                value = argument.AsInt32();
                return true;
            case OSCArgumentType.Int64:
                value = argument.AsInt64();
                return true;
            case OSCArgumentType.True:
                value = 1;
                return true;
            case OSCArgumentType.False:
                value = 0;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static int ToMidiValue(double value, bool highResolution14Bit)
    {
        var max = highResolution14Bit ? 16383 : 127;
        return (int)Math.Clamp(Math.Round(value), 0, max);
    }

    private static bool ShouldSuppressEcho(ControlEvent input, Guid? endpointId, ControlFeedbackMode feedbackMode) =>
        (feedbackMode == ControlFeedbackMode.DoNotEchoToOrigin
         || feedbackMode == ControlFeedbackMode.MotorFeedbackOnly)
        && endpointId.HasValue
        && input.OriginId == endpointId.Value;

    private bool ShouldRateLimit(Guid nodeId, int minSendIntervalMs)
    {
        if (minSendIntervalMs <= 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        if (_lastOutputSendUtc.TryGetValue(nodeId, out var last)
            && now - last < TimeSpan.FromMilliseconds(minSendIntervalMs))
        {
            return true;
        }

        _lastOutputSendUtc[nodeId] = now;
        return false;
    }

    private bool AllowsSoftTakeover(Guid nodeId, MidiInputControlNodeSettings settings, int rawValue)
    {
        if (!settings.SoftTakeoverEnabled)
            return true;
        if (!_softTakeover.TryGetValue(nodeId, out var state))
            return true;
        if (state.Captured)
            return true;

        var normalized = NormalizeMidiValue(rawValue, settings.HighResolution14Bit);
        var tolerance = Math.Clamp(settings.SoftTakeoverTolerance, 0.0, 1.0);
        if (Math.Abs(normalized - state.TargetNormalizedValue) > tolerance)
            return false;

        _softTakeover[nodeId] = state with { Captured = true };
        return true;
    }

    private static double NormalizeMidiValue(int value, bool highResolution14Bit)
    {
        var max = highResolution14Bit ? 16383.0 : 127.0;
        return Math.Clamp(value / max, 0.0, 1.0);
    }

    private static bool OscAddressMatches(string pattern, string address) =>
        string.IsNullOrWhiteSpace(pattern)
        || string.Equals(pattern, address, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<OSCArgument> BuildOscArguments(ControlOscArgumentMode mode, ControlEvent input)
    {
        return mode switch
        {
            ControlOscArgumentMode.None => [],
            ControlOscArgumentMode.FirstScalarAsFloat when TryGetScalar(input, out var scalar) =>
                [OSCArgument.Float32((float)scalar)],
            ControlOscArgumentMode.FirstScalarAsInt when TryGetScalar(input, out var scalar) =>
                [OSCArgument.Int32((int)Math.Round(scalar))],
            ControlOscArgumentMode.FirstTextAsString when input is TextControlEvent text =>
                [OSCArgument.String(text.Value)],
            _ => [],
        };
    }

    private static IReadOnlyList<Guid> AppendPath(IReadOnlyList<Guid>? path, Guid nodeId)
    {
        if (path is null || path.Count == 0)
            return [nodeId];
        var next = new Guid[path.Count + 1];
        for (var i = 0; i < path.Count; i++)
            next[i] = path[i];
        next[^1] = nodeId;
        return next;
    }
}

internal sealed class NullControlMidiSender : IControlMidiSender
{
    public static NullControlMidiSender Instance { get; } = new();

    private NullControlMidiSender()
    {
    }

    public ValueTask SendControlChangeAsync(
        Guid? endpointId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

internal sealed record SoftTakeoverState(double TargetNormalizedValue, bool Captured);

public static class ControlMath
{
    public static double MapRange(
        double value,
        double inMin,
        double inMax,
        double outMin,
        double outMax,
        bool clamp)
    {
        if (Math.Abs(inMax - inMin) < double.Epsilon)
            return outMin;
        var t = (value - inMin) / (inMax - inMin);
        if (clamp)
            t = Math.Clamp(t, 0.0, 1.0);
        return outMin + (outMax - outMin) * t;
    }
}
