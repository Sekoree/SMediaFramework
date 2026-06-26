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

    private readonly IMediaRegistry _registry;
    private readonly IAudioBackend? _audioBackend;
    private readonly string? _outputDeviceId;
    private readonly CueGraph _cueGraph = new();
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
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
            IAudioOutput? output = null;
            if (_audioBackend is not null && graph.Player.AudioRouter is not null)
            {
                var rate = graph.Player.SampleRate > 0 ? graph.Player.SampleRate : 48_000;
                output = _audioBackend.CreateOutput(_outputDeviceId, new AudioFormat(rate, 2));
                graph.Player.AttachAudioOutput(output, "_master");
            }

            group.Replace(graph.Session, output, layer);
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
            GetOrAddGroup(groupId).Replace(null, null);
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

    /// <summary>One transport group: its session clock (D4), active clip, master output (D11), and the
    /// composition layer the active clip's video feeds (removed when the clip is replaced).</summary>
    private sealed class TransportGroup : IDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
        public MediaSession? Active { get; private set; }
        private IAudioOutput? _output;
        private ClipCompositionRuntime.LayerSlot? _layer;
        public int LastFiredNumber { get; set; } = int.MinValue;

        public void Replace(MediaSession? session, IAudioOutput? output, ClipCompositionRuntime.LayerSlot? layer = null)
        {
            Clock.SetReference(session is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(session.Player.PlayClock));
            _layer?.Dispose();
            Active?.Dispose();
            (_output as IDisposable)?.Dispose();
            Active = session;
            _output = output;
            _layer = layer;
        }

        public void Dispose() => Replace(null, null);
    }

    private sealed class PlayheadPlaybackClock(IPlayhead playhead) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playhead.CurrentPosition;
        public bool IsAdvancing => playhead.IsRunning;
    }
}
