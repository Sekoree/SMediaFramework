using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>Immutable per-group transport snapshot — a query result, never mutated by the caller (D5).</summary>
public sealed record TransportSnapshot(
    string GroupId,
    TimeSpan SessionTime,
    TimeSpan ClipPosition,
    bool IsRunning);

/// <summary>
/// The headless home of a show (D5): cues, clips, and transport groups behind an internal dispatcher.
/// Commands marshal onto the session thread and queries return immutable snapshots — the API never assumes
/// a UI thread (the <c>ui_thread_observable_property_sets</c> bug class is structurally impossible here).
/// One <see cref="SessionClock"/> per transport group (D4); clips open through <see cref="IMediaRegistry"/>
/// (D6); when an <see cref="IAudioBackend"/> is supplied, each group plays on a master output (D11).
/// </summary>
public sealed class ShowSession : IAsyncDisposable
{
    /// <summary>The implicit group cues fall into when <see cref="CueDefinition.GroupId"/> is null.</summary>
    public const string DefaultGroup = "main";

    /// <summary>The output id of a transport group's master audio output — target it from an
    /// <see cref="OutputPatchRoute"/> (<c>OutputId</c>) to apply an N→M channel remap when clips play (03 §6).</summary>
    public const string MasterOutputId = "_master";

    private readonly IMediaRegistry _registry;
    private readonly IAudioBackend? _audioBackend;
    private readonly string? _outputDeviceId;
    private readonly CueGraph _cueGraph = new();
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputPatchRoute> _routes = [];
    private IReadOnlyList<ShowAudioOutput> _audioOutputs = [];
    private readonly SessionDispatcher _dispatcher = new("show-session");
    private volatile bool _disposed;

    /// <param name="audioBackend">Optional. When supplied, each transport group plays its active clip on a
    /// master output created on this backend (D11). Null runs the cue/transport mechanics with no device.</param>
    public ShowSession(IMediaRegistry registry, IAudioBackend? audioBackend = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audioBackend = audioBackend;
        if (audioBackend is not null)
        {
            var devices = audioBackend.EnumerateOutputDevices();
            _outputDeviceId = (devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault())?.Id;
        }
    }

    /// <summary>The registry clips open through (frozen capabilities — D6).</summary>
    public IMediaRegistry Registry => _registry;

    // --- dispatcher (D5) ---------------------------------------------------------------------------

    /// <summary>Fire-and-forget a command on the session thread (runs inline if already on it).</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatcher.IsOnDispatcherThread)
        {
            action();
            return;
        }

        if (!_dispatcher.Post(action))
            throw new ObjectDisposedException(nameof(ShowSession));
    }

    /// <summary>Marshals <paramref name="func"/> onto the session thread and awaits its result. A reentrant
    /// call (already on the session thread) runs inline to avoid self-deadlock.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatcher.IsOnDispatcherThread)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return _dispatcher.InvokeAsync(func);
    }

    /// <summary>Marshals a non-returning command onto the session thread.</summary>
    public Task InvokeAsync(Func<Task> func) =>
        InvokeAsync(async () =>
        {
            await func().ConfigureAwait(false);
            return true;
        });

    // --- show loading ------------------------------------------------------------------------------

    /// <summary>
    /// Builds the cue graph from <paramref name="document"/>: each clip-bound cue, when fired, opens its
    /// media through the registry and plays it on the cue's transport group. Call before firing cues.
    /// </summary>
    public void LoadDocument(ShowDocument document) =>
        LoadDocumentAsync(document).GetAwaiter().GetResult();

    /// <summary>Asynchronously loads a show document on the session dispatcher.</summary>
    public Task LoadDocumentAsync(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return InvokeAsync(() =>
        {
            LoadDocumentCore(document);
            return Task.CompletedTask;
        });
    }

    private void LoadDocumentCore(ShowDocument document)
    {
        foreach (var group in _groups.Values)
            group.Dispose();
        _groups.Clear();

        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();

        _cueGraph.Clear();
        _routes = document.Routes;
        _audioOutputs = document.AudioOutputs;

        foreach (var comp in document.Compositions)
        {
            var definition = new ClipCompositionDefinition(
                comp.Id, comp.Name, comp.Width, comp.Height, comp.FrameRateNum, comp.FrameRateDen);
            // Headless: the default CPU compositor; a discarding lease keeps the pump composing with no device.
            var lease = new ClipCompositionOutputLease(
                $"{comp.Id}_out", comp.Name, new DiscardingVideoOutput(), DisposeOutputOnRuntimeDispose: true);
            _compositions[comp.Id] = new ClipCompositionRuntime(
                definition, [lease], compositionMapping: comp.OutputMapping);
        }

        var clipsByCue = document.Clips.ToDictionary(c => c.CueId, StringComparer.Ordinal);
        foreach (var cue in document.Cues.OrderBy(c => c.Number))
        {
            var groupId = cue.GroupId ?? DefaultGroup;
            var binding = clipsByCue.GetValueOrDefault(cue.Id);
            _cueGraph.AddCue(cue, ct => PlayClipAsync(groupId, binding, ct));
        }
    }

    private ValueTask PlayClipAsync(string groupId, ShowClipBinding? binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding is null)
            return ValueTask.CompletedTask; // a control/stop cue with no media of its own

        var group = GetOrAddGroup(groupId);

        // For a composition-bound clip, mint the layer first and open the clip with the layer's output as the
        // video negotiation lead: the layer declares the compositor's accepted pixel formats, so a source that
        // only resolves its format during negotiation (e.g. a hardware-decoded MP4) can still negotiate. The
        // clip's video then feeds the layer directly, which the composition pump composites (CPU headless).
        ClipCompositionRuntime.LayerSlot? layer = null;
        if (binding.CompositionId is { } compositionId &&
            _compositions.TryGetValue(compositionId, out var composition))
        {
            var placement = new VideoPlacementSpec(compositionId, binding.LayerIndex, DestWidth: 1, DestHeight: 1);
            layer = composition.AddLayer(composition.CanvasFormat, placement);
        }

        var graphBuilder = MediaGraphBuilder.File(binding.MediaPath);
        if (binding.AudioStreamIndex is { } audioTrack)
            graphBuilder.WithOptions(o => o with { AudioStreamIndex = audioTrack }); // multi-track select (03 §6)
        if (layer is not null)
            graphBuilder.WithVideoOutput(layer.Output);

        MediaGraph graph;
        try
        {
            graph = graphBuilder.Build(_registry);
        }
        catch
        {
            layer?.Dispose();
            throw;
        }

        try
        {
            var outputs = new List<IAudioOutput>();
            if (_audioBackend is not null && graph.Player.AudioRouter is not null)
            {
                var rate = graph.Player.SampleRate > 0 ? graph.Player.SampleRate : 48_000;
                // D11 per-group outputs: attach the clip's audio to each output the group declares (the first
                // is the master/clock; the rest auto-slave with adaptive-rate). Each output's N→M channel
                // matrix (03 §6) comes from its matching route — it remaps the source channels + sets the
                // channel count; an output with no route is plain stereo.
                foreach (var outDef in ResolveGroupOutputs(groupId))
                {
                    var channelMap = ResolveOutputChannelMap(outDef.Id);
                    var channels = channelMap?.OutputChannels ?? 2;
                    var o = _audioBackend.CreateOutput(outDef.DeviceId ?? _outputDeviceId, new AudioFormat(rate, channels));
                    graph.Player.AttachAudioOutput(o, outDef.Id, map: channelMap);
                    outputs.Add(o);
                }
            }

            group.Replace(graph.Session, outputs, layer);
            graph.Player.Play();
        }
        catch
        {
            graph.Dispose();
            layer?.Dispose();
            throw;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Resolves the N→M channel map for <paramref name="outputId"/> from the show's routing scene
    /// (the first enabled <see cref="OutputPatchRoute"/> patched to it), or null for the source-derived default.</summary>
    private ChannelMap? ResolveOutputChannelMap(string outputId)
    {
        foreach (var route in _routes)
            if (route.Enabled && string.Equals(route.OutputId, outputId, StringComparison.Ordinal))
                return route.ToChannelMap();
        return null;
    }

    /// <summary>The audio outputs a group plays on: its declared <see cref="ShowAudioOutput"/>s, or a single
    /// implicit master (<see cref="MasterOutputId"/>) on the default device when the show declares none.</summary>
    private IReadOnlyList<ShowAudioOutput> ResolveGroupOutputs(string groupId)
    {
        var declared = _audioOutputs.Where(o => string.Equals(o.GroupId, groupId, StringComparison.Ordinal)).ToArray();
        return declared.Length > 0 ? declared : [new ShowAudioOutput(MasterOutputId, GroupId: groupId)];
    }

    // --- transport commands (marshaled — D5) -------------------------------------------------------

    /// <summary>Fires a specific cue by id (PreWait/PostWait/AutoContinue honoured by the cue graph).</summary>
    public Task<CueExecutionStatus> FireCueAsync(string cueId) =>
        InvokeAsync(() => _cueGraph.FireAsync(cueId).AsTask());

    /// <summary>GO — fires the next armed/enabled cue in <paramref name="groupId"/> after the last fired.</summary>
    public Task<CueExecutionStatus> GoAsync(string groupId = DefaultGroup) =>
        InvokeAsync(async () =>
        {
            var group = GetOrAddGroup(groupId);
            var next = _cueGraph.Cues
                .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > group.LastFiredNumber)
                .OrderBy(c => c.Number)
                .FirstOrDefault();
            if (next is null)
                return CueExecutionStatus.NotReady;

            group.LastFiredNumber = next.Number;
            return await _cueGraph.FireAsync(next.Id).ConfigureAwait(false);
        });

    /// <summary>Seeks the active clip on <paramref name="groupId"/> (coordinated A/V seek).</summary>
    public Task SeekAsync(TimeSpan position, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            GetOrAddGroup(groupId).Active?.Player.SeekCoordinated(position);
            return Task.CompletedTask;
        });

    /// <summary>Stops and releases the active clip (and its master output) on <paramref name="groupId"/>.</summary>
    public Task StopAsync(string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            GetOrAddGroup(groupId).Replace(null, [], null);
            return Task.CompletedTask;
        });

    // --- queries (immutable snapshots — D5) --------------------------------------------------------

    /// <summary>An immutable snapshot of each transport group's session time, clip position, and run state.</summary>
    public Task<IReadOnlyList<TransportSnapshot>> SnapshotAsync() =>
        InvokeAsync<IReadOnlyList<TransportSnapshot>>(() =>
        {
            var snaps = _groups
                .Select(kv => new TransportSnapshot(
                    kv.Key,
                    kv.Value.Clock.Now,
                    kv.Value.Active?.Player.Position ?? TimeSpan.Zero,
                    kv.Value.Active?.Player.IsRunning ?? false))
                .ToArray();
            return Task.FromResult<IReadOnlyList<TransportSnapshot>>(snaps);
        });

    /// <summary>An immutable snapshot of the loaded cue definitions, ordered by cue number.</summary>
    public Task<IReadOnlyList<CueDefinition>> GetCueDefinitionsAsync() =>
        InvokeAsync(() => Task.FromResult(_cueGraph.Cues));

    /// <summary>An immutable snapshot of the cue execution log.</summary>
    public Task<IReadOnlyList<CueExecutionLogEntry>> GetCueExecutionLogAsync() =>
        InvokeAsync(() => Task.FromResult(_cueGraph.ExecutionLog));

    /// <summary>A composition's pump stats (frames submitted to its layers + composited), or null when no
    /// composition with that id is loaded — proves the cue→clip→layer→composite path ran (headless).</summary>
    public Task<ClipCompositionRuntimeStats?> GetCompositionStatsAsync(string compositionId) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
                ? composition.GetStats()
                : (ClipCompositionRuntimeStats?)null));

    /// <summary>Applies (or clears, with <see langword="null"/>) a composition's output mapping at runtime —
    /// projector keystone / multi-panel tiling. Returns false when no composition with that id is loaded.</summary>
    public Task<bool> ApplyCompositionMappingAsync(string compositionId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
                return Task.FromResult(false);
            composition.UpdateCompositionMapping(mapping);
            return Task.FromResult(true);
        });

    private TransportGroup GetOrAddGroup(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            _groups[groupId] = group = new TransportGroup();
        return group;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_dispatcher.IsOnDispatcherThread)
        {
            DisposeState();
            _dispatcher.Dispose();
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(DisposeState).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void DisposeState()
    {
        foreach (var group in _groups.Values)
            group.Dispose();
        _groups.Clear();
        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
    }

    /// <summary>One transport group: its session clock (D4), active clip, its audio outputs (D11 — first is
    /// master, rest auto-slave), and the composition layer the active clip's video feeds (all released on
    /// clip replace).</summary>
    private sealed class TransportGroup : IDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
        public MediaSession? Active { get; private set; }
        private IReadOnlyList<IAudioOutput> _outputs = [];
        private ClipCompositionRuntime.LayerSlot? _layer;
        public int LastFiredNumber { get; set; } = int.MinValue;

        public void Replace(MediaSession? session, IReadOnlyList<IAudioOutput> outputs, ClipCompositionRuntime.LayerSlot? layer)
        {
            Clock.SetReference(session is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(session.Player.PlayClock));
            _layer?.Dispose();
            Active?.Dispose();
            foreach (var output in _outputs)
                (output as IDisposable)?.Dispose();
            Active = session;
            _outputs = outputs;
            _layer = layer;
        }

        public void Dispose() => Replace(null, [], null);
    }

    private sealed class PlayheadPlaybackClock(IPlayhead playhead) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playhead.CurrentPosition;
        public bool IsAdvancing => playhead.IsRunning;
    }
}
