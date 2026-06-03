using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public interface IControlOscSender
{
    ValueTask SendAsync(string host, int port, string address, IReadOnlyList<OSCArgument> arguments, CancellationToken cancellationToken = default);
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
    private readonly Dictionary<Guid, ControlNodeConfig> _nodes;
    private readonly Dictionary<Guid, List<ControlConnectionConfig>> _outgoing;

    public ControlGraphRuntime(ControlGraphConfig graph, IControlOscSender oscSender)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _oscSender = oscSender ?? throw new ArgumentNullException(nameof(oscSender));
        _nodes = graph.Nodes.ToDictionary(n => n.Id);
        _outgoing = graph.Connections
            .GroupBy(c => c.FromNodeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var validation = Validate(graph);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", validation.Issues.Select(i => i.Message)));
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

    private async Task DispatchAsync(ControlEvent evt, CancellationToken cancellationToken)
    {
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

            case OscOutputControlNodeSettings osc:
                await _oscSender.SendAsync(
                    osc.Host,
                    osc.Port,
                    osc.Address,
                    BuildOscArguments(osc.ArgumentMode, input),
                    cancellationToken).ConfigureAwait(false);
                return [];

            case X32ChannelFaderControlNodeSettings x32:
                await _oscSender.SendAsync(
                    x32.Host,
                    x32.Port,
                    X32Presets.ChannelFaderAddress(x32.Channel),
                    BuildOscArguments(ControlOscArgumentMode.FirstScalarAsFloat, input),
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
            MapRangeControlNodeSettings => ControlPortType.Scalar,
            PassthroughControlNodeSettings => ControlPortType.Any,
            _ => ControlPortType.Any,
        };

    private static ControlPortType GetInputPortType(ControlNodeConfig node, string portId) =>
        node.Settings switch
        {
            MapRangeControlNodeSettings => ControlPortType.Any,
            OscOutputControlNodeSettings => ControlPortType.Any,
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
            default:
                value = 0;
                return false;
        }
    }

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
