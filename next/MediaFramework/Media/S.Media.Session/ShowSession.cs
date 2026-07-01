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

    private static readonly TimeSpan SoftStopFadeDuration = TimeSpan.FromMilliseconds(750);

    private readonly IMediaRegistry _registry;
    private readonly IAudioBackend? _audioBackend;
    private readonly string? _outputDeviceId;
    private CueGraph _cueGraph = new(); // swapped atomically on load (NXT-12 transactional load)
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputPatchRoute> _routes = [];
    private IReadOnlyList<ShowAudioOutput> _audioOutputs = [];
    private readonly SessionDispatcher _dispatcher = new("show-session");
    private readonly Func<string, int, int, int, IVideoOverlaySource?>? _subtitleFactory;
    // Host video-output factory (compositionId, name, width, height) → the leases a composition renders to
    // (NDI/SDL/local). The HOST owns each returned output's lifetime and declares it on the lease — a borrowed
    // host output is returned with DisposeOutputOnRuntimeDispose=false (+ an optional release callback), so the
    // session NEVER disposes it (NXT-01: disposing a borrowed SDL/NDI output is a use-after-reload defect).
    // Null ⇒ headless discard. Lets the GUI surface composited video to its output lines.
    private readonly Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? _videoOutputFactory;
    // Host compositor factory (canvas format → compositor). Null ⇒ the default CPU compositor; a host that has a
    // GL context can inject a GPU/warp compositor so session compositions use the intended zero-copy GPU path
    // instead of building full-BGRA CPU canvases (NXT-11). Threaded into every ClipCompositionRuntime at load.
    private readonly Func<VideoFormat, ClipCompositionCompositor>? _compositorFactory;
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
    // Soundboard voices (task #10): polyphonic one-shots, each a fresh MediaPlayer on an output, keyed by a
    // host id (the GUI's soundboard tile). Independent of the transport groups — the keyed generalization of
    // the single preview above. Owned by the dispatcher.
    private readonly Dictionary<string, VoiceHandle> _voices = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    // Lock-free query view (NXT-16): a volatile snapshot of each group's clock + active player, republished on
    // the dispatcher whenever the group set or active clip changes. Snapshot() reads THIS and pulls live
    // (thread-safe) position/duration/run-state off the captured references, so a position/state poll never
    // serializes behind a long-running command on the dispatcher.
    private volatile IReadOnlyList<GroupClockView> _groupViews = [];

    private sealed record GroupClockView(string GroupId, SessionClock Clock, S.Media.Players.MediaPlayer? Player);

    // The cancellation source of the in-flight cue fire (its pre/post-wait + open + auto-continue chain). Set
    // while a fire runs; read off-dispatcher by CancelActiveFire so STOP/LOAD/DISPOSE can abort it (NXT-03).
    private volatile CancellationTokenSource? _activeFireCts;

    // Off-dispatcher fire model (NXT-03): a cue fire runs OFF the serial dispatcher (its pre-wait + media open
    // no longer park the loop, so STOP/seek/load/queries stay responsive), re-entering only for short state
    // commits. _fireLock serializes fires (the app drives GO serially); _showGeneration is bumped on every load
    // so a fire whose open straddled a reload discards its (now-stale) clip at commit instead of corrupting the
    // newer show.
    private readonly SemaphoreSlim _fireLock = new(1, 1);
    private volatile int _showGeneration;

    /// <param name="audioBackend">Optional. When supplied, each transport group plays its active clip on a
    /// master output created on this backend (D11). Null runs the cue/transport mechanics with no device.</param>
    /// <param name="subtitleFactory">Optional host-wired factory (path + stream index + canvas width/height → overlay
    /// source). When set, a composition-bound clip's selected subtitles auto-attach as
    /// top layer. Keeps the session renderer-agnostic — see <c>S.Media.Subtitles.SubtitleSourceFactory.FromFile</c>.</param>
    public ShowSession(
        IMediaRegistry registry,
        IAudioBackend? audioBackend = null,
        Func<string, int, int, int, IVideoOverlaySource?>? subtitleFactory = null,
        Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? videoOutputFactory = null,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audioBackend = audioBackend;
        _subtitleFactory = subtitleFactory;
        _videoOutputFactory = videoOutputFactory;
        _compositorFactory = compositorFactory;
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

    /// <summary>Raised (with the voice id) when a soundboard voice started by <see cref="FireVoiceAsync"/> ends
    /// on its own. Not raised on an explicit <see cref="StopVoiceAsync"/> / <see cref="FadeVoiceAsync"/>.</summary>
    public event Action<string>? VoiceEnded;

    private sealed record VoiceHandle(
        IArmedClip Clip, IReadOnlyList<IAudioOutput> Outputs, string OutputId, CancellationTokenSource Cts);

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
        CancelActiveFire(); // a reload must not wait behind a long in-flight fire (NXT-03)
        return InvokeAsync(() => LoadDocumentCoreAsync(document));
    }

    private async Task LoadDocumentCoreAsync(ShowDocument document)
    {
        // Validate BEFORE any teardown — a malformed document (bad version, duplicate ids/numbers, dangling
        // references, a cyclic auto-continue chain) must never destroy the running show (NXT-12 / NXT-07).
        ShowDocumentValidator.ThrowIfInvalid(document);

        // Stage the replacement graph in locals. If a composition fails to construct (or the host video factory
        // throws), dispose only the partially-built NEW compositions and rethrow — the live show is untouched.
        var newCompositions = new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
        try
        {
            foreach (var comp in document.Compositions)
            {
                var definition = new ClipCompositionDefinition(
                    comp.Id, comp.Name, comp.Width, comp.Height, comp.FrameRateNum, comp.FrameRateDen);
                // Host-provided leases (the GUI's NDI/SDL/local lines for this composition). The host owns each
                // output's lifetime and declares dispose/release on its lease — the session must NOT dispose a
                // borrowed host output (NXT-01). When the factory is absent or returns none, a session-owned
                // discarding lease keeps the CPU pump composing headless.
                var hostLeases = _videoOutputFactory?.Invoke(comp.Id, comp.Name, comp.Width, comp.Height);
                var leases = hostLeases is { Count: > 0 }
                    ? hostLeases
                    : [new ClipCompositionOutputLease(
                        $"{comp.Id}_out", comp.Name, new DiscardingVideoOutput(), DisposeOutputOnRuntimeDispose: true)];
                newCompositions[comp.Id] = new ClipCompositionRuntime(
                    definition, leases, compositorFactory: _compositorFactory, compositionMapping: comp.OutputMapping);
            }
        }
        catch
        {
            foreach (var built in newCompositions.Values)
                built.Dispose();
            throw; // running show left intact — fields are not mutated until the commit below
        }

        var newClipsByCue = document.Clips.ToDictionary(c => c.CueId, StringComparer.Ordinal);
        var newCueGraph = new CueGraph();
        foreach (var cue in document.Cues.OrderBy(c => c.Number))
        {
            var groupId = cue.GroupId ?? DefaultGroup;
            var binding = newClipsByCue.GetValueOrDefault(cue.Id);
            // A cue without a clip currently has no executable session action. Do not report it as Fired: that
            // produced a successful no-op and made HaPlay briefly mark a stale/unbound media cue as playing.
            // Future control/stop cues need their own action binding rather than relying on an empty clip.
            newCueGraph.AddCue(
                cue,
                ct => PlayClipAsync(groupId, binding, ct),
                binding is null ? static () => false : null);
        }

        // Commit (atomic on the dispatcher): retire the running show, then swap in the staged graph. Nothing
        // below can fail, so the swap can't leave a half-built replacement.
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);
        _groups.Clear();
        PublishGroupViews();

        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
        foreach (var (id, runtime) in newCompositions)
            _compositions[id] = runtime;

        _cueGraph = newCueGraph;
        _clipsByCue = newClipsByCue;
        _routes = document.Routes;
        _audioOutputs = document.AudioOutputs;
        _showGeneration++; // a fire whose open straddled this reload bails at commit (NXT-03 off-dispatcher)

        _ = WarmUpcomingAsync(); // background pre-roll of the first cues so the first GO arms instantly
    }

    private async ValueTask PlayClipAsync(string groupId, ShowClipBinding? binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding is null)
            return; // a control/stop cue with no media of its own

        // --- SETUP (on the dispatcher): capture the show generation, group and composition. The layer is
        // intentionally NOT created yet: its placement transform needs the media's actual negotiated source
        // dimensions, which are unknown until ArmAsync opens the clip below.
        var setup = await InvokeAsync(() =>
        {
            var generation = _showGeneration;
            var grp = GetOrAddGroup(groupId);
            ClipCompositionRuntime? comp = null;
            if (binding.CompositionId is { } compositionId && _compositions.TryGetValue(compositionId, out var found))
                comp = found;
            return Task.FromResult((generation, group: grp, composition: comp));
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): arm the clip through the standby engine — it opens via the registry
        // (auto-wiring adaptive-rate drift correction) and seeks to the trim-in (Window.Start), reusing a warm
        // prepared clip when present. The long part; the dispatcher loop stays free throughout (NXT-03).
        var armed = await _standby.ArmAsync(BuildClipSpec(binding), cancellationToken).ConfigureAwait(false);

        // --- COMMIT (on the dispatcher): swap the armed clip in, or discard it if the show was reloaded / the
        // fire was cancelled (STOP) during the open (NXT-03).
        await InvokeAsync(() => CommitClipAsync(groupId, binding, cancellationToken, setup, armed)).ConfigureAwait(false);
    }

    /// <summary>The on-dispatcher commit half of <see cref="PlayClipAsync"/> (NXT-03): attaches the freshly-armed
    /// clip's outputs, masters the composition, and swaps it in — unless the show was reloaded (generation moved)
    /// or the fire was cancelled while the clip opened off the dispatcher, in which case it discards the now-stale
    /// clip without touching the live show.</summary>
    private async Task CommitClipAsync(
        string groupId,
        ShowClipBinding binding,
        CancellationToken cancellationToken,
        (int generation, TransportGroup group, ClipCompositionRuntime? composition) setup,
        IArmedClip armed)
    {
        if (cancellationToken.IsCancellationRequested || _showGeneration != setup.generation || _disposed)
        {
            await armed.ReleaseAsync().ConfigureAwait(false);
            return;
        }

        var group = setup.group;
        var composition = setup.composition;
        var player = armed.Player;
        ClipCompositionRuntime.LayerSlot? layer = null;
        var outputs = new List<IAudioOutput>();
        var subtitleAttachments = new List<IDisposable>();
        var fadeIn = binding.FadeIn > TimeSpan.Zero;
        // Retained for the active clip so both fade-in and every stop path ramp each route relative to its
        // configured gain rather than assuming unity.
        var routeTargets = new List<(string OutputId, float TargetGain)>();
        try
        {
            if (composition is not null
                && binding.CompositionId is { } compositionId
                && player.VideoSource is { } videoSource)
            {
                // PlacementResolver scales source pixels into the normalized destination rectangle. Passing the
                // canvas format here (the old pre-open placeholder) made a 1280x720 clip on a 1920x1080 canvas
                // use an identity/full-HD transform, leaving the real frame undersized and offset.
                layer = composition.AddLayer(
                    videoSource.Format,
                    BuildVideoPlacementSpec(compositionId, binding.LayerIndex, binding.Placement));
            }

            if (layer is not null)
            {
                player.AttachVideoOutput(layer.Output); // the clip's video feeds the composition layer
                // NXT-04: clock-master the composition pump to this transport group, so it composites at the
                // group clock's cadence and selects frames against the clip's playhead instead of free-running
                // (showing the latest frame). The master is the GROUP's SessionClock — stable across cues (it
                // re-references each active clip and survives replacement), so SetClockMaster's once-only master
                // stays valid as cues change while the per-clip timeline (the playhead) updates each fire.
                // ShowSession feeds the layer raw source frames (no retiming), so the playhead and frame PTS
                // share the source timebase — no trim offset needed. Live sources keep the free-run "latest
                // frame" path: their A/V correlation is the separate live-sync work (NXT-04 live half).
                if (!player.IsLive)
                    composition!.SetClockMaster(new SessionClockMaster(group.Clock), player.PlayClock);
            }

            if (_audioBackend is not null && player.AudioRouter is not null)
            {
                var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                if (binding.AudioRoutes is { } clipRoutes)
                {
                    // Per-clip routing (GUI per-cue audio): the clip plays on exactly its routed outputs/devices,
                    // each with its own N→M channel map + static gain. An explicitly empty collection means silent;
                    // only null inherits the show's group/default routing. The first route is the master/clock;
                    // the rest auto-slave. With a fade-in the route attaches silent and ramps up to its target.
                    for (var i = 0; i < clipRoutes.Count; i++)
                    {
                        var route = clipRoutes[i];
                        var channelMap = route.ToChannelMap();
                        var channels = channelMap?.OutputChannels ?? 2;
                        var outputId = $"clip{i}";
                        var o = _audioBackend.CreateOutput(route.DeviceId ?? _outputDeviceId, new AudioFormat(rate, channels));
                        player.AttachAudioOutput(o, outputId, map: channelMap, gain: fadeIn ? 0f : route.Gain);
                        outputs.Add(o);
                        routeTargets.Add((outputId, route.Gain));
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
                        // Fade-in: attach silent (gain 0) and ramp the route gain up to unity over FadeIn after Start.
                        player.AttachAudioOutput(o, outDef.Id, map: channelMap, gain: fadeIn ? 0f : 1f);
                        outputs.Add(o);
                        routeTargets.Add((outDef.Id, 1f));
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
            await ReplaceActiveAsync(group, armed, outputs, layer, subtitleAttachments).ConfigureAwait(false);
            group.SetActiveFadeMetadata(binding, routeTargets, fadeIn ? 0f : 1f);

            // Background per-clip work — the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor —
            // shares one cancellation, cancelled when the clip is replaced. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            var end = player.Duration - binding.EndOffset;
            var endHandling = (binding.Loop || binding.EndBehavior != ClipEndBehavior.Stop
                               || binding.EndOffset > TimeSpan.Zero || binding.FadeOut > TimeSpan.Zero)
                && player.Duration > TimeSpan.Zero
                && end > binding.StartOffset;
            if (fadeIn || endHandling)
            {
                var clipCts = new CancellationTokenSource();
                group.SetClipWorkCts(clipCts);
                if (fadeIn && routeTargets.Count > 0)
                    StartFadeIn(groupId, player, routeTargets, binding.FadeIn, clipCts.Token);
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
        var targetAudioRate = binding.AudioRoutes switch
        {
            { Count: 0 } => null, // explicitly silent: do not infer anything from the default hardware device
            { } routes => routes.Select(route => route.SampleRate).FirstOrDefault(rate => rate is > 0)
                          ?? ResolveBackendSampleRate(routes.FirstOrDefault()?.DeviceId ?? _outputDeviceId),
            null => ResolveBackendSampleRate(_outputDeviceId), // standalone/group-routing compatibility
        };
        var options = binding.AudioStreamIndex is { } audioTrack
            ? S.Media.Players.MediaPlayerOpenOptions.Default with
            {
                AudioStreamIndex = audioTrack,
                TargetAudioSampleRate = targetAudioRate,
                FileVideoDecodeQueueCapacity = 16,
            } // multi-track select (03 §6)
            : S.Media.Players.MediaPlayerOpenOptions.Default with
            {
                TargetAudioSampleRate = targetAudioRate,
                FileVideoDecodeQueueCapacity = 16,
            };
        var window = binding.StartOffset > TimeSpan.Zero
            ? new S.Media.Core.ClipWindow(binding.StartOffset, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false)
            : S.Media.Core.ClipWindow.Unbounded;
        // A non-null variant (e.g. "preview") gives a distinct standby key so this arms a FRESH instance
        // instead of consuming GO's prepared clip.
        return new ClipSpec(
            variant is null ? binding.CueId : $"{binding.CueId}:{variant}",
            ClipMediaSource.File(_registry, binding.MediaPath, options),
            window,
            cacheKey: $"{binding.MediaPath}|audio:{binding.AudioStreamIndex?.ToString() ?? "auto"}" +
                      $"|rate:{targetAudioRate?.ToString() ?? "source"}" +
                      (variant is null ? string.Empty : $"#{variant}"));
    }

    /// <summary>Returns the hardware/backend nominal rate for a device. JACK devices expose their fixed
    /// graph rate here; opening PortAudio at the media's source rate would fail for 44.1 kHz media on a
    /// 48 kHz JACK graph.</summary>
    private int? ResolveBackendSampleRate(string? deviceId)
    {
        if (_audioBackend is null)
            return null;
        var devices = _audioBackend.EnumerateOutputDevices();
        var device = !string.IsNullOrWhiteSpace(deviceId)
            ? devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            : devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        return device is { DefaultSampleRate: > 0 }
            ? checked((int)Math.Round(device.DefaultSampleRate))
            : null;
    }

    private static VideoPlacementSpec BuildVideoPlacementSpec(string compositionId, int layerIndex, ShowVideoPlacement? p) =>
        p is null
            ? new VideoPlacementSpec(compositionId, layerIndex, DestWidth: 1, DestHeight: 1)
            : new VideoPlacementSpec(
                compositionId, layerIndex,
                Opacity: p.Opacity, Placement: p.Fit,
                DestX: p.DestX, DestY: p.DestY, DestWidth: p.DestWidth, DestHeight: p.DestHeight,
                CropLeft: p.CropLeft, CropTop: p.CropTop,
                CropRight: p.CropRight, CropBottom: p.CropBottom,
                RotationDegrees: p.RotationDegrees,
                VideoFx: p.VideoFx);

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

    // --- soundboard voices (task #10) --------------------------------------------------------------

    /// <summary>Fires a soundboard voice: opens <paramref name="mediaPath"/> as a fresh player on
    /// <paramref name="deviceId"/> (or the default) at <paramref name="volume"/> and tracks it under
    /// <paramref name="voiceId"/>. Polyphonic across ids; re-firing the same id replaces its voice. Raises
    /// <see cref="VoiceEnded"/> at the voice's natural end. (Loop is a later refinement.)</summary>
    public Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId = null, float volume = 1f) =>
        InvokeAsync(async () =>
        {
            await ReleaseVoiceAsync(voiceId).ConfigureAwait(false); // re-trigger replaces the prior voice
            var outputId = $"voice:{voiceId}";
            var targetAudioRate = ResolveBackendSampleRate(deviceId ?? _outputDeviceId);
            var armed = await _standby.ArmAsync(new ClipSpec(
                outputId,
                ClipMediaSource.File(
                    _registry,
                    mediaPath,
                    S.Media.Players.MediaPlayerOpenOptions.Default with
                    {
                        TargetAudioSampleRate = targetAudioRate,
                    }),
                S.Media.Core.ClipWindow.Unbounded,
                $"{outputId}|rate:{targetAudioRate?.ToString() ?? "source"}")).ConfigureAwait(false);
            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(deviceId ?? _outputDeviceId, new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, outputId, gain: volume);
                    outputs.Add(output);
                }

                armed.Start();
                var cts = new CancellationTokenSource();
                _voices[voiceId] = new VoiceHandle(armed, outputs, outputId, cts);
                StartVoiceEndMonitor(voiceId, player, cts.Token);
            }
            catch
            {
                foreach (var output in outputs)
                    (output as IDisposable)?.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        });

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask());

    /// <summary>Stops every soundboard voice (the GUI's StopAllSounds).</summary>
    public Task StopAllVoicesAsync() =>
        InvokeAsync(async () =>
        {
            foreach (var id in _voices.Keys.ToArray())
                await ReleaseVoiceAsync(id).ConfigureAwait(false);
        });

    /// <summary>Live-sets a voice's output gain (linear). No-op when the voice isn't playing.</summary>
    public Task SetVoiceVolumeAsync(string voiceId, float volume) =>
        InvokeAsync(() =>
        {
            if (_voices.TryGetValue(voiceId, out var v)
                && v.Clip.Player.AudioRouter is { } router
                && v.Clip.Player.AudioSourceId is { } sourceId)
                router.SetRouteGain(sourceId, v.OutputId, volume);
            return Task.CompletedTask;
        });

    /// <summary>Fades a voice's gain to silence over <paramref name="duration"/>, then stops it (the GUI's
    /// FadeOutSound). No <see cref="VoiceEnded"/>. A zero/negative duration stops immediately.</summary>
    public Task FadeVoiceAsync(string voiceId, TimeSpan duration) =>
        InvokeAsync(() =>
        {
            if (!_voices.TryGetValue(voiceId, out var v))
                return Task.CompletedTask;
            if (duration <= TimeSpan.Zero)
                return ReleaseVoiceAsync(voiceId).AsTask();
            StartVoiceFadeOut(voiceId, v.Clip.Player, duration, v.Cts.Token);
            return Task.CompletedTask;
        });

    /// <summary>Whether a soundboard voice is currently playing.</summary>
    public Task<bool> IsVoicePlayingAsync(string voiceId) =>
        InvokeAsync(() => Task.FromResult(_voices.ContainsKey(voiceId)));

    private async ValueTask ReleaseVoiceAsync(string voiceId)
    {
        if (!_voices.Remove(voiceId, out var v))
            return;
        v.Cts.Cancel();
        v.Cts.Dispose();
        await v.Clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var output in v.Outputs)
            (output as IDisposable)?.Dispose();
    }

    /// <summary>Watches a voice; on natural end releases it + raises <see cref="VoiceEnded"/> (the keyed
    /// counterpart of the preview end-monitor).</summary>
    private void StartVoiceEndMonitor(string voiceId, S.Media.Players.MediaPlayer player, CancellationToken ct)
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
                                    !_voices.TryGetValue(voiceId, out var cur) || !ReferenceEquals(cur.Clip.Player, player)
                                    || (!player.IsRunning && player.Position > TimeSpan.Zero)))
                                .ConfigureAwait(false);
                            if (!ended)
                                continue;
                            await InvokeAsync(async () =>
                            {
                                if (_voices.TryGetValue(voiceId, out var cur) && ReferenceEquals(cur.Clip.Player, player))
                                {
                                    await ReleaseVoiceAsync(voiceId).ConfigureAwait(false);
                                    VoiceEnded?.Invoke(voiceId);
                                }
                            }).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* best-effort — a voice-monitor hiccup must never crash the session */ }
                },
                ct);
        }
    }

    /// <summary>Ramps a voice's gain to 0 over <paramref name="duration"/> then releases it (fade-out).</summary>
    private void StartVoiceFadeOut(string voiceId, S.Media.Players.MediaPlayer player, TimeSpan duration, CancellationToken ct)
    {
        if (player.AudioSourceId is not { } sourceId)
            return;
        var outputId = $"voice:{voiceId}";
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
                            var level = (float)Math.Clamp(1d - sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
                            var done = await InvokeAsync<bool>(() =>
                            {
                                if (ct.IsCancellationRequested
                                    || !_voices.TryGetValue(voiceId, out var cur) || !ReferenceEquals(cur.Clip.Player, player)
                                    || player.AudioRouter is not { } router)
                                    return Task.FromResult(true);
                                router.SetRouteGain(sourceId, outputId, level);
                                return Task.FromResult(level <= 0f);
                            }).ConfigureAwait(false);
                            if (done)
                                break;
                            await Task.Delay(EndMonitorPollInterval, ct).ConfigureAwait(false);
                        }
                        if (!ct.IsCancellationRequested)
                            await InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask()).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* best-effort */ }
                },
                ct);
        }
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

    /// <summary>Ramps each route's gain from silence up to its configured target over <paramref name="duration"/>
    /// (the clip was attached silent). The ramp fraction multiplies each route's <c>TargetGain</c>, so a route
    /// set below or above unity fades up to exactly that level rather than to a hardcoded 1.0 (NXT-07). A
    /// background poll that marshals each gain step onto the dispatcher; cancelled when the clip is replaced.</summary>
    private void StartFadeIn(string groupId, S.Media.Players.MediaPlayer player,
        IReadOnlyList<(string OutputId, float TargetGain)> routes, TimeSpan duration, CancellationToken ct)
    {
        if (player.AudioSourceId is null)
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
                            var frac = elapsed >= duration
                                ? 1f
                                : (float)Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
                            var done = await InvokeAsync<bool>(() =>
                            {
                                if (ct.IsCancellationRequested ||
                                    _groups.GetValueOrDefault(groupId)?.Active?.Player != player ||
                                    player.AudioRouter is null)
                                    return Task.FromResult(true);
                                var group = _groups.GetValueOrDefault(groupId);
                                group?.ApplyAudioScale(player, routes, frac);
                                return Task.FromResult(frac >= 1f);
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
                                if (ct.IsCancellationRequested
                                    || _groups.GetValueOrDefault(groupId) is not { } group
                                    || group.Active?.Player != player)
                                    return true; // clip replaced/gone → stop monitoring
                                var position = player.Position;
                                var remaining = end - position;
                                var naturalFade = binding.FadeOut > TimeSpan.Zero
                                    ? binding.FadeOut
                                    : binding.EndBehavior == ClipEndBehavior.FadeOutAndStop
                                        ? SoftStopFadeDuration
                                        : TimeSpan.Zero;

                                // Old HaPlay starts the configured fade before the out-point, fading audio and
                                // video together. Once started, the fade task owns the final release so this
                                // monitor exits and cannot race it with an immediate hard stop.
                                if (!loops && !freezes && naturalFade > TimeSpan.Zero
                                    && remaining > TimeSpan.Zero && remaining <= naturalFade
                                    && group.TryBeginFadeOut(player))
                                {
                                    StartNaturalFadeOut(
                                        groupId,
                                        group.Active!,
                                        group.ActiveRouteTargets,
                                        group.ActiveAudioScale,
                                        group.ActiveLayer?.Opacity ?? 0f,
                                        remaining,
                                        ct);
                                    return true;
                                }

                                if (position < end - EndMonitorGuard)
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
                                // Plain Stop: release the clip at the out-point. FadeOutAndStop is handled above.
                                await ReplaceActiveAsync(GetOrAddGroup(groupId), null, [], null).ConfigureAwait(false);
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

    /// <summary>Runs a natural-end audio/video fade without occupying the session dispatcher between steps,
    /// then releases the clip if it is still active.</summary>
    private void StartNaturalFadeOut(
        string groupId,
        IArmedClip clip,
        IReadOnlyList<(string OutputId, float TargetGain)> routeTargets,
        float startAudioScale,
        float startOpacity,
        TimeSpan duration,
        CancellationToken ct)
    {
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
                            var scale = (float)Math.Clamp(
                                1d - sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
                            var done = await InvokeAsync<bool>(() =>
                            {
                                if (ct.IsCancellationRequested
                                    || _groups.GetValueOrDefault(groupId) is not { } group
                                    || !ReferenceEquals(group.Active, clip))
                                    return Task.FromResult(true);
                                group.ApplyFadeLevel(
                                    clip.Player, routeTargets, startAudioScale, startOpacity, scale);
                                return Task.FromResult(scale <= 0f);
                            }).ConfigureAwait(false);
                            if (done)
                                break;
                            await Task.Delay(FadeStepInterval, ct).ConfigureAwait(false);
                        }

                        if (!ct.IsCancellationRequested)
                        {
                            await InvokeAsync(async () =>
                            {
                                if (_groups.GetValueOrDefault(groupId) is { } group
                                    && ReferenceEquals(group.Active, clip))
                                    await ReplaceActiveAsync(group, null, [], null).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // Best effort: a fade failure must not terminate the session process.
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

    /// <summary>Fires a specific cue by id (PreWait/PostWait/AutoContinue honoured by the cue graph). Runs OFF the
    /// serial dispatcher (NXT-03), so its pre-wait + media open don't park the loop — STOP/seek/load/queries stay
    /// responsive and can abort it. A cancelled fire returns <see cref="CueExecutionStatus.Failed"/>.</summary>
    public async Task<CueExecutionStatus> FireCueAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try { return await FireCoreAsync(cueId).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled by stop/load/dispose
        finally { _fireLock.Release(); }
    }

    /// <summary>The lock-free fire core (the caller MUST hold <see cref="_fireLock"/>): runs the cue graph fire OFF
    /// the serial dispatcher (NXT-03) — its pre/post-wait and media open no longer park the loop; only the short
    /// state commits re-enter it (see <see cref="PlayClipAsync"/>). The fire's cancellation source is published to
    /// <see cref="_activeFireCts"/> so <see cref="CancelActiveFire"/> aborts it; cancellation propagates as
    /// <see cref="OperationCanceledException"/> (callers map it to a non-advancing result).</summary>
    private async Task<CueExecutionStatus> FireCoreAsync(string cueId)
    {
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try { return await _cueGraph.FireAsync(cueId, cts.Token).ConfigureAwait(false); }
        finally { _activeFireCts = null; }
    }

    /// <summary>Cancels the in-flight cue fire, if any, WITHOUT marshaling onto the dispatcher — so a stop/load/
    /// dispose can unblock the serial loop that a long pre-wait or open is parking, then run promptly (NXT-03).
    /// A no-op when nothing is firing. Note: a synchronous, uninterruptible native open still runs to completion;
    /// this preempts the (common) pre/post-wait and any cancellable stage.</summary>
    private void CancelActiveFire()
    {
        var cts = _activeFireCts;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the fire already finished and disposed it */ }
    }

    /// <summary>GO — fires the next armed and enabled cue in <paramref name="groupId"/> after the cursor. A
    /// disabled or unarmed cue is skipped (never fired); the cursor advances only when the chosen cue actually
    /// ran or faulted, so a cue that was momentarily not fireable can still be reached by a later GO (NXT-07).</summary>
    public async Task<CueExecutionStatus> GoAsync(string groupId = DefaultGroup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Hold the fire-lock across select → fire → advance, so a concurrent GO (e.g. two rapid remote commands)
        // can't read the same cursor and double-fire the same cue. The fire itself still runs off the dispatcher.
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Selection on the dispatcher (reads the cue graph + the group cursor).
            var (group, next) = await InvokeAsync(() =>
            {
                var grp = GetOrAddGroup(groupId);
                var nxt = _cueGraph.Cues
                    .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > grp.LastFiredNumber
                                && c.Armed && c.Enabled)
                    .OrderBy(c => c.Number)
                    .FirstOrDefault();
                return Task.FromResult((grp, nxt));
            }).ConfigureAwait(false);

            if (next is null)
                return CueExecutionStatus.NotReady;

            // Fire OFF the dispatcher (we already hold the fire-lock — FireCoreAsync is the lock-free core).
            CueExecutionStatus status;
            try { status = await FireCoreAsync(next.Id).ConfigureAwait(false); }
            catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled — do NOT advance

            // Advance the cursor on the dispatcher — only when the cue actually ran (or faulted), never a skip/cancel.
            if (status is CueExecutionStatus.Fired or CueExecutionStatus.Failed)
                await InvokeAsync(() => { group.LastFiredNumber = next.Number; return Task.CompletedTask; }).ConfigureAwait(false);
            _ = WarmUpcomingAsync(groupId); // pre-roll the next cue(s) in the background so the next GO is instant
            return status;
        }
        finally
        {
            _fireLock.Release();
        }
    }

    /// <summary>Seeks the active clip on <paramref name="groupId"/> (coordinated A/V seek).</summary>
    public Task SeekAsync(TimeSpan position, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            GetOrAddGroup(groupId).Active?.Player.SeekCoordinated(position);
            return Task.CompletedTask;
        });

    /// <summary>Soft-stops and releases the active clip on <paramref name="groupId"/>. The cue's configured
    /// fade-out is used, falling back to the legacy HaPlay 750 ms stop fade. Cancels any in-flight cue fire first
    /// so STOP never waits behind a long pre-wait/open (NXT-03).</summary>
    public Task StopAsync(string groupId = DefaultGroup)
    {
        CancelActiveFire();
        return InvokeAsync(async () =>
        {
            var group = GetOrAddGroup(groupId);
            await FadeGroupsAsync([group]).ConfigureAwait(false);
            await ReplaceActiveAsync(group, null, [], null).ConfigureAwait(false);
        });
    }

    /// <summary>Soft-stops every active transport group together (HaPlay Stop/Panic parity).</summary>
    public Task StopAllAsync()
    {
        CancelActiveFire();
        return InvokeAsync(async () =>
        {
            var active = _groups.Values.Where(group => group.Active is not null).ToArray();
            await FadeGroupsAsync(active).ConfigureAwait(false);
            foreach (var group in active)
                await ReplaceActiveAsync(group, null, [], null).ConfigureAwait(false);
        });
    }

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is the active clip (per-cue stop /
    /// cancel — the GUI's <c>CancelCueCallback</c>). No-op when that cue isn't currently playing.</summary>
    public Task StopCueAsync(string cueId)
    {
        CancelActiveFire();
        return InvokeAsync(async () =>
        {
            foreach (var group in _groups.Values)
                if (group.Active?.Spec.Id == cueId)
                {
                    await FadeGroupsAsync([group]).ConfigureAwait(false);
                    await ReplaceActiveAsync(group, null, [], null).ConfigureAwait(false);
                }
        });
    }

    /// <summary>Ramps active audio routes and composition layers to silence. All groups advance from one
    /// stopwatch so Panic fades them concurrently instead of serially. Must be called by the session dispatcher.</summary>
    private static async Task FadeGroupsAsync(IReadOnlyList<TransportGroup> groups)
    {
        var fades = new List<GroupFade>(groups.Count);
        foreach (var group in groups)
        {
            if (group.Active is not { } clip || !group.TryBeginFadeOut(clip.Player))
                continue;
            fades.Add(new GroupFade(
                group,
                clip,
                group.ActiveBinding?.FadeOut is { } configured && configured > TimeSpan.Zero
                    ? configured
                    : SoftStopFadeDuration,
                group.ActiveAudioScale,
                group.ActiveLayer?.Opacity ?? 0f,
                group.ActiveRouteTargets));
        }
        if (fades.Count == 0)
            return;

        var maxDuration = fades.Max(fade => fade.Duration);
        if (maxDuration <= TimeSpan.Zero)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var elapsed = sw.Elapsed;
            foreach (var fade in fades)
            {
                if (!ReferenceEquals(fade.Group.Active, fade.Clip))
                    continue;
                var scale = (float)Math.Clamp(1d - elapsed.TotalMilliseconds / fade.Duration.TotalMilliseconds, 0d, 1d);
                fade.Group.ApplyFadeLevel(
                    fade.Clip.Player, fade.RouteTargets, fade.StartAudioScale, fade.StartOpacity, scale);
            }

            if (elapsed >= maxDuration)
                return;
            await Task.Delay(FadeStepInterval).ConfigureAwait(false);
        }
    }

    private sealed record GroupFade(
        TransportGroup Group,
        IArmedClip Clip,
        TimeSpan Duration,
        float StartAudioScale,
        float StartOpacity,
        IReadOnlyList<(string OutputId, float TargetGain)> RouteTargets);

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

    /// <summary>An immutable snapshot of each transport group's session time, clip position, and run state.
    /// Lock-free (NXT-16): reads the published group view and pulls live position/run-state off the captured
    /// clock/player without marshaling, so it never queues behind a long-running command on the dispatcher.</summary>
    public Task<IReadOnlyList<TransportSnapshot>> SnapshotAsync() => Task.FromResult(Snapshot());

    /// <summary>The synchronous, lock-free form of <see cref="SnapshotAsync"/> — safe to call from any thread
    /// (e.g. a 250 ms UI position poll) even while the session dispatcher is busy with a long command.</summary>
    public IReadOnlyList<TransportSnapshot> Snapshot()
    {
        var views = _groupViews; // single volatile read of the published view
        var snaps = new TransportSnapshot[views.Count];
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i];
            // The captured player/clock may be torn down concurrently by a transport command; a racing read
            // just yields a stale/zero value for one poll tick rather than throwing across the query.
            TimeSpan now = TimeSpan.Zero, pos = TimeSpan.Zero, dur = TimeSpan.Zero;
            var running = false;
            try
            {
                now = v.Clock.Now;
                if (v.Player is { } p)
                {
                    pos = p.Position;
                    dur = p.Duration;
                    running = p.IsRunning;
                }
            }
            catch { /* concurrent teardown — leave zeros for this tick */ }
            snaps[i] = new TransportSnapshot(v.GroupId, now, pos, dur, running);
        }
        return snaps;
    }

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

    /// <summary>Applies (or clears) the mapping for one physical output of a composition. The output id is
    /// supplied by the host's <see cref="ClipCompositionOutputLease"/> and remains stable across live edits.</summary>
    public Task<bool> ApplyOutputMappingAsync(
        string compositionId, string outputId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
            && composition.UpdateOutputMapping(outputId, mapping)));

    private TransportGroup GetOrAddGroup(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            _groups[groupId] = group = new TransportGroup();
            PublishGroupViews();
        }
        return group;
    }

    /// <summary>Republishes the lock-free query view (NXT-16). Called on the dispatcher after any change to the
    /// group set or a group's active clip, so <see cref="Snapshot"/> reads never round-trip the dispatcher.</summary>
    private void PublishGroupViews() =>
        _groupViews = _groups
            .Select(kv => new GroupClockView(kv.Key, kv.Value.Clock, kv.Value.Active?.Player))
            .ToArray();

    /// <summary>Swaps a group's active clip and republishes the query view so a position/state poll always sees
    /// the new run-state without waiting behind the dispatcher.</summary>
    private async ValueTask ReplaceActiveAsync(
        TransportGroup group, IArmedClip? clip, IReadOnlyList<IAudioOutput> outputs,
        ClipCompositionRuntime.LayerSlot? layer, IReadOnlyList<IDisposable>? subtitleAttachments = null)
    {
        await group.ReplaceAsync(clip, outputs, layer, subtitleAttachments).ConfigureAwait(false);
        PublishGroupViews();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelActiveFire(); // unblock the dispatcher so disposal isn't stuck behind a long in-flight fire (NXT-03)

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
        foreach (var id in _voices.Keys.ToArray())
            await ReleaseVoiceAsync(id).ConfigureAwait(false);
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);
        _groups.Clear();
        PublishGroupViews();
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
        private ShowClipBinding? _activeBinding;
        private IReadOnlyList<(string OutputId, float TargetGain)> _activeRouteTargets = [];
        private float _activeAudioScale = 1f;
        private int _fadeOutStarted;
        public int LastFiredNumber { get; set; } = int.MinValue;

        public ShowClipBinding? ActiveBinding => _activeBinding;
        public ClipCompositionRuntime.LayerSlot? ActiveLayer => _layer;
        public IReadOnlyList<(string OutputId, float TargetGain)> ActiveRouteTargets => _activeRouteTargets;
        public float ActiveAudioScale => _activeAudioScale;

        /// <summary>Hands the group the cancellation source for the active clip's background work (the fade-in
        /// ramp + the end-of-clip loop/stop/freeze monitor). Cancelled when the clip is replaced.</summary>
        public void SetClipWorkCts(CancellationTokenSource cts) => _clipWorkCts = cts;

        public void SetActiveFadeMetadata(
            ShowClipBinding binding,
            IReadOnlyList<(string OutputId, float TargetGain)> routeTargets,
            float initialAudioScale)
        {
            _activeBinding = binding;
            _activeRouteTargets = routeTargets.ToArray();
            _activeAudioScale = Math.Clamp(initialAudioScale, 0f, 1f);
            Volatile.Write(ref _fadeOutStarted, 0);
        }

        public bool TryBeginFadeOut(S.Media.Players.MediaPlayer player) =>
            Active?.Player == player && Interlocked.Exchange(ref _fadeOutStarted, 1) == 0;

        public void ApplyAudioScale(
            S.Media.Players.MediaPlayer player,
            IReadOnlyList<(string OutputId, float TargetGain)> routeTargets,
            float scale)
        {
            if (Active?.Player != player)
                return;
            _activeAudioScale = Math.Clamp(scale, 0f, 1f);
            if (player.AudioRouter is { } router && player.AudioSourceId is { } sourceId)
                foreach (var (outputId, targetGain) in routeTargets)
                    router.SetRouteGain(sourceId, outputId, targetGain * _activeAudioScale);
        }

        public void ApplyFadeLevel(
            S.Media.Players.MediaPlayer player,
            IReadOnlyList<(string OutputId, float TargetGain)> routeTargets,
            float startAudioScale,
            float startOpacity,
            float scale)
        {
            if (Active?.Player != player)
                return;
            scale = Math.Clamp(scale, 0f, 1f);
            ApplyAudioScale(player, routeTargets, startAudioScale * scale);
            if (_layer is not null)
                _layer.Opacity = startOpacity * scale;
        }

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
            _activeBinding = null;
            _activeRouteTargets = [];
            _activeAudioScale = 1f;
            Volatile.Write(ref _fadeOutStarted, 0);

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

    /// <summary>Exposes a transport group's <see cref="SessionClock"/> as an <see cref="IPlaybackClock"/> so a
    /// composition pump can be clock-mastered to the group (NXT-04). It follows whatever clip the group is
    /// playing (the SessionClock re-references the active clip's playhead) and survives clip replacement, unlike
    /// a per-clip clock which dies when its clip is released — which is what lets the once-only composition
    /// master stay valid across successive cues.</summary>
    private sealed class SessionClockMaster(SessionClock clock) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => clock.Now;
        public bool IsAdvancing => clock.IsAdvancing;
    }
}
