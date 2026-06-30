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
    TimeSpan ClipDuration,
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
    private readonly Func<string, int, int, int, IVideoOverlaySource?>? _subtitleFactory;
    // Opens + warms clips (seek-to-Start trim-in, standby pre-roll). Clips arm through here instead of a
    // direct MediaGraph build so the show can pre-roll upcoming cues (8b convergence). All access is on the
    // serial dispatcher; the engine is also internally thread-safe.
    private readonly ClipStandbyEngine _standby = new();
    // Cue id → its clip binding (built on load) so the standby pre-roll can look up upcoming cues' media.
    private IReadOnlyDictionary<string, ShowClipBinding> _clipsByCue =
        new Dictionary<string, ShowClipBinding>(StringComparer.Ordinal);
    // Preview playback (a loaded cue auditioned on a separate device, independent of the transport groups).
    private IArmedClip? _previewClip;
    private IReadOnlyList<IAudioOutput> _previewOutputs = [];
    private CancellationTokenSource? _previewCts;
    private volatile bool _disposed;

    /// <param name="audioBackend">Optional. When supplied, each transport group plays its active clip on a
    /// master output created on this backend (D11). Null runs the cue/transport mechanics with no device.</param>
    /// <param name="subtitleFactory">Optional host-wired factory (path + stream index + canvas width/height → overlay
    /// source). When set, a composition-bound clip's selected subtitles auto-attach as
    /// top layer. Keeps the session renderer-agnostic — see <c>S.Media.Subtitles.SubtitleSourceFactory.FromFile</c>.</param>
    public ShowSession(
        IMediaRegistry registry,
        IAudioBackend? audioBackend = null,
        Func<string, int, int, int, IVideoOverlaySource?>? subtitleFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audioBackend = audioBackend;
        _subtitleFactory = subtitleFactory;
        _standby.StandbyStatesChanged += states => PreparedCuesChanged?.Invoke(states);
        if (audioBackend is not null)
        {
            var devices = audioBackend.EnumerateOutputDevices();
            _outputDeviceId = (devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault())?.Id;
        }
    }

    /// <summary>The registry clips open through (frozen capabilities — D6).</summary>
    public IMediaRegistry Registry => _registry;

    /// <summary>Raised when the standby engine's prepared-clip set changes (the GUI's
    /// <c>PreparedCueStatesChanged</c> — a per-cue "ready" indicator). Forwards
    /// <see cref="ClipStandbyEngine"/>'s states; the UI handler marshals to the UI thread, as it does today.</summary>
    public event Action<IReadOnlyList<ClipPreparationStatus>>? PreparedCuesChanged;

    /// <summary>Raised (with the cue id) when a preview started by <see cref="PreviewCueAsync"/> ends on its
    /// own (the GUI's <c>PreviewEnded</c>). Not raised on an explicit <see cref="StopPreviewAsync"/>.</summary>
    public event Action<string>? PreviewEnded;

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
        return InvokeAsync(() => LoadDocumentCoreAsync(document));
    }

    private async Task LoadDocumentCoreAsync(ShowDocument document)
    {
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);
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

        _clipsByCue = document.Clips.ToDictionary(c => c.CueId, StringComparer.Ordinal);
        foreach (var cue in document.Cues.OrderBy(c => c.Number))
        {
            var groupId = cue.GroupId ?? DefaultGroup;
            var binding = _clipsByCue.GetValueOrDefault(cue.Id);
            _cueGraph.AddCue(cue, ct => PlayClipAsync(groupId, binding, ct));
        }

        _ = WarmUpcomingAsync(); // background pre-roll of the first cues so the first GO arms instantly
    }

    private async ValueTask PlayClipAsync(string groupId, ShowClipBinding? binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding is null)
            return; // a control/stop cue with no media of its own

        var group = GetOrAddGroup(groupId);

        // Mint the composition layer (if any). The clip is armed through the standby engine WITHOUT a video
        // lead: its source declares its emit format up front (the HW-decode branch advertises Bgra32), so it
        // negotiates at open and the layer is attached post-arm. Arming lead-free is what lets a prepared
        // (warm) clip be reused across fires — the layer is per-fire. The free-running composition pump then
        // composites the fed frames (CPU headless), selecting the latest frame (no clock master → no rebasing).
        ClipCompositionRuntime.LayerSlot? layer = null;
        if (binding.CompositionId is { } compositionId &&
            _compositions.TryGetValue(compositionId, out var composition))
        {
            layer = composition.AddLayer(
                composition.CanvasFormat,
                BuildVideoPlacementSpec(compositionId, binding.LayerIndex, binding.Placement));
        }

        // Arm through the standby engine: it opens via the registry (which auto-wires adaptive-rate drift
        // correction) and seeks to the trim-in (Window.Start), reusing a warm prepared clip when present
        // (matched by ClipSpec.Key = (cueId, mediaPath) — the same spec the pre-roll prepared).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(BuildClipSpec(binding), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            layer?.Dispose();
            throw;
        }

        var player = armed.Player;
        var outputs = new List<IAudioOutput>();
        var subtitleAttachments = new List<IDisposable>();
        var fadeIn = binding.FadeIn > TimeSpan.Zero;
        var fadingOutputIds = fadeIn ? new List<string>() : null;
        try
        {
            if (layer is not null)
                player.AttachVideoOutput(layer.Output); // the clip's video feeds the composition layer

            if (_audioBackend is not null && player.AudioRouter is not null)
            {
                var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                if (binding.AudioRoutes is { Count: > 0 } clipRoutes)
                {
                    // Per-clip routing (GUI per-cue audio): the clip plays on exactly its routed outputs/devices,
                    // each with its own N→M channel map + static gain. The first route is the master/clock; the
                    // rest auto-slave. (A fade-in ramps every route 0→1, so per-route gain applies only without
                    // a fade — the common GUI case for a fader; fade+fader is a later refinement.)
                    for (var i = 0; i < clipRoutes.Count; i++)
                    {
                        var route = clipRoutes[i];
                        var channelMap = route.ToChannelMap();
                        var channels = channelMap?.OutputChannels ?? 2;
                        var outputId = $"clip{i}";
                        var o = _audioBackend.CreateOutput(route.DeviceId ?? _outputDeviceId, new AudioFormat(rate, channels));
                        player.AttachAudioOutput(o, outputId, map: channelMap, gain: fadeIn ? 0f : route.Gain);
                        outputs.Add(o);
                        fadingOutputIds?.Add(outputId);
                    }
                }
                else
                {
                    // D11 per-group outputs: attach the clip's audio to each output the group declares (the first
                    // is the master/clock; the rest auto-slave with adaptive-rate). Each output's N→M channel
                    // matrix (03 §6) comes from the matching source→output route — it remaps the source channels +
                    // sets the channel count; an output with no route for this clip is plain stereo.
                    foreach (var outDef in ResolveGroupOutputs(groupId))
                    {
                        var channelMap = ResolveOutputChannelMap(binding, outDef.Id);
                        var channels = channelMap?.OutputChannels ?? 2;
                        var o = _audioBackend.CreateOutput(outDef.DeviceId ?? _outputDeviceId, new AudioFormat(rate, channels));
                        // Fade-in: attach silent (gain 0) and ramp the route gain up over FadeIn after Start.
                        player.AttachAudioOutput(o, outDef.Id, map: channelMap, gain: fadeIn ? 0f : 1f);
                        outputs.Add(o);
                        fadingOutputIds?.Add(outDef.Id);
                    }
                }
            }

            if (layer is not null
                && binding.CompositionId is { } subtitleCompositionId
                && _compositions.TryGetValue(subtitleCompositionId, out var subtitleComposition)
                && _subtitleFactory is { } subtitleFactory)
            {
                var selections = binding.GetSubtitleSelections();
                var nextLayerIndex = int.MaxValue - selections.Count;
                foreach (var selection in selections)
                {
                    var path = string.IsNullOrWhiteSpace(selection.Path) ? binding.MediaPath : selection.Path!;
                    var overlay = subtitleFactory(
                        path, selection.StreamIndex,
                        subtitleComposition.CanvasFormat.Width, subtitleComposition.CanvasFormat.Height);
                    if (overlay is not null)
                    {
                        subtitleAttachments.Add(subtitleComposition.AttachSubtitleOverlay(
                            overlay, () => player.Position, nextLayerIndex++));
                    }
                }
            }

            armed.Start();
            await group.ReplaceAsync(armed, outputs, layer, subtitleAttachments).ConfigureAwait(false);

            // Background per-clip work — the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor —
            // shares one cancellation, cancelled when the clip is replaced. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            var end = player.Duration - binding.EndOffset;
            var endHandling = (binding.Loop || binding.EndBehavior != ClipEndBehavior.Stop || binding.EndOffset > TimeSpan.Zero)
                && player.Duration > TimeSpan.Zero
                && end > binding.StartOffset;
            if (fadeIn || endHandling)
            {
                var clipCts = new CancellationTokenSource();
                group.SetClipWorkCts(clipCts);
                if (fadeIn && fadingOutputIds is { Count: > 0 })
                    StartFadeIn(groupId, player, fadingOutputIds, binding.FadeIn, clipCts.Token);
                if (endHandling)
                    StartEndMonitor(groupId, binding, player, end, clipCts.Token);
            }
        }
        catch
        {
            foreach (var attachment in subtitleAttachments)
                attachment.Dispose();
            foreach (var output in outputs)
                (output as IDisposable)?.Dispose();
            await armed.ReleaseAsync().ConfigureAwait(false);
            layer?.Dispose();
            throw;
        }
    }

    /// <summary>Builds the standby <see cref="ClipSpec"/> for a clip binding — identical on the pre-roll
    /// (prepare) and fire (arm) paths, so a warmed clip is found by its key (cue id + media path).</summary>
    private ClipSpec BuildClipSpec(ShowClipBinding binding, string? variant = null)
    {
        var options = binding.AudioStreamIndex is { } audioTrack
            ? S.Media.Players.MediaPlayerOpenOptions.Default with { AudioStreamIndex = audioTrack } // multi-track select (03 §6)
            : S.Media.Players.MediaPlayerOpenOptions.Default;
        var window = binding.StartOffset > TimeSpan.Zero
            ? new S.Media.Core.ClipWindow(binding.StartOffset, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false)
            : S.Media.Core.ClipWindow.Unbounded;
        // A non-null variant (e.g. "preview") gives a distinct standby key so this arms a FRESH instance
        // instead of consuming GO's prepared clip.
        return new ClipSpec(
            variant is null ? binding.CueId : $"{binding.CueId}:{variant}",
            ClipMediaSource.File(_registry, binding.MediaPath, options),
            window,
            cacheKey: variant is null ? binding.MediaPath : $"{binding.MediaPath}#{variant}");
    }

    private static VideoPlacementSpec BuildVideoPlacementSpec(string compositionId, int layerIndex, ShowVideoPlacement? p) =>
        p is null
            ? new VideoPlacementSpec(compositionId, layerIndex, DestWidth: 1, DestHeight: 1)
            : new VideoPlacementSpec(
                compositionId, layerIndex,
                Opacity: p.Opacity, Placement: p.Fit,
                DestX: p.DestX, DestY: p.DestY, DestWidth: p.DestWidth, DestHeight: p.DestHeight,
                RotationDegrees: p.RotationDegrees);

    /// <summary>Live-edit the active cue's composition placement while it plays (the GUI's
    /// <c>UpdateActiveCueVideoPlacement</c>) — repositions / re-opacities its layer. Returns false when the
    /// cue isn't the active clip on any group (or has no composition layer).</summary>
    public Task<bool> UpdateActivePlacementAsync(string cueId, string compositionId, int layerIndex, ShowVideoPlacement placement) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active?.Spec.Id == cueId)
                    return Task.FromResult(group.UpdateActivePlacement(
                        BuildVideoPlacementSpec(compositionId, layerIndex, placement)));
            return Task.FromResult(false);
        });

    /// <summary>Live-edit the active cue's audio routing matrix (source channels → <paramref name="outputId"/>'s
    /// channels) while it plays (the GUI's <c>UpdateActiveCueAudioRoutes</c>). Returns false when the cue isn't
    /// the active clip on any group (or has no audio router). Applies on the clip's source→output route.</summary>
    public Task<bool> ApplyActiveAudioMatrixAsync(string cueId, string outputId, float[,] gains) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active is { } active && active.Spec.Id == cueId
                    && active.Player.AudioRouter is { } router
                    && active.Player.AudioSourceId is { } sourceId)
                {
                    router.ApplyMatrix(sourceId, outputId, gains);
                    return Task.FromResult(true);
                }

            return Task.FromResult(false);
        });

    /// <summary>Previews a loaded cue's clip on a separate (preview / headphones) device, independent of the
    /// transport groups (the GUI's <c>PreviewCue</c>). Opens a FRESH instance (not the standby-prepared clip),
    /// plays it on <paramref name="previewDeviceId"/> (or the default device), and fires
    /// <see cref="PreviewEnded"/> at its natural end. Replaces any current preview. Returns false when the cue
    /// has no clip binding.</summary>
    public Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId = null) =>
        InvokeAsync(async () =>
        {
            await ReleasePreviewAsync().ConfigureAwait(false);
            if (!_clipsByCue.TryGetValue(cueId, out var binding))
                return false;

            var armed = await _standby.ArmAsync(BuildClipSpec(binding, "preview")).ConfigureAwait(false);
            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(previewDeviceId ?? _outputDeviceId, new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, "_preview");
                    outputs.Add(output);
                }

                armed.Start();
                _previewClip = armed;
                _previewOutputs = outputs;
                _previewCts = new CancellationTokenSource();
                StartPreviewEndMonitor(cueId, player, _previewCts.Token);
                return true;
            }
            catch
            {
                foreach (var output in outputs)
                    (output as IDisposable)?.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        });

    /// <summary>Stops the current preview, if any (the GUI's <c>StopPreview</c>). Does not raise
    /// <see cref="PreviewEnded"/>.</summary>
    public Task StopPreviewAsync() => InvokeAsync(() => ReleasePreviewAsync().AsTask());

    private async ValueTask ReleasePreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        var clip = _previewClip;
        var outputs = _previewOutputs;
        _previewClip = null;
        _previewOutputs = [];
        if (clip is not null)
            await clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var output in outputs)
            (output as IDisposable)?.Dispose();
    }

    /// <summary>Watches the preview clip; when it ends on its own (ran, then stopped) it releases it and raises
    /// <see cref="PreviewEnded"/>. Cancelled by <see cref="ReleasePreviewAsync"/> (an explicit stop / replace),
    /// which exits without raising the event. Marshals each check onto the dispatcher (see StartEndMonitor).</summary>
    private void StartPreviewEndMonitor(string cueId, S.Media.Players.MediaPlayer player, CancellationToken ct)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(EndMonitorPollInterval, ct).ConfigureAwait(false);
                            var ended = await InvokeAsync<bool>(() =>
                                Task.FromResult(
                                    !ReferenceEquals(_previewClip?.Player, player)          // replaced/stopped → exit
                                    || (!player.IsRunning && player.Position > TimeSpan.Zero))) // ran, then ended
                                .ConfigureAwait(false);
                            if (!ended)
                                continue;
                            await InvokeAsync(async () =>
                            {
                                if (ReferenceEquals(_previewClip?.Player, player)) // still ours ⇒ natural end
                                {
                                    await ReleasePreviewAsync().ConfigureAwait(false);
                                    PreviewEnded?.Invoke(cueId);
                                }
                            }).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // best-effort — a preview-monitor hiccup must never crash the session
                    }
                },
                ct);
        }
    }

    /// <summary>The clip specs for the next <paramref name="count"/> clip-bound cues after the last fired in
    /// <paramref name="groupId"/>. Reads cue/clip state, so call on the dispatcher.</summary>
    private List<ClipSpec> BuildUpcomingSpecs(string groupId, int count)
    {
        var group = GetOrAddGroup(groupId);
        var specs = new List<ClipSpec>();
        foreach (var cue in _cueGraph.Cues
                     .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > group.LastFiredNumber)
                     .OrderBy(c => c.Number)
                     .Take(count))
        {
            if (_clipsByCue.TryGetValue(cue.Id, out var binding))
                specs.Add(BuildClipSpec(binding));
        }

        return specs;
    }

    /// <summary>Pre-warms (opens + seeks-to-Start, holds ready) the next <paramref name="count"/> cues after
    /// the last fired in <paramref name="groupId"/> so the next GO arms instantly. Best-effort — a warm
    /// failure is swallowed and never affects transport. Awaitable for the UI/tests; <see cref="GoAsync"/>
    /// fires it without awaiting so the opens run in the background while the current cue plays.</summary>
    public Task WarmUpcomingAsync(string groupId = DefaultGroup, int count = 2) =>
        InvokeAsync(async () =>
        {
            try
            {
                var specs = BuildUpcomingSpecs(groupId, count);
                if (specs.Count > 0)
                    await _standby.RefreshStandbyAsync(
                            specs,
                            new ClipStandbyPolicy(MaxPreparedDecoders: count, Window: count))
                        .ConfigureAwait(false);
            }
            catch
            {
                // best-effort pre-roll — a failed warm just means the next GO opens on demand
            }
        });

    private static readonly TimeSpan FadeStepInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Ramps the clip's audio route gain 0→1 over <paramref name="duration"/> on each output (the clip
    /// was attached silent). A background poll that marshals each gain step onto the dispatcher; cancelled when
    /// the clip is replaced. Best-effort — a hiccup just leaves the clip at its current gain.</summary>
    private void StartFadeIn(string groupId, S.Media.Players.MediaPlayer player, IReadOnlyList<string> outputIds, TimeSpan duration, CancellationToken ct)
    {
        if (player.AudioSourceId is not { } sourceId)
            return;

        // Suppress ExecutionContext flow (see StartEndMonitor) so InvokeAsync marshals onto the dispatcher.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var elapsed = sw.Elapsed;
                            var level = elapsed >= duration
                                ? 1f
                                : (float)Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
                            var done = await InvokeAsync<bool>(() =>
                            {
                                if (ct.IsCancellationRequested ||
                                    _groups.GetValueOrDefault(groupId)?.Active?.Player != player ||
                                    player.AudioRouter is not { } router)
                                    return Task.FromResult(true);
                                foreach (var outId in outputIds)
                                    router.SetRouteGain(sourceId, outId, level);
                                return Task.FromResult(level >= 1f);
                            }).ConfigureAwait(false);
                            if (done)
                                return;
                            await Task.Delay(FadeStepInterval, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // best-effort — a fade hiccup must never crash the session
                    }
                },
                ct);
        }
    }

    private static readonly TimeSpan EndMonitorPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EndMonitorGuard = TimeSpan.FromMilliseconds(120);

    /// <summary>Watches the active clip's position and applies its end-of-clip behaviour at <paramref name="end"/>
    /// (the trimmed out-point, or the natural duration): <see cref="ClipEndBehavior.Loop"/> → seek back to the
    /// in-point; <see cref="ClipEndBehavior.FreezeLastFrame"/> → pause on the last frame; otherwise stop. A
    /// background poll that marshals each check onto the dispatcher (cancelled when the clip is replaced).
    /// Position-based, not <c>IsRunning</c> — a paused clip's position is frozen below <paramref name="end"/>,
    /// so pause is never mistaken for end.</summary>
    private void StartEndMonitor(string groupId, ShowClipBinding binding, S.Media.Players.MediaPlayer player, TimeSpan end, CancellationToken ct)
    {
        var loops = binding.Loop || binding.EndBehavior == ClipEndBehavior.Loop;
        var freezes = binding.EndBehavior == ClipEndBehavior.FreezeLastFrame;
        var start = binding.StartOffset;

        // Suppress ExecutionContext flow so the dispatcher's AsyncLocal identity does NOT leak into the monitor
        // thread; otherwise InvokeAsync would run the checks inline (off-dispatcher) and race transport commands.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(EndMonitorPollInterval, ct).ConfigureAwait(false);
                            var done = await InvokeAsync<bool>(async () =>
                            {
                                if (ct.IsCancellationRequested ||
                                    _groups.GetValueOrDefault(groupId)?.Active?.Player != player)
                                    return true; // clip replaced/gone → stop monitoring
                                if (player.Position < end - EndMonitorGuard)
                                    return false; // not at the out-point yet (frozen here if paused)
                                if (loops)
                                {
                                    player.SeekCoordinated(start);
                                    if (!player.IsRunning)
                                        player.Play();
                                    return false; // keep looping
                                }
                                if (freezes)
                                {
                                    player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                                    return true; // held on the last frame
                                }
                                // Stop / FadeOutAndStop (fade ramp deferred): release the clip at the out-point.
                                await GetOrAddGroup(groupId).ReplaceAsync(null, [], null).ConfigureAwait(false);
                                return true;
                            }).ConfigureAwait(false);
                            if (done)
                                return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // best-effort — an end-monitor hiccup must never crash the session
                    }
                },
                ct);
        }
    }

    /// <summary>Resolves the N→M channel map for this clip's source→output route, or null for the source-derived default.</summary>
    private ChannelMap? ResolveOutputChannelMap(ShowClipBinding binding, string outputId)
    {
        var sourceId = binding.CueId;
        foreach (var route in _routes)
            if (route.Enabled
                && string.Equals(route.SourceId, sourceId, StringComparison.Ordinal)
                && string.Equals(route.OutputId, outputId, StringComparison.Ordinal))
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
            var status = await _cueGraph.FireAsync(next.Id).ConfigureAwait(false);
            _ = WarmUpcomingAsync(groupId); // pre-roll the next cue(s) in the background so the next GO is instant
            return status;
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
        InvokeAsync(() => GetOrAddGroup(groupId).ReplaceAsync(null, [], null).AsTask());

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is the active clip (per-cue stop /
    /// cancel — the GUI's <c>CancelCueCallback</c>). No-op when that cue isn't currently playing.</summary>
    public Task StopCueAsync(string cueId) =>
        InvokeAsync(async () =>
        {
            foreach (var group in _groups.Values)
                if (group.Active?.Spec.Id == cueId)
                    await group.ReplaceAsync(null, [], null).ConfigureAwait(false);
        });

    /// <summary>Pauses or resumes the active clip on <paramref name="groupId"/> — a seamless toggle (codec
    /// pipelines are not flushed, so resume continues from the same frame, matching the GUI engine's
    /// <c>SkipFlush</c> pause). On pause the player's playhead — and therefore the group's session clock +
    /// transport-snapshot position — freezes; resume continues from there (the playback-clock freeze
    /// contract). No-op when the group has no active clip.</summary>
    public Task SetPausedAsync(bool paused, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            if (GetOrAddGroup(groupId).Active is { } active)
            {
                if (paused)
                    active.Player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                else
                    active.Player.Play();
            }

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
                    kv.Value.Active?.Player.Duration ?? TimeSpan.Zero,
                    kv.Value.Active?.Player.IsRunning ?? false))
                .ToArray();
            return Task.FromResult<IReadOnlyList<TransportSnapshot>>(snaps);
        });

    /// <summary>An immutable snapshot of the loaded cue definitions, ordered by cue number.</summary>
    public Task<IReadOnlyList<CueDefinition>> GetCueDefinitionsAsync() =>
        InvokeAsync(() => Task.FromResult(_cueGraph.Cues));

    /// <summary>The cue ids whose clips are currently prepared (warm) in the standby engine — a UI "ready"
    /// indicator, and how a test confirms the pre-roll ran.</summary>
    public Task<IReadOnlyList<string>> GetPreparedCueIdsAsync() =>
        InvokeAsync<IReadOnlyList<string>>(() =>
            Task.FromResult<IReadOnlyList<string>>(_standby.PreparedKeys.Select(k => k.Id).ToArray()));

    /// <summary>
    /// Attach a live <see cref="IVideoOutput"/> (e.g. a UI preview surface) to a loaded composition's pump — the
    /// composited canvas starts flowing to it on the next pump tick. Returns false if no composition has that id.
    /// The caller owns the output's lifetime; it is not disposed with the runtime.
    /// </summary>
    public Task<bool> AttachCompositionOutputAsync(string compositionId, IVideoOutput output, string outputId = "preview") =>
        InvokeAsync(() =>
            _compositions.TryGetValue(compositionId, out var composition)
                ? Task.FromResult(composition.AddOutput(new ClipCompositionOutputLease(outputId, outputId, output)))
                : Task.FromResult(false));

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
            await DisposeStateAsync().ConfigureAwait(false);
            _dispatcher.Dispose();
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(DisposeStateAsync).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeStateAsync()
    {
        await ReleasePreviewAsync().ConfigureAwait(false);
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);
        _groups.Clear();
        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
        await _standby.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One transport group: its session clock (D4), active clip, its audio outputs (D11 — first is
    /// master, rest auto-slave), and the composition layer the active clip's video feeds (all released on
    /// clip replace).</summary>
    private sealed class TransportGroup : IAsyncDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
        public IArmedClip? Active { get; private set; }
        private IReadOnlyList<IAudioOutput> _outputs = [];
        private IReadOnlyList<IDisposable> _subtitleAttachments = [];
        private ClipCompositionRuntime.LayerSlot? _layer;
        private CancellationTokenSource? _clipWorkCts;
        public int LastFiredNumber { get; set; } = int.MinValue;

        /// <summary>Hands the group the cancellation source for the active clip's background work (the fade-in
        /// ramp + the end-of-clip loop/stop/freeze monitor). Cancelled when the clip is replaced.</summary>
        public void SetClipWorkCts(CancellationTokenSource cts) => _clipWorkCts = cts;

        /// <summary>Live-repositions the active clip's composition layer; false if it has no layer.</summary>
        public bool UpdateActivePlacement(VideoPlacementSpec spec)
        {
            if (_layer is null)
                return false;
            _layer.UpdatePlacement(spec);
            return true;
        }

        public async ValueTask ReplaceAsync(
            IArmedClip? clip,
            IReadOnlyList<IAudioOutput> outputs,
            ClipCompositionRuntime.LayerSlot? layer,
            IReadOnlyList<IDisposable>? subtitleAttachments = null)
        {
            // Stop the displaced clip's background work (fade ramp + end-of-clip monitor) before anything else.
            _clipWorkCts?.Cancel();
            _clipWorkCts?.Dispose();
            _clipWorkCts = null;

            Clock.SetReference(clip is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(clip.Player.PlayClock));

            var oldActive = Active;
            var oldOutputs = _outputs;
            var oldLayer = _layer;
            var oldSubtitles = _subtitleAttachments;

            Active = clip;
            _outputs = outputs;
            _layer = layer;
            _subtitleAttachments = subtitleAttachments ?? [];

            // Release the displaced clip BEFORE its outputs (the player feeds them). Runs on the serial
            // dispatcher, so the brief Active=new / old-not-yet-released window is never observed.
            foreach (var attachment in oldSubtitles)
                attachment.Dispose();
            oldLayer?.Dispose();
            if (oldActive is not null)
                await oldActive.ReleaseAsync().ConfigureAwait(false);
            foreach (var output in oldOutputs)
                (output as IDisposable)?.Dispose();
        }

        public ValueTask DisposeAsync() => ReplaceAsync(null, [], null);
    }

    private sealed class PlayheadPlaybackClock(IPlayhead playhead) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playhead.CurrentPosition;
        public bool IsAdvancing => playhead.IsRunning;
    }
}
