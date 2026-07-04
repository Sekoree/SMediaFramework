using S.Media.Core.Audio;

namespace S.Media.Session;

public enum RoutingTransitionKind
{
    Cut,
    Fade,
    Transform,
    Crop,
}

/// <summary>
/// One source→output patch in the routing scene. <paramref name="ChannelMatrix"/> carries the N→M audio
/// remap (03 §6) as serializable show data — the <c>map[outCh] = inCh</c> encoding of
/// <see cref="ChannelMap"/> (<c>-1</c> = silence); <c>null</c> means the router's source-derived default
/// (<see cref="ChannelMap.DefaultFor"/>). In <see cref="ShowDocument"/> playback, <paramref name="SourceId"/>
/// matches the clip binding's cue id. Use <see cref="ToChannelMap"/> to materialize it for the router.
/// </summary>
public sealed record OutputPatchRoute(
    string SourceId,
    string OutputId,
    bool Enabled = true,
    string? FormatVersion = null,
    int[]? ChannelMatrix = null)
{
    /// <summary>Materializes the N→M <see cref="ChannelMap"/>, or <c>null</c> to use the router's source-derived default.</summary>
    public ChannelMap? ToChannelMap() => ChannelMatrix is { Length: > 0 } m ? new ChannelMap(m) : null;
}

public sealed record SceneLayerDefinition(
    string LayerId,
    string SourceId,
    int ZOrder,
    float Opacity = 1f,
    string? Transform = null,
    string? Crop = null);

public sealed record RoutingTransition(
    string TargetId,
    RoutingTransitionKind Kind,
    TimeSpan Duration,
    float? FromOpacity = null,
    float? ToOpacity = null);

public sealed record SyncGroupDefinition(
    string GroupId,
    string MasterClockId,
    IReadOnlyList<string> MemberIds);

public sealed record NDIEndpointPreset(
    string Id,
    string Name,
    bool IsInput);

public sealed record OperatorEndpointMetrics(
    string Id,
    long Submitted,
    long Dropped,
    int QueueDepth,
    string? State = null);

public sealed record RoutingSceneSnapshot(
    IReadOnlyList<OutputPatchRoute> Routes,
    IReadOnlyList<SceneLayerDefinition> Layers,
    IReadOnlyList<RoutingTransition> Transitions,
    IReadOnlyList<SyncGroupDefinition> SyncGroups,
    IReadOnlyList<NDIEndpointPreset> NDIPresets,
    string? PreviewOutputId,
    string? ProgramOutputId,
    IReadOnlyList<OperatorEndpointMetrics> Metrics);

public sealed record RoutingSceneApplyPlan(
    IReadOnlyList<OutputPatchRoute> RoutesToAddOrUpdate,
    IReadOnlyList<OutputPatchRoute> RoutesToRemove,
    IReadOnlyList<SceneLayerDefinition> LayersToAddOrUpdate,
    IReadOnlyList<SceneLayerDefinition> LayersToRemove);

public sealed class RoutingScene
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string SourceId, string OutputId), OutputPatchRoute> _routes = [];
    private readonly Dictionary<string, SceneLayerDefinition> _layers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoutingTransition> _transitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SyncGroupDefinition> _syncGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NDIEndpointPreset> _ndiPresets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperatorEndpointMetrics> _metrics = new(StringComparer.Ordinal);
    private string? _previewOutputId;
    private string? _programOutputId;

    public void SetRoute(OutputPatchRoute route)
    {
        ArgumentException.ThrowIfNullOrEmpty(route.SourceId);
        ArgumentException.ThrowIfNullOrEmpty(route.OutputId);
        lock (_gate)
            _routes[(route.SourceId, route.OutputId)] = route;
    }

    public bool RemoveRoute(string sourceId, string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
            return _routes.Remove((sourceId, outputId));
    }

    public void UpsertLayer(SceneLayerDefinition layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(layer.LayerId);
        ArgumentException.ThrowIfNullOrEmpty(layer.SourceId);
        lock (_gate)
            _layers[layer.LayerId] = layer;
    }

    public bool RemoveLayer(string layerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(layerId);
        lock (_gate)
            return _layers.Remove(layerId);
    }

    public void SetTransition(RoutingTransition transition)
    {
        ArgumentException.ThrowIfNullOrEmpty(transition.TargetId);
        lock (_gate)
            _transitions[transition.TargetId] = transition;
    }

    public void SetSyncGroup(SyncGroupDefinition group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group.GroupId);
        ArgumentException.ThrowIfNullOrEmpty(group.MasterClockId);
        lock (_gate)
            _syncGroups[group.GroupId] = group;
    }

    public void SetNDIPreset(NDIEndpointPreset preset)
    {
        ArgumentException.ThrowIfNullOrEmpty(preset.Id);
        ArgumentException.ThrowIfNullOrEmpty(preset.Name);
        lock (_gate)
            _ndiPresets[preset.Id] = preset;
    }

    public void SetPreviewProgram(string? previewOutputId, string? programOutputId)
    {
        lock (_gate)
        {
            _previewOutputId = previewOutputId;
            _programOutputId = programOutputId;
        }
    }

    public void SetMetrics(OperatorEndpointMetrics metrics)
    {
        ArgumentException.ThrowIfNullOrEmpty(metrics.Id);
        lock (_gate)
            _metrics[metrics.Id] = metrics;
    }

    public RoutingSceneSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RoutingSceneSnapshot(
                _routes.Values.OrderBy(r => r.SourceId, StringComparer.Ordinal).ThenBy(r => r.OutputId, StringComparer.Ordinal).ToArray(),
                _layers.Values.OrderBy(l => l.ZOrder).ThenBy(l => l.LayerId, StringComparer.Ordinal).ToArray(),
                _transitions.Values.OrderBy(t => t.TargetId, StringComparer.Ordinal).ToArray(),
                _syncGroups.Values.OrderBy(g => g.GroupId, StringComparer.Ordinal).ToArray(),
                _ndiPresets.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
                _previewOutputId,
                _programOutputId,
                _metrics.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray());
        }
    }

    public static RoutingSceneApplyPlan PlanChanges(RoutingSceneSnapshot before, RoutingSceneSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeRoutes = before.Routes.ToDictionary(r => (r.SourceId, r.OutputId));
        var afterRoutes = after.Routes.ToDictionary(r => (r.SourceId, r.OutputId));
        var beforeLayers = before.Layers.ToDictionary(l => l.LayerId, StringComparer.Ordinal);
        var afterLayers = after.Layers.ToDictionary(l => l.LayerId, StringComparer.Ordinal);

        return new RoutingSceneApplyPlan(
            afterRoutes.Where(kv => !beforeRoutes.TryGetValue(kv.Key, out var current) || current != kv.Value)
                .Select(kv => kv.Value)
                .OrderBy(r => r.SourceId, StringComparer.Ordinal)
                .ThenBy(r => r.OutputId, StringComparer.Ordinal)
                .ToArray(),
            beforeRoutes.Where(kv => !afterRoutes.ContainsKey(kv.Key))
                .Select(kv => kv.Value)
                .OrderBy(r => r.SourceId, StringComparer.Ordinal)
                .ThenBy(r => r.OutputId, StringComparer.Ordinal)
                .ToArray(),
            afterLayers.Where(kv => !beforeLayers.TryGetValue(kv.Key, out var current) || current != kv.Value)
                .Select(kv => kv.Value)
                .OrderBy(l => l.ZOrder)
                .ThenBy(l => l.LayerId, StringComparer.Ordinal)
                .ToArray(),
            beforeLayers.Where(kv => !afterLayers.ContainsKey(kv.Key))
                .Select(kv => kv.Value)
                .OrderBy(l => l.ZOrder)
                .ThenBy(l => l.LayerId, StringComparer.Ordinal)
                .ToArray());
    }
}
