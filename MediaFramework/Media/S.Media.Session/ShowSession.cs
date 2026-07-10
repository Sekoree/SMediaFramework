using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>Immutable per-group transport snapshot - a query result, never mutated by the caller (D5).</summary>
/// <param name="IsActive">True when the group currently holds a clip (playing, paused, or frozen) - the reliable
/// "is this cue still up" signal. Distinct from <paramref name="IsRunning"/> (the clock is advancing), which a
/// video-only held/text clip can report <c>false</c> for while still on screen.</param>
/// <param name="TimelineGeneration">The group's timeline DISCONTINUITY generation (NXT-04): bumped on every
/// seek, loop wrap, pause/resume, and clip replacement. Pollers compare it across ticks to distinguish "the
/// timeline jumped" from "playback progressed" - the authoritative signal that replaces transient-pause
/// heuristics (a value has no meaning on its own; only change does).</param>
public sealed record TransportSnapshot(
    string GroupId,
    TimeSpan SessionTime,
    TimeSpan ClipPosition,
    TimeSpan ClipDuration,
    bool IsRunning,
    bool IsActive = false,
    bool LiveSourceDisconnected = false,
    int AudioChannels = 0,
    int AudioSampleRate = 0,
    int TimelineGeneration = 0)
{
    /// <summary>
    /// The complete NXT-04 timeline view used by render/subtitle/output consumers. The positional properties
    /// above remain for UI/API compatibility; new timing-sensitive code should consume this contract.
    /// </summary>
    public TransportTimelineSnapshot Timeline { get; init; }
}

/// <summary>A soundboard voice's playhead - for the UI's per-tile progress/countdown.</summary>
public readonly record struct VoiceProgress(string VoiceId, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// The headless home of a show (D5): cues, clips, and transport groups behind an internal dispatcher.
/// Commands marshal onto the session thread and queries return immutable snapshots - the API never assumes
/// a UI thread (the <c>ui_thread_observable_property_sets</c> bug class is structurally impossible here).
/// One <see cref="SessionClock"/> per transport group (D4); clips open through <see cref="IMediaRegistry"/>
/// (D6); when an <see cref="IAudioBackend"/> is supplied, each group plays on a master output (D11).
/// </summary>
public sealed class ShowSession : IAsyncDisposable
{
    /// <summary>The implicit group cues fall into when <see cref="CueDefinition.GroupId"/> is null.</summary>
    public const string DefaultGroup = "main";

    /// <summary>The output id of a transport group's master audio output - target it from an
    /// <see cref="OutputPatchRoute"/> (<c>OutputId</c>) to apply an N→M channel remap when clips play (03 §6).</summary>
    public const string MasterOutputId = "_master";

    private static readonly TimeSpan SoftStopFadeDuration = TimeSpan.FromMilliseconds(750);

    private readonly IMediaRegistry _registry;
    private readonly IAudioBackend? _audioBackend;
    private readonly string? _outputDeviceId;
    // Swapped atomically on load (NXT-12 transactional load); volatile because fires and the lock-free cue-
    // definition query read it OFF the dispatcher (the graph itself is internally locked).
    private volatile CueGraph _cueGraph = new();
    private readonly Dictionary<string, TransportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime> _compositions = new(StringComparer.Ordinal);
    // Lock-free view of the compositions for the UI health poll: republished (on the dispatcher) whenever
    // _compositions changes, so GetCompositionStats can read it - and the runtime's own thread-safe GetStats -
    // off any thread without marshaling (mirrors _groupViews / SnapshotAsync).
    private volatile IReadOnlyDictionary<string, ClipCompositionRuntime> _compositionsView =
        new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime.LayerSlot> _testPatternSlots = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputPatchRoute> _routes = [];
    private IReadOnlyList<ShowAudioOutput> _audioOutputs = [];
    private readonly SessionDispatcher _dispatcher = new("show-session");
    private readonly Func<string, int, int, int, IVideoOverlaySource?>? _subtitleFactory;
    // Host video-output factory (compositionId, name, width, height) → the leases a composition renders to
    // (NDI/SDL/local). The HOST owns each returned output's lifetime and declares it on the lease - a borrowed
    // host output is returned with DisposeOutputOnRuntimeDispose=false (+ an optional release callback), so the
    // session NEVER disposes it (NXT-01: disposing a borrowed SDL/NDI output is a use-after-reload defect).
    // Null ⇒ headless discard. Lets the GUI surface composited video to its output lines.
    // RELOAD ORDERING CONTRACT (NXT-20): during a document reload the factory is invoked for the NEW graph
    // while the OLD compositions still hold their leases - staging before teardown is what keeps a failed
    // load from destroying the running show (NXT-12). A host with exclusive/single-holder output lines must
    // therefore hand a still-bound line over (keep the existing acquisition and return the same output, as
    // HaPlay's hold-across-reload does) rather than release-then-reacquire, and must detach a dropped line
    // from the live compositions before releasing it.
    private readonly Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? _videoOutputFactory;
    // Host compositor factory (canvas format → compositor). Null ⇒ the default CPU compositor; a host that has a
    // GL context can inject a GPU/warp compositor so session compositions use the intended zero-copy GPU path
    // instead of building full-BGRA CPU canvases (NXT-11). Threaded into every ClipCompositionRuntime at load.
    private readonly Func<VideoFormat, ClipCompositionCompositor>? _compositorFactory;
    // Host audio-output factory (route deviceId, format) → a borrowed sink for that device, or null to let the
    // IAudioBackend create it. Mirrors _videoOutputFactory: a returned lease with DisposeOutputOnRuntimeDispose
    // = false is NEVER disposed by the session (the host owns it - e.g. an NDI sender's audio side sharing the
    // carrier that also emits the composition's video). Null ⇒ every route uses the backend device.
    private readonly Func<string, AudioFormat, ClipAudioOutputLease?>? _audioOutputFactory;
    // Opens + warms clips (seek-to-Start trim-in, standby pre-roll). Clips arm through here instead of a
    // direct MediaGraph build so the show can pre-roll upcoming cues (8b convergence). All access is on the
    // serial dispatcher; the engine is also internally thread-safe.
    private readonly ClipStandbyEngine _standby = new();
    // Cue id → its clip binding (built on load) so the standby pre-roll can look up upcoming cues' media.
    private IReadOnlyDictionary<string, ShowClipBinding> _clipsByCue =
        new Dictionary<string, ShowClipBinding>(StringComparer.Ordinal);
    // Soundboard voices + the cue preview - playback outside the transport groups, split along its ownership
    // seam (review Part-5 #2). Owns the voice/preview registries and monitors; this session's public
    // voice/preview API delegates to it.
    private readonly VoicePlayer _voicePlayer;
    private volatile bool _disposed;

    // Lock-free query view (NXT-16): a volatile snapshot of each group's clock + active player, republished on
    // the dispatcher whenever the group set or active clip changes. Snapshot() reads THIS and pulls live
    // (thread-safe) position/duration/run-state off the captured references, so a position/state poll never
    // serializes behind a long-running command on the dispatcher.
    private volatile IReadOnlyList<GroupClockView> _groupViews = [];

    private sealed record GroupClockView(string GroupId, S.Media.Players.MediaPlayer? Player, TransportGroup Group);

    // Lock-free per-device audio-pump view for the outputs-panel line-health poll (the audio analogue of
    // _compositionsView): republished on the dispatcher whenever the active clips change, so a UI poll sums a
    // routed line's enqueued/dropped chunks off-thread without marshaling. Keyed to the device the cue routed to.
    private volatile IReadOnlyList<ActiveAudioPump> _audioPumpsView = [];

    private readonly record struct ActiveAudioPump(AudioRouter Router, string OutputId, string DeviceId);

    // A clip's attached audio output plus its ownership. The session disposes it on clip replace only when
    // DisposeOnRelease (a backend-created device it owns); a host lease (e.g. an NDI carrier's audio) is
    // BORROWED - never disposed, only its Release hook is invoked so the host can drop its reference.
    private readonly record struct ClipAudioOutput(IAudioOutput Output, bool DisposeOnRelease, Action? Release);
    private sealed record AudioRouteTarget(string OutputId, float TargetGain, ShowClipAudioRoute? Route = null);

    /// <summary>Resolves a route's device to a sink: the host audio factory first (a borrowed lease it owns),
    /// else the session's <see cref="IAudioBackend"/> creates one it owns. Called only when a backend exists.</summary>
    private ClipAudioOutput ResolveAudioOutput(string? deviceId, AudioFormat format)
    {
        if (deviceId is { } id && _audioOutputFactory?.Invoke(id, format) is { } lease)
            return new ClipAudioOutput(lease.Output, lease.DisposeOutputOnRuntimeDispose, lease.Release);
        return new ClipAudioOutput(_audioBackend!.CreateOutput(deviceId, format), DisposeOnRelease: true, Release: null);
    }

    /// <summary>Teardown for one attached audio output: run the host's release hook (if any), then dispose the
    /// sink only when the session owns it.</summary>
    private static void ReleaseClipAudioOutput(ClipAudioOutput o)
    {
        o.Release?.Invoke();
        if (o.DisposeOnRelease)
            (o.Output as IDisposable)?.Dispose();
    }

    /// <summary>Resolves + attaches ONE audio route's output with per-route error isolation: a device that
    /// cannot be opened (fixed-rate JACK graph rejecting the clip's mix rate, unplugged hardware) or attached is
    /// logged and skipped so the clip still plays on its remaining routes - instead of one bad device faulting
    /// the whole cue fire or (worse) a mid-play rebuild that has already detached every output. On success the
    /// output is appended to <paramref name="outputs"/> (the caller's ownership-tracked set).</summary>
    private bool TryAttachRouteOutput(
        S.Media.Players.MediaPlayer player,
        string outputId,
        string? deviceId,
        ChannelMap? channelMap,
        int rate,
        float gain,
        List<ClipAudioOutput> outputs,
        ShowClipAudioRoute? route = null)
    {
        ClipAudioOutput o;
        try
        {
            var channels = route is { HasGainMatrix: true }
                ? route.MatrixOutputChannels ?? route.MatrixCells!.Max(c => c.OutputChannel) + 1
                : channelMap?.OutputChannels ?? 2;
            o = ResolveAudioOutput(deviceId ?? _outputDeviceId, new AudioFormat(rate, channels));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not open ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        try
        {
            if (route is { HasGainMatrix: true }
                && player.AudioRouter is { } router
                && player.AudioSourceId is { } sourceId)
            {
                router.AddOutput(o.Output, outputId);
                try
                {
                    router.ApplyMatrix(sourceId, outputId, route.ToGainMatrix(gain));
                }
                catch
                {
                    router.RemoveOutput(outputId);
                    throw;
                }
            }
            else
            {
                player.AttachAudioOutput(o.Output, outputId, map: channelMap, gain: gain);
            }
        }
        catch (Exception ex)
        {
            ReleaseClipAudioOutput(o);
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not attach ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        outputs.Add(o);
        return true;
    }

    /// <summary>A composition layer the active clip's video is fanned to, tagged by its composition + layer index
    /// so a live placement edit can target the right one when a clip is placed onto more than one layer.</summary>
    private readonly record struct PlacedLayer(
        string CompositionId, int LayerIndex, ClipCompositionRuntime.IPlacedClipLayer Slot);

    // Fire sequencing (NXT-03): the fire-lock + in-flight fire cancellation live on the orchestrator (split
    // along its ownership seam - review Part-5 #2); this session's public fire/GO API delegates to it.
    // _showGeneration is bumped on every load so a fire whose open straddled a reload discards its (now-stale)
    // clip at commit instead of corrupting the newer show.
    private readonly CueFireOrchestrator _fires;
    private volatile int _showGeneration;

    /// <param name="audioBackend">Optional. When supplied, each transport group plays its active clip on a
    /// master output created on this backend (D11). Null runs the cue/transport mechanics with no device.</param>
    /// <param name="subtitleFactory">Optional host-wired factory (path + stream index + canvas width/height → overlay
    /// source). When set, a composition-bound clip's selected subtitles auto-attach as
    /// top layer. Keeps the session renderer-agnostic - see <c>S.Media.Subtitles.SubtitleSourceFactory.FromFile</c>.</param>
    public ShowSession(
        IMediaRegistry registry,
        IAudioBackend? audioBackend = null,
        Func<string, int, int, int, IVideoOverlaySource?>? subtitleFactory = null,
        Func<string, string, int, int, IReadOnlyList<ClipCompositionOutputLease>>? videoOutputFactory = null,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null,
        Func<string, AudioFormat, ClipAudioOutputLease?>? audioOutputFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audioBackend = audioBackend;
        _deviceCache = new AudioOutputDeviceCache(audioBackend);
        _subtitleFactory = subtitleFactory;
        _videoOutputFactory = videoOutputFactory;
        _compositorFactory = compositorFactory;
        _audioOutputFactory = audioOutputFactory;
        _standby.StandbyStatesChanged += states => PreparedCuesChanged?.Invoke(states);
        if (audioBackend is not null)
        {
            var devices = audioBackend.EnumerateOutputDevices();
            _outputDeviceId = (devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault())?.Id;
        }

        _fires = new CueFireOrchestrator(this);
        _voicePlayer = new VoicePlayer(this, _standby, audioBackend, _outputDeviceId, BuildPreviewSpec, BuildVoiceSpec);
        _voicePlayer.VoiceEnded += id => VoiceEnded?.Invoke(id);
        _voicePlayer.PreviewEnded += id => PreviewEnded?.Invoke(id);
    }

    /// <summary>The preview instance's spec for a loaded cue (a variant key distinct from the GO-prepared
    /// clip so an audition never consumes it), or null when the cue has no clip binding. Dispatcher-read.</summary>
    private ClipSpec? BuildPreviewSpec(string cueId) =>
        _clipsByCue.TryGetValue(cueId, out var binding) ? BuildClipSpec(binding, "preview") : null;

    /// <summary>A soundboard voice's spec: a fresh unbounded file open at the target device's backend rate
    /// (JACK graphs reject the media's own rate - the same resolution every clip spec gets). Dispatcher-read.</summary>
    private ClipSpec BuildVoiceSpec(string outputId, string mediaPath, string? deviceId)
    {
        var targetAudioRate = ResolveBackendSampleRate(deviceId ?? _outputDeviceId);
        return new ClipSpec(
            outputId,
            ClipMediaSource.File(
                _registry,
                mediaPath,
                S.Media.Players.MediaPlayerOpenOptions.Default with
                {
                    TargetAudioSampleRate = targetAudioRate,
                }),
            S.Media.Core.ClipWindow.Unbounded,
            $"{outputId}|rate:{targetAudioRate?.ToString() ?? "source"}");
    }

    /// <summary>The registry clips open through (frozen capabilities - D6).</summary>
    public IMediaRegistry Registry => _registry;

    /// <summary>Raised when the standby engine's prepared-clip set changes (the GUI's
    /// <c>PreparedCueStatesChanged</c> - a per-cue "ready" indicator). Forwards
    /// <see cref="ClipStandbyEngine"/>'s states; the UI handler marshals to the UI thread, as it does today.</summary>
    public event Action<IReadOnlyList<ClipPreparationStatus>>? PreparedCuesChanged;

    /// <summary>Raised (with the cue id) when a preview started by <see cref="PreviewCueAsync"/> ends on its
    /// own (the GUI's <c>PreviewEnded</c>). Not raised on an explicit <see cref="StopPreviewAsync"/>.</summary>
    public event Action<string>? PreviewEnded;

    /// <summary>Raised (with the voice id) when a soundboard voice started by <see cref="FireVoiceAsync"/> ends
    /// on its own. Not raised on an explicit <see cref="StopVoiceAsync"/> / <see cref="FadeVoiceAsync"/>.</summary>
    public event Action<string>? VoiceEnded;

    /// <summary>Raised (with the cue id) when a transport-group clip reaches its NATURAL end and is released by
    /// the end-of-clip machinery - the trimmed/duration out-point's plain stop, or a natural fade-out completing.
    /// Never raised for an operator stop/cancel/reload, a loop (keeps running), or a freeze (stays up) - it is
    /// the host's cue auto-follow trigger (the legacy engine's <c>NaturalEnd</c>). Raised from the session
    /// dispatcher; marshal in the handler.</summary>
    public event Action<string>? ClipNaturallyEnded;

    /// <summary>Whether the session is disposed - for owned components' commit-time staleness checks
    /// (<see cref="VoicePlayer"/>); the public API throws via <see cref="ObjectDisposedException.ThrowIf"/>.</summary>
    internal bool IsDisposed => _disposed;

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
        _fires.CancelActiveFire(); // a reload must not wait behind a long in-flight fire (NXT-03)
        return InvokeAsync(() => LoadDocumentCoreAsync(document));
    }

    private async Task LoadDocumentCoreAsync(ShowDocument document)
    {
        // Normalize null collections FIRST (NXT-12): a minimal/older JSON simply omits arrays the document
        // gained later, and source-gen leaves missing positional params null. Every consumer below (and at
        // fire time) assumes non-null lists, so a partial document must never smuggle a null past the load.
        document = document with
        {
            Cues = document.Cues ?? [],
            Clips = document.Clips ?? [],
            Compositions = document.Compositions ?? [],
            Routes = document.Routes ?? [],
            AudioOutputs = document.AudioOutputs ?? [],
        };

        // Validate BEFORE any teardown - a malformed document (bad version, duplicate ids/numbers, dangling
        // references, a cyclic auto-continue chain) must never destroy the running show (NXT-12 / NXT-07).
        ShowDocumentValidator.ThrowIfInvalid(document);

        // Stage the replacement graph in locals. If a composition fails to construct (or the host video factory
        // throws), dispose only the partially-built NEW compositions and rethrow - the live show is untouched.
        var newCompositions = new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
        try
        {
            foreach (var comp in document.Compositions)
            {
                var definition = new ClipCompositionDefinition(
                    comp.Id, comp.Name, comp.Width, comp.Height, comp.FrameRateNum, comp.FrameRateDen);
                // Host-provided leases (the GUI's NDI/SDL/local lines for this composition). The host owns each
                // output's lifetime and declares dispose/release on its lease - the session must NOT dispose a
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
            throw; // running show left intact - fields are not mutated until the commit below
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

        _testPatternSlots.Clear(); // slots are owned by their compositions (disposed below); drop stale refs
        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
        foreach (var (id, runtime) in newCompositions)
            _compositions[id] = runtime;
        PublishCompositionsView(); // refresh the lock-free health-poll view for the new composition set

        _cueGraph = newCueGraph;
        _clipsByCue = newClipsByCue;
        _routes = document.Routes;
        _audioOutputs = document.AudioOutputs;
        _showGeneration++; // a fire whose open straddled this reload bails at commit (NXT-03 off-dispatcher)

        // Background pre-roll of the first cues so the first GO arms instantly. Launched with ExecutionContext
        // flow SUPPRESSED (NXT-22): we are ON the dispatcher here, and a plain fire-and-forget would carry the
        // dispatcher's AsyncLocal identity into the warm task's continuations - a future InvokeAsync from such a
        // continuation would run inline OFF the real loop and race transport commands (the same trap the
        // monitors guard against with SuppressFlow).
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => WarmUpcomingAsync());
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
            // Compositions are resolved per-placement in CommitClipAsync (also on the dispatcher) so a clip can
            // fan onto several - the group + generation are all the pre-open setup needs.
            return Task.FromResult((generation, group: grp));
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): arm the clip through the standby engine - it opens via the registry
        // (auto-wiring adaptive-rate drift correction) and seeks to the trim-in (Window.Start), reusing a warm
        // prepared clip when present. The long part; the dispatcher loop stays free throughout (NXT-03).
        var armed = await _standby.ArmAsync(BuildClipSpec(binding), cancellationToken).ConfigureAwait(false);

        // --- COMMIT (on the dispatcher): swap the armed clip in, or discard it if the show was reloaded / the
        // fire was cancelled (STOP) during the open (NXT-03).
        await InvokeAsync(() => CommitClipAsync(groupId, binding, cancellationToken, setup, armed)).ConfigureAwait(false);
    }

    /// <summary>The on-dispatcher commit half of <see cref="PlayClipAsync"/> (NXT-03): attaches the freshly-armed
    /// clip's outputs, masters the composition, and swaps it in - unless the show was reloaded (generation moved)
    /// or the fire was cancelled while the clip opened off the dispatcher, in which case it discards the now-stale
    /// clip without touching the live show.</summary>
    private async Task CommitClipAsync(
        string groupId,
        ShowClipBinding binding,
        CancellationToken cancellationToken,
        (int generation, TransportGroup group) setup,
        IArmedClip armed)
    {
        if (cancellationToken.IsCancellationRequested || _showGeneration != setup.generation || _disposed)
        {
            await armed.ReleaseAsync().ConfigureAwait(false);
            return;
        }

        var group = setup.group;
        var player = armed.Player;
        var layers = new List<PlacedLayer>();
        var outputs = new List<ClipAudioOutput>();
        var subtitleAttachments = new List<IDisposable>();
        var fadeIn = binding.FadeIn > TimeSpan.Zero;
        // Retained for the active clip so both fade-in and every stop path ramp each route relative to its
        // configured gain rather than assuming unity.
        var routeTargets = new List<AudioRouteTarget>();
        // Device-tagged routed outputs (OutputId → device) for the per-line audio-health poll.
        var audioPumps = new List<(string OutputId, string DeviceId)>();
        try
        {
            if (player.VideoSource is { } videoSource)
            {
                // A cue may place its ONE decoded source onto several composition layers at once - PiP, the same
                // feed in two regions, or mirrored to a second canvas. Fan the player's video out to each: one
                // LayerSlot per placement, all fed by the same VideoRouter input through a unique output id.
                // PlacementResolver scales source pixels into the normalized destination rectangle; passing the
                // negotiated source format (not the canvas) keeps a clip smaller than the canvas correctly sized
                // rather than identity-stretched.
                //
                // NXT-04: every DISTINCT composition follows the group's one authoritative TransportTimeline.
                // Its master coordinate drives cadence/output target time; its source coordinate selects frames;
                // cue-local origin/trim/rate/live-correlation remain available on the same generation. This also
                // closes the former live free-run exception: a live clip is correlated to the group and composed
                // against that contract rather than using an unrelated latest-frame clock.
                var mastered = new HashSet<string>(StringComparer.Ordinal);
                var fanoutIndex = 0;
                var placements = binding.GetPlacements();

                // GPU surface path (NXT-10): a single-placement clip whose source can render itself as a
                // compositor layer surface, on a surface-hosting compositor, composites GPU-side - no CPU
                // frame fan-out at all. The surface renders at the pump's SOURCE-time coordinate (the same
                // TransportTimeline that selects decoded frames), so transport (seek/pause/trim/end
                // monitoring) behaves identically to the frame path. Multi-placement clips keep the frame
                // path: one decoded stream fans out cheaply, N independent GPU renders would not.
                if (player.VideoSource is ILayerSurfaceVideoSource surfaceSource
                    && placements.Count == 1
                    && _compositions.TryGetValue(placements[0].CompositionId, out var surfaceComp)
                    && surfaceComp.SupportsSurfaceLayers)
                {
                    var placement = placements[0];
                    var surfaceSlot = surfaceComp.AddSurfaceLayer(
                        surfaceSource.CreateLayerSurface(),
                        BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                    surfaceComp.SetTransportTimeline(group.Timeline);
                    layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, surfaceSlot));
                    MediaDiagnostics.LogInformation(
                        "clip {CueId}: video composites as a GPU layer surface on {Composition} (NXT-10)",
                        binding.CueId, placement.CompositionId);
                }
                else
                {
                    foreach (var placement in placements)
                    {
                        if (!_compositions.TryGetValue(placement.CompositionId, out var comp))
                            continue;
                        var slot = comp.AddLayer(
                            videoSource.Format,
                            BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                        player.AttachVideoOutput(slot.Output, id: $"comp{fanoutIndex++}"); // unique id ⇒ router fans out
                        if (mastered.Add(placement.CompositionId))
                            comp.SetTransportTimeline(group.Timeline);
                        layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, slot));
                    }
                }
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
                        var outputId = $"clip{i}";
                        if (!TryAttachRouteOutput(
                                player, outputId, route.DeviceId, route.ToChannelMap(), rate,
                                gain: fadeIn ? 0f : route.Gain, outputs, route))
                            continue; // one un-openable device must not fault the whole cue - play the rest
                        routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                        if (route.DeviceId is { } clipDevice)
                            audioPumps.Add((outputId, clipDevice));
                    }
                }
                else
                {
                    // D11 per-group outputs: attach the clip's audio to each output the group declares (the first
                    // is the master/clock; the rest auto-slave with adaptive-rate). Each output's N→M channel
                    // matrix (03 §6) comes from the matching source→output route - it remaps the source channels +
                    // sets the channel count; an output with no route for this clip is plain stereo.
                    foreach (var outDef in ResolveGroupOutputs(groupId))
                    {
                        // Fade-in: attach silent (gain 0) and ramp the route gain up to unity over FadeIn after Start.
                        if (!TryAttachRouteOutput(
                                player, outDef.Id, outDef.DeviceId, ResolveOutputChannelMap(binding, outDef.Id), rate,
                                gain: fadeIn ? 0f : 1f, outputs))
                            continue;
                        routeTargets.Add(new AudioRouteTarget(outDef.Id, 1f));
                        if (outDef.DeviceId is { } groupDevice)
                            audioPumps.Add((outDef.Id, groupDevice));
                    }
                }
            }

            if (layers.Count > 0
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
                            overlay, group.Timeline, nextLayerIndex++));
                    }
                }
            }

            armed.Start();
            await ReplaceActiveAsync(group, armed, outputs, layers, subtitleAttachments, binding).ConfigureAwait(false);
            group.SetActiveFadeMetadata(binding, routeTargets, fadeIn ? 0f : 1f);
            // Publish the device-tagged audio outputs for the line-health poll (ReplaceActiveAsync republished
            // the group views before these were set, so refresh once more now they're known).
            group.SetActiveAudioPumps(audioPumps);
            PublishGroupViews();

            // Background per-clip work - the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor -
            // shares one cancellation, cancelled when the clip is replaced. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            var end = player.Duration - binding.EndOffset;
            var endHandling = (binding.Loop || binding.EndBehavior != ClipEndBehavior.Stop
                               || binding.EndOffset > TimeSpan.Zero || binding.FadeOut > TimeSpan.Zero
                               || binding.EndAtDuration || binding.NotifyNaturalEnd)
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
                ReleaseClipAudioOutput(output);
            await armed.ReleaseAsync().ConfigureAwait(false);
            foreach (var placed in layers)
                placed.Slot.Dispose();
            throw;
        }
    }

    /// <summary>Builds the standby <see cref="ClipSpec"/> for a clip binding - identical on the pre-roll
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

    // NXT-24: backend device enumeration is not free (PortAudio walks the host API's device table, and a flaky
    // ALSA setup makes it worse) and the spec builder runs on EVERY fire / warm / voice. SESSION-01: the caching
    // + backend-rate resolution now lives in AudioOutputDeviceCache; the session just holds one.
    private readonly AudioOutputDeviceCache _deviceCache;

    private int? ResolveBackendSampleRate(string? deviceId) => _deviceCache.ResolveBackendSampleRate(deviceId);

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
    /// <c>UpdateActiveCueVideoPlacement</c>) - repositions / re-opacities its layer. Returns false when the
    /// cue isn't the active clip on any group (or has no composition layer).</summary>
    public Task<bool> UpdateActivePlacementAsync(string cueId, string compositionId, int layerIndex, ShowVideoPlacement placement) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active?.Spec.Id == cueId)
                    return Task.FromResult(group.UpdateActivePlacement(
                        compositionId, layerIndex, BuildVideoPlacementSpec(compositionId, layerIndex, placement)));
            return Task.FromResult(false);
        });

    /// <summary>Hot-attaches an output lease to a LIVE composition so a playing clip starts fanning its
    /// composited video to a newly-selected line WITHOUT a re-fire (the GUI's <c>TryAddOutput</c> under the
    /// ShowSession path). Returns false when the composition isn't currently loaded. The lease carries the same
    /// borrowed/owned ownership contract as the fire-path video leases (a borrowed host output declares
    /// <see cref="ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose"/> = false).</summary>
    public Task<bool> AddCompositionOutputAsync(string compositionId, ClipCompositionOutputLease lease)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentNullException.ThrowIfNull(lease);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.AddOutput(lease)));
    }

    /// <summary>Hot-detaches an output (by its lease <c>OutputId</c>) from a LIVE composition - the GUI's
    /// <c>TryRemoveOutput</c> under the ShowSession path. Returns false when the composition isn't loaded or had
    /// no such output. The detached output is NOT disposed here (the host that leased it owns its lifetime).</summary>
    public Task<bool> RemoveCompositionOutputAsync(string compositionId, string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.RemoveOutput(outputId)));
    }

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

    /// <summary>Live-edit the active cue's audio routing by re-applying its per-output-line routes (each line's
    /// channel map/full gain matrix + gain) while it plays - the GUI's <c>UpdateActiveCueAudioRoutes</c> under the
    /// ShowSession path. Each route <c>i</c> replaces every route for the clip's <c>clip{i}</c> output, then installs
    /// either its legacy channel map or its per-cell matrix. Returns false when
    /// the cue isn't the active clip on any group. If the live clip-output count no longer matches the edited route
    /// count (a line was added/removed/muted mid-playback, which reorders the positional <c>clip{i}</c> ids), the
    /// live apply is skipped so nothing is mis-patched - that change lands cleanly on the next fire instead.</summary>
    public Task<bool> ApplyActiveAudioRoutesAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active is { } active && active.Spec.Id == cueId
                    && active.Player.AudioRouter is { } router
                    && active.Player.AudioSourceId is { } sourceId)
                {
                    // Count the clip's contiguous clip0..clipN outputs; only live-apply when that count matches the
                    // edited routes (stable composition - the common level/channel tweak). A count change reorders
                    // the positional ids, so defer it to the next fire rather than mis-patch a live output.
                    var ids = router.GetRegisteredOutputIds().ToHashSet(StringComparer.Ordinal);
                    var liveClipOutputs = 0;
                    while (ids.Contains($"clip{liveClipOutputs}"))
                        liveClipOutputs++;
                    if (liveClipOutputs != routes.Count)
                        return Task.FromResult(true); // composition changed → applies on the next fire

                    var updatedTargets = new List<AudioRouteTarget>(routes.Count);
                    for (var i = 0; i < routes.Count; i++)
                    {
                        var map = routes[i].ToChannelMap();
                        var outputId = $"clip{i}";
                        if (!routes[i].HasGainMatrix && map is null)
                        {
                            // A fully-unrouted line carries no map - nothing to re-apply. Its previously
                            // installed route keeps playing, so keep its OLD target too: dropping it from the
                            // rebuilt list would exempt that line from stop-fades/scale rides (hard cut).
                            if (group.ActiveRouteTargets.FirstOrDefault(t => t.OutputId == outputId) is { } kept)
                                updatedTargets.Add(kept);
                            continue;
                        }
                        var old = group.ActiveRouteTargets.FirstOrDefault(t => t.OutputId == outputId);
                        var switchedKinds = old is null || old.Route?.HasGainMatrix != routes[i].HasGainMatrix;
                        try
                        {
                            // Same-kind updates reconcile in place (matrix cells ramp atomically; legacy route id
                            // replaces in place). Only a matrix↔legacy mode switch needs all pair routes removed.
                            if (switchedKinds)
                                router.RemoveRoute(sourceId, outputId);
                            if (routes[i].HasGainMatrix)
                                router.ApplyMatrix(sourceId, outputId,
                                    routes[i].ToGainMatrix(routes[i].Gain * group.ActiveAudioScale));
                            else
                                router.AddRoute(sourceId, outputId, map!.Value,
                                    routes[i].Gain * group.ActiveAudioScale);
                            updatedTargets.Add(new AudioRouteTarget(outputId, routes[i].Gain, routes[i]));
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            // channel count mismatch vs the live output - lands on the next fire
                            if (old is not null)
                            {
                                if (switchedKinds && old.Route is { } oldRoute)
                                {
                                    try
                                    {
                                        if (oldRoute.HasGainMatrix)
                                            router.ApplyMatrix(sourceId, outputId,
                                                oldRoute.ToGainMatrix(old.TargetGain * group.ActiveAudioScale));
                                        else if (oldRoute.ToChannelMap() is { } oldMap)
                                            router.AddRoute(sourceId, outputId, oldMap,
                                                old.TargetGain * group.ActiveAudioScale);
                                    }
                                    catch (Exception rollbackEx) when (
                                        rollbackEx is ArgumentException or InvalidOperationException)
                                    {
                                        // The output changed underneath both edits; the next rebuild/fire owns it.
                                    }
                                }
                                updatedTargets.Add(old); // keep stop/fade ownership of the still-installed route
                            }
                        }
                    }

                    group.SetActiveRouteTargets(updatedTargets);

                    return Task.FromResult(true);
                }

            return Task.FromResult(false);
        });
    }

    /// <summary>REBUILDS the active cue's audio outputs from a fresh route set while it plays - the count-change
    /// counterpart of <see cref="ApplyActiveAudioRoutesAsync"/> (which only re-applies in place for a stable
    /// count). Removes EVERY current <c>clip{i}</c> output from the router (its <c>_audio_discard</c>
    /// negotiation-lead sink stays, so the router keeps running - the clip plays on even with ZERO device
    /// outputs, on the wall clock), then re-adds one output per route. Used by the deck's hot output add/remove so
    /// unrouting an output keeps playback going and re-routing re-attaches at the live position. Returns false
    /// when the cue isn't the active clip on any group.</summary>
    public Task<bool> RebuildActiveClipAudioOutputsAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
            {
                if (group.Active is not { } active || active.Spec.Id != cueId
                    || active.Player.AudioRouter is not { } router || active.Player.AudioSourceId is null)
                    continue;

                // 1) Drop every current clip{i} output from the router FIRST (before releasing the tracked sinks,
                //    so no route dangles to a released output). The discard sink is left, so the router keeps pacing.
                foreach (var id in router.GetRegisteredOutputIds()
                             .Where(id => id.StartsWith("clip", StringComparison.Ordinal)).ToList())
                    router.RemoveOutput(id);

                // 2) Re-add one output per route (mirrors CommitClipAsync's per-clip audio block). Per-route
                //    isolation is CRITICAL here: step 1 already removed every output, so without it one
                //    un-openable device (e.g. a fixed-rate JACK graph rejecting the clip's mix rate) faulted the
                //    whole rebuild and left the clip totally silent instead of playing its remaining routes.
                var rate = active.Player.SampleRate > 0 ? active.Player.SampleRate : 48_000;
                var newOutputs = new List<ClipAudioOutput>(routes.Count);
                var audioPumps = new List<(string OutputId, string DeviceId)>();
                var routeTargets = new List<AudioRouteTarget>();
                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];
                    var outputId = $"clip{i}";
                    if (!TryAttachRouteOutput(
                            active.Player, outputId, route.DeviceId, route.ToChannelMap(), rate,
                            gain: route.Gain, newOutputs, route))
                        continue;
                    routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                    if (route.DeviceId is { } dev)
                        audioPumps.Add((outputId, dev));
                }

                // 3) Swap the group's tracked set, release the OLD one per ownership, refresh route targets + pumps.
                foreach (var o in group.SwapAudioOutputs(newOutputs))
                    ReleaseClipAudioOutput(o);
                group.SetActiveRouteTargets(routeTargets);
                group.SetActiveAudioPumps(audioPumps);
                PublishGroupViews();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
    }

    /// <summary>Live-swap the active cue's held video frame - a text / still cue whose content was edited while it
    /// plays - with no reload or re-fire. Finds the cue's active clip and, if its source supports it
    /// (<see cref="IReplaceableFrameSource"/>, e.g. a rendered text source), replaces the displayed frame in place.
    /// Returns false when the cue isn't the active clip on any group or its source can't be swapped; the session
    /// owns <paramref name="frame"/> after this call (disposed if not applied).</summary>
    public Task<bool> UpdateActiveClipFrameAsync(string cueId, VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active is { } active && active.Spec.Id == cueId
                    && active.Player.VideoSource is IReplaceableFrameSource replaceable)
                {
                    replaceable.ReplaceFrame(frame);
                    return Task.FromResult(true);
                }

            frame.Dispose(); // not applied → don't leak the caller's frame
            return Task.FromResult(false);
        });
    }

    /// <summary>Previews a loaded cue's clip on a separate (preview / headphones) device, independent of the
    /// transport groups (the GUI's <c>PreviewCue</c>). Opens a FRESH instance (not the standby-prepared clip),
    /// plays it on <paramref name="previewDeviceId"/> (or the default device), and fires
    /// <see cref="PreviewEnded"/> at its natural end. Replaces any current preview. Returns false when the cue
    /// has no clip binding, or when the preview was preempted (stopped/replaced) while its media was opening.
    /// The open runs OFF the serial dispatcher (NXT-19) so a slow audition open never parks transport, and
    /// <see cref="StopPreviewAsync"/> / a replacing preview cancels it mid-open.</summary>
    public Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.PreviewCueAsync(cueId, previewDeviceId);
    }

    /// <summary>Stops the current preview, if any (the GUI's <c>StopPreview</c>) - including one still opening
    /// (NXT-19). Does not raise <see cref="PreviewEnded"/>.</summary>
    public Task StopPreviewAsync() => _voicePlayer.StopPreviewAsync();

    // --- soundboard voices (task #10) --------------------------------------------------------------

    /// <summary>Fires a soundboard voice: opens <paramref name="mediaPath"/> as a fresh player on
    /// <paramref name="deviceId"/> (or the default) at <paramref name="volume"/> and tracks it under
    /// <paramref name="voiceId"/>. Polyphonic across ids; re-firing the same id replaces its voice (including a
    /// still-opening one). Raises <see cref="VoiceEnded"/> at the voice's natural end. The media open runs OFF
    /// the serial dispatcher (NXT-19) - a slow open never parks transport - and
    /// <see cref="StopVoiceAsync"/>/<see cref="StopAllVoicesAsync"/>/a re-fire/dispose preempt it; a preempted
    /// fire completes without error (the voice simply never started). (Loop is a later refinement.)</summary>
    public Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId = null, float volume = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.FireVoiceAsync(voiceId, mediaPath, deviceId, volume);
    }

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => _voicePlayer.StopVoiceAsync(voiceId);

    /// <summary>Stops every soundboard voice (the GUI's StopAllSounds) - including any still opening (NXT-19).</summary>
    public Task StopAllVoicesAsync() => _voicePlayer.StopAllVoicesAsync();

    /// <summary>Live-sets a voice's output gain (linear). No-op when the voice isn't playing.</summary>
    public Task SetVoiceVolumeAsync(string voiceId, float volume) => _voicePlayer.SetVoiceVolumeAsync(voiceId, volume);

    /// <summary>Fades a voice's gain to silence over <paramref name="duration"/>, then stops it (the GUI's
    /// FadeOutSound). No <see cref="VoiceEnded"/>. A zero/negative duration stops immediately.</summary>
    public Task FadeVoiceAsync(string voiceId, TimeSpan duration) => _voicePlayer.FadeVoiceAsync(voiceId, duration);

    /// <summary>Whether a soundboard voice is currently playing (a lock-free view read - NXT-16 residue).</summary>
    public Task<bool> IsVoicePlayingAsync(string voiceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.IsVoicePlayingAsync(voiceId);
    }

    /// <summary>Per-voice playhead (id, position, duration) for every currently-playing soundboard voice - the
    /// source for the UI's per-tile progress/countdown (a lock-free view read - NXT-16 residue). Empty when
    /// nothing is playing.</summary>
    public Task<IReadOnlyList<VoiceProgress>> GetVoiceProgressAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _voicePlayer.GetVoiceProgressAsync();
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
    /// the last fired in <paramref name="groupId"/> so the next GO arms instantly. Best-effort - a warm
    /// failure is swallowed and never affects transport. Awaitable for the UI/tests; <see cref="GoAsync"/>
    /// fires it without awaiting so the opens run in the background while the current cue plays. Only the
    /// cue/group state read marshals onto the dispatcher - the standby refresh (which OPENS media) runs OFF
    /// it (NXT-19/NXT-22): awaiting warm opens inside a dispatcher work item would park the loop for their
    /// whole duration (a blocked pre-roll open would freeze every transport command behind it).</summary>
    public async Task WarmUpcomingAsync(string groupId = DefaultGroup, int count = 2)
    {
        try
        {
            var specs = await InvokeAsync(() => Task.FromResult(BuildUpcomingSpecs(groupId, count)))
                .ConfigureAwait(false);
            if (specs.Count > 0)
                await _standby.RefreshStandbyAsync(
                        specs,
                        new ClipStandbyPolicy(MaxPreparedDecoders: count, Window: count))
                    .ConfigureAwait(false);
        }
        catch
        {
            // best-effort pre-roll - a failed warm just means the next GO opens on demand
        }
    }

    private static readonly TimeSpan FadeStepInterval = FadeRamp.DefaultStepInterval;

    /// <summary>Ramps each route's gain from silence up to its configured target over <paramref name="duration"/>
    /// (the clip was attached silent). The ramp fraction multiplies each route's <c>TargetGain</c>, so a route
    /// set below or above unity fades up to exactly that level rather than to a hardcoded 1.0 (NXT-07).
    /// A <see cref="FadeRamp"/>; cancelled when the clip is replaced.</summary>
    private void StartFadeIn(string groupId, S.Media.Players.MediaPlayer player,
        IReadOnlyList<AudioRouteTarget> routes, TimeSpan duration, CancellationToken ct)
    {
        if (player.AudioSourceId is null)
            return;

        FadeRamp.Start(FadeStepInterval, ct, elapsed => InvokeAsync<bool>(() =>
        {
            if (ct.IsCancellationRequested ||
                _groups.GetValueOrDefault(groupId)?.Active?.Player != player ||
                player.AudioRouter is null)
                return Task.FromResult(true);
            var frac = FadeRamp.LevelUp(elapsed, duration);
            _groups.GetValueOrDefault(groupId)?.ApplyAudioScale(player, routes, frac);
            return Task.FromResult(frac >= 1f);
        }));
    }

    internal static readonly TimeSpan EndMonitorPollInterval = TimeSpan.FromMilliseconds(100); // also the voice/preview monitors' rate
    private static readonly TimeSpan EndMonitorGuard = TimeSpan.FromMilliseconds(120);

    /// <summary>Consecutive end-monitor ticks a NotifyNaturalEnd clip's audio clock must sit stopped (not
    /// host-paused) before the stall is treated as source EOF - 500 ms at the 100 ms poll, comfortably past a
    /// coordinated seek's transient clock pause (~100–300 ms) while still snappy for cue auto-follow.</summary>
    private const int EndMonitorStallTicks = 5;

    /// <summary>Watches the active clip's position and applies its end-of-clip behaviour at <paramref name="end"/>
    /// (the trimmed out-point, or the natural duration): <see cref="ClipEndBehavior.Loop"/> → seek back to the
    /// in-point; <see cref="ClipEndBehavior.FreezeLastFrame"/> → pause on the last frame; otherwise stop. A
    /// background poll that marshals each check onto the dispatcher (cancelled when the clip is replaced).
    /// Position-based, not <c>IsRunning</c> - a paused clip's position is frozen below <paramref name="end"/>,
    /// so pause is never mistaken for end.</summary>
    private void StartEndMonitor(string groupId, ShowClipBinding binding, S.Media.Players.MediaPlayer player, TimeSpan end, CancellationToken ct)
    {
        var loops = binding.Loop || binding.EndBehavior == ClipEndBehavior.Loop;
        var freezes = binding.EndBehavior == ClipEndBehavior.FreezeLastFrame;
        var start = binding.StartOffset;
        var stalledTicks = 0;
        var lastTimelineGeneration = -1;

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

                                // A timeline discontinuity (seek/pause/resume - NXT-04 generation) restarts the
                                // stall persistence window: its transient clock pause must never accumulate
                                // toward the stall-at-EOF verdict, however long the pipeline takes to settle.
                                var generation = group.Timeline.Generation;
                                if (generation != lastTimelineGeneration)
                                {
                                    lastTimelineGeneration = generation;
                                    stalledTicks = 0;
                                }

                                // Stall-at-EOF (NotifyNaturalEnd only): a real file can end short of its metadata
                                // duration (VBR/imprecise headers), so position never reaches the out-point and the
                                // position check below would idle forever. An audio-clocked player whose clock has
                                // stopped mid-clip without a host pause has hit source EOF (or an unrecoverable
                                // stall - either way the clip is over for the operator). Requires the stop to
                                // PERSIST so a coordinated seek's transient clock pause can't be mistaken for it.
                                // Audio-clocked only: a video-only held clip's clock legitimately idles while up.
                                if (binding.NotifyNaturalEnd && !loops && !freezes
                                    && player.SampleRate > 0
                                    && !group.PausedByHost
                                    && !player.IsRunning
                                    && position > TimeSpan.Zero)
                                {
                                    if (++stalledTicks >= EndMonitorStallTicks)
                                    {
                                        await ReplaceActiveAsync(group, null, [], []).ConfigureAwait(false);
                                        ClipNaturallyEnded?.Invoke(binding.CueId); // EOF stall = natural end
                                        return true;
                                    }
                                }
                                else
                                {
                                    stalledTicks = 0;
                                }
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
                                    var masterBeforeLoop = group.Timeline.GetSnapshot().MasterTime;
                                    // Restores play state even when the wrap-seek faults (same contract as
                                    // SeekAsync) - a failed loop restart must not freeze the clip at the
                                    // out-point with the monitor still believing it loops.
                                    SeekCoordinatedRestoringPlayState(player, start, group, masterBeforeLoop, resume: true);
                                    return false; // keep looping
                                }
                                if (freezes)
                                {
                                    player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                                    group.Timeline.MarkDiscontinuity();
                                    return true; // held on the last frame
                                }
                                // Plain Stop: release the clip at the out-point. FadeOutAndStop is handled above.
                                await ReplaceActiveAsync(GetOrAddGroup(groupId), null, [], []).ConfigureAwait(false);
                                ClipNaturallyEnded?.Invoke(binding.CueId); // natural end → host cue auto-follow
                                return true;
                            }).ConfigureAwait(false);
                            if (done)
                                return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // best-effort - an end-monitor hiccup must never crash the session
                    }
                },
                ct);
        }
    }

    /// <summary>Runs a natural-end audio/video fade without occupying the session dispatcher between steps
    /// (a <see cref="FadeRamp"/>), then releases the clip if it is still active.</summary>
    private void StartNaturalFadeOut(
        string groupId,
        IArmedClip clip,
        IReadOnlyList<AudioRouteTarget> routeTargets,
        float startAudioScale,
        float startOpacity,
        TimeSpan duration,
        CancellationToken ct)
    {
        FadeRamp.Start(
            FadeStepInterval, ct,
            step: elapsed => InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || _groups.GetValueOrDefault(groupId) is not { } group
                    || !ReferenceEquals(group.Active, clip))
                    return Task.FromResult(true);
                var scale = FadeRamp.LevelDown(elapsed, duration);
                group.ApplyFadeLevel(
                    clip.Player, routeTargets, startAudioScale, startOpacity, scale);
                return Task.FromResult(scale <= 0f);
            }),
            onCompleted: () => InvokeAsync(async () =>
            {
                if (_groups.GetValueOrDefault(groupId) is { } group
                    && ReferenceEquals(group.Active, clip))
                {
                    var endedCueId = clip.Spec.Id;
                    await ReplaceActiveAsync(group, null, [], []).ConfigureAwait(false);
                    ClipNaturallyEnded?.Invoke(endedCueId); // natural fade-out completed
                }
            }));
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

    // --- transport commands (marshaled - D5) -------------------------------------------------------

    /// <summary>Fires a specific cue by id (PreWait/PostWait/AutoContinue honoured by the cue graph). Runs OFF the
    /// serial dispatcher (NXT-03), so its pre-wait + media open don't park the loop - STOP/seek/load/queries stay
    /// responsive and can abort it. A cancelled fire returns <see cref="CueExecutionStatus.Failed"/>.</summary>
    public Task<CueExecutionStatus> FireCueAsync(string cueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCueAsync(cueId);
    }

    /// <summary>Runs the current cue graph's fire - the <see cref="CueFireOrchestrator"/>'s state seam. Reads
    /// <see cref="_cueGraph"/> off-dispatcher exactly as the fire core always has (the graph reference swaps
    /// atomically on load; the show-generation guard makes a straddling fire discard its stale clip at commit).</summary>
    internal Task<CueExecutionStatus> FireOnGraphAsync(string cueId, CancellationToken token) =>
        _cueGraph.FireAsync(cueId, token).AsTask();

    /// <summary>Fires several cues together with a coordinated start - the fire-time counterpart of the seek/pause
    /// barriers (NXT-04 start skew / old-engine <c>FireGroupAsync</c> parity). Every cue's clip opens
    /// <em>concurrently</em> instead of each open serializing behind the previous cue's start, so a simultaneous
    /// cue group (the UI's coordinated trigger step) starts together rather than staggered by the sum of the opens
    /// - the cue graph is thread-safe for concurrent fires. All share ONE cancellation source, so a
    /// STOP/LOAD/DISPOSE aborts the whole group. Returns the per-cue statuses in
    /// input order (a cancelled cue reports <see cref="CueExecutionStatus.Failed"/>). Runs OFF the serial
    /// dispatcher (NXT-03) and holds the fire-lock for the whole group so no GO/fire interleaves.</summary>
    public Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        ArgumentNullException.ThrowIfNull(cueIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.FireCuesAsync(cueIds);
    }

    /// <summary>GO - fires the next armed and enabled cue in <paramref name="groupId"/> after the cursor. A
    /// disabled or unarmed cue is skipped (never fired); the cursor advances only when the chosen cue actually
    /// ran or faulted, so a cue that was momentarily not fireable can still be reached by a later GO (NXT-07).</summary>
    public Task<CueExecutionStatus> GoAsync(string groupId = DefaultGroup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fires.GoAsync(groupId);
    }

    /// <summary>GO's cue selection (dispatcher): the next armed+enabled cue in <paramref name="groupId"/> after
    /// the group's cursor, plus the show generation it was read under (so the matching cursor advance can no-op
    /// when a reload swapped the show in between).</summary>
    internal Task<(CueDefinition? Next, int Generation)> SelectNextGoCueAsync(string groupId) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            var next = _cueGraph.Cues
                .Where(c => (c.GroupId ?? DefaultGroup) == groupId && c.Number > group.LastFiredNumber
                            && c.Armed && c.Enabled)
                .OrderBy(c => c.Number)
                .FirstOrDefault();
            return Task.FromResult((next, _showGeneration));
        });

    /// <summary>GO's cursor advance (dispatcher). A no-op when <paramref name="generation"/> no longer matches -
    /// a reload swapped the show between selection and advance, and the fresh show's cursor must not inherit the
    /// old one's progress (the pre-split code got the same outcome by writing to the orphaned group).</summary>
    internal Task AdvanceGoCursorAsync(string groupId, int number, int generation) =>
        InvokeAsync(() =>
        {
            if (_showGeneration == generation)
                GetOrAddGroup(groupId).LastFiredNumber = number;
            return Task.CompletedTask;
        });

    /// <summary>Seeks the active clip on <paramref name="groupId"/> (coordinated A/V seek).</summary>
    public Task SeekAsync(TimeSpan position, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.Active is { } active)
            {
                // SeekCoordinated pauses+seeks but does NOT resume, so preserve the pre-seek play state: a
                // scrub while playing must keep playing, not freeze. Without this the clip is left paused
                // (IsRunning=false) after every seek, and the media-player deck's poll reads that as "ended"
                // and tears the deck down - i.e. seek "stops playback" (matches SeekManyAsync's resume).
                var wasRunning = active.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                SeekCoordinatedRestoringPlayState(active.Player, position, group, masterBeforeSeek, resume: wasRunning);
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// Coordinated seek that can never strand the clip paused: <c>SeekCoordinated</c> pauses BEFORE it
    /// seeks, so a decode/demux fault thrown by the seek (observed live: <c>avcodec_send_packet</c>
    /// EINVAL on some codecs) used to skip the resume AND the discontinuity mark - the clip sat frozen
    /// with no error shown, the deck's poll read it as ended, and every later seek failed the same way.
    /// On failure this restores the pre-seek play state (best effort - the demux stays wherever the
    /// failed seek left it) and still marks the discontinuity (the source position may have partially
    /// moved), THEN rethrows so the caller's task surfaces the fault.
    /// </summary>
    private static void SeekCoordinatedRestoringPlayState(
        S.Media.Players.MediaPlayer player,
        TimeSpan position,
        TransportGroup group,
        TimeSpan masterBeforeSeek,
        bool resume)
    {
        try
        {
            player.SeekCoordinated(position);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"ShowSession: coordinated seek to {position} failed; restoring play state");
            try
            {
                if (resume && !player.IsRunning)
                    player.Play();
            }
            catch (Exception resumeEx)
            {
                MediaDiagnostics.LogError(resumeEx, "ShowSession: resume after a failed seek also failed");
            }
            group.Timeline.MarkDiscontinuity(masterBeforeSeek);
            throw;
        }

        if (resume)
            player.Play();
        group.Timeline.MarkDiscontinuity(masterBeforeSeek); // source jumps; master stays monotonic (NXT-04)
    }

    /// <summary>Seeks several groups together behind one shared epoch - the group-seek barrier (NXT-04 /
    /// old-engine <c>group_seek_barrier</c> parity). Every target group is paused first so its clock freezes,
    /// each is seeked (coordinated), then the ones that were running resume together - so a multi-cue seek lands
    /// atomically instead of each group seeking (and drifting while the others keep advancing) in turn. Runs as
    /// one dispatcher operation, so no other transport command interleaves between the seeks. Groups with no
    /// active clip are skipped; a repeated group id just re-seeks its one active player (last position wins).</summary>
    public Task SeekManyAsync(IReadOnlyList<(string GroupId, TimeSpan Position)> seeks)
    {
        ArgumentNullException.ThrowIfNull(seeks);
        if (seeks.Count == 0)
            return Task.CompletedTask;
        return InvokeAsync(() =>
        {
            // 1) Freeze every target's clock (shared epoch) so a slow demux seek on one group can't let another
            //    group's playhead run on past it. Remember which were running so paused cues stay paused.
            var targets = new List<(
                TransportGroup Group,
                S.Media.Players.MediaPlayer Player,
                TimeSpan Position,
                bool Resume,
                TimeSpan MasterBeforeSeek)>(seeks.Count);
            List<Exception>? errors = null;
            foreach (var (groupId, position) in seeks)
            {
                var group = GetOrAddGroup(groupId);
                if (group.Active is not { } active)
                    continue;
                var wasRunning = active.Player.IsRunning;
                var masterBeforeSeek = group.Timeline.GetSnapshot().MasterTime;
                if (wasRunning)
                {
                    try
                    {
                        active.Player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                    }
                    catch (Exception ex)
                    {
                        // Skip this group's seek (its clock never froze) but keep the barrier for the rest.
                        MediaDiagnostics.LogError(ex, $"ShowSession: group seek pause failed for group '{groupId}'; skipping its seek");
                        (errors ??= []).Add(ex);
                        continue;
                    }
                }
                targets.Add((group, active.Player, position, wasRunning, masterBeforeSeek));
            }

            // 2) Seek all with clocks frozen, then 3) release the running ones together from the shared epoch.
            // A failing seek must not break the barrier: the other groups still seek, and EVERY paused group
            // still resumes (a faulted one from its pre-seek position) - a fault used to leave every
            // not-yet-seeked group stranded paused with no error surfaced.
            foreach (var (_, player, position, _, _) in targets)
            {
                try
                {
                    player.SeekCoordinated(position);
                }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, $"ShowSession: group seek to {position} failed; the clip resumes from its pre-seek position");
                    (errors ??= []).Add(ex);
                }
            }
            foreach (var (group, player, _, resume, masterBeforeSeek) in targets)
            {
                if (resume)
                {
                    try
                    {
                        player.Play();
                    }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogError(ex, "ShowSession: group seek resume failed");
                        (errors ??= []).Add(ex);
                    }
                }
                group.Timeline.MarkDiscontinuity(masterBeforeSeek); // all masters preserve the shared pre-seek epoch
            }

            if (errors is not null)
            {
                if (errors.Count == 1)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
                throw new AggregateException("One or more group seeks failed (play state was restored).", errors);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Soft-stops and releases the active clip on <paramref name="groupId"/>. The cue's configured
    /// fade-out is used, falling back to the legacy HaPlay 750 ms stop fade. Cancels any in-flight cue fire first
    /// so STOP never waits behind a long pre-wait/open (NXT-03). The fade ramp itself runs OFF the serial
    /// dispatcher (NXT-18) - only short gain/opacity steps and the final release marshal onto it - so other
    /// commands (pause, seek, load, a following GO's commit) never queue behind a long configured fade. The
    /// returned task still completes only after the clip is released ("stopped means stopped").</summary>
    /// <param name="fade">When true (the cue Stop/Panic default) the group's audio + layers ramp to silence over
    /// its <see cref="ShowClipBinding.FadeOut"/> or <see cref="SoftStopFadeDuration"/> first; when false the clip is
    /// cut immediately (the media-player deck stops hard - it has no soft-stop fade).</param>
    public Task StopAsync(string groupId = DefaultGroup, bool fade = true)
    {
        _fires.CancelActiveFire();
        return StopGroupsCoreAsync(() => [GetOrAddGroup(groupId)], fade);
    }

    /// <summary>Soft-stops every active transport group together (HaPlay Stop/Panic parity).</summary>
    public Task StopAllAsync()
    {
        _fires.CancelActiveFire();
        return StopGroupsCoreAsync(() => _groups.Values.Where(group => group.Active is not null).ToArray(), fade: true);
    }

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is the active clip (per-cue stop /
    /// cancel - the GUI's <c>CancelCueCallback</c>). No-op when that cue isn't currently playing.</summary>
    public Task StopCueAsync(string cueId)
    {
        _fires.CancelActiveFire();
        return StopGroupsCoreAsync(
            () => _groups.Values.Where(group => group.Active?.Spec.Id == cueId).ToArray(), fade: true);
    }

    /// <summary>What one stop targeted, captured on the dispatcher at claim time: the group, the clip that was
    /// active THEN (the only clip this stop may release), and - when the fade claim succeeded - its ramp.</summary>
    private sealed record StopClaim(TransportGroup Group, IArmedClip? Clip, GroupFade? Fade);

    /// <summary>The shared stop path (NXT-18): resolves the target groups and claims their fades ON the
    /// dispatcher, ramps OFF it (<see cref="RunStopFadeAsync"/>), then releases each claimed clip back ON the
    /// dispatcher - identity-guarded, so a cue fired DURING the fade survives (a stop only releases the clip it
    /// saw at claim time; the previous on-dispatcher implementation got the same outcome by queuing the fire's
    /// commit behind the whole fade). A group whose fade claim lost to an in-flight natural fade-out skips the
    /// ramp (that fade task owns the levels) but is still released here - a STOP preempts a natural fade.
    /// <paramref name="selectGroups"/> runs on the dispatcher.</summary>
    private async Task StopGroupsCoreAsync(Func<IReadOnlyList<TransportGroup>> selectGroups, bool fade)
    {
        var claims = await InvokeAsync(() =>
        {
            var groups = selectGroups();
            var list = new List<StopClaim>(groups.Count);
            foreach (var group in groups)
            {
                var clip = group.Active;
                GroupFade? groupFade = null;
                if (fade && clip is not null && group.TryBeginFadeOut(clip.Player))
                {
                    groupFade = new GroupFade(
                        group,
                        clip,
                        group.ActiveBinding?.FadeOut is { } configured && configured > TimeSpan.Zero
                            ? configured
                            : SoftStopFadeDuration,
                        group.ActiveAudioScale,
                        group.ActiveLayer?.Opacity ?? 0f,
                        group.ActiveRouteTargets);
                }

                list.Add(new StopClaim(group, clip, groupFade));
            }

            return Task.FromResult<IReadOnlyList<StopClaim>>(list);
        }).ConfigureAwait(false);

        var fades = claims.Where(c => c.Fade is not null).Select(c => c.Fade!).ToArray();
        if (fades.Length > 0)
            await RunStopFadeAsync(fades).ConfigureAwait(false);

        try
        {
            await InvokeAsync(async () =>
            {
                foreach (var claim in claims)
                    if (claim.Clip is not null && ReferenceEquals(claim.Group.Active, claim.Clip))
                        await ReplaceActiveAsync(claim.Group, null, [], []).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed mid-stop - disposal releases every clip itself.
        }
    }

    /// <summary>Ramps the claimed stop fades to silence OFF the dispatcher (an awaited <see cref="FadeRamp"/>):
    /// each step marshals one short gain/opacity commit onto it, so the serial loop is never parked for the
    /// fade duration (NXT-18). All fades advance from one clock so Panic fades groups concurrently - each
    /// computes its own level from its own duration. Exits early when every claimed clip has been replaced
    /// (nothing left to fade).</summary>
    private async Task RunStopFadeAsync(IReadOnlyList<GroupFade> fades)
    {
        var maxDuration = TimeSpan.Zero;
        foreach (var fade in fades)
            if (fade.Duration > maxDuration)
                maxDuration = fade.Duration;
        if (maxDuration <= TimeSpan.Zero)
            return;

        try
        {
            await FadeRamp.RunAsync(FadeStepInterval, CancellationToken.None, elapsed => InvokeAsync(() =>
            {
                var applied = false;
                foreach (var fade in fades)
                {
                    if (!ReferenceEquals(fade.Group.Active, fade.Clip))
                        continue; // replaced/ended during the fade - leave the new clip alone
                    fade.Group.ApplyFadeLevel(
                        fade.Clip.Player, fade.RouteTargets, fade.StartAudioScale, fade.StartOpacity,
                        FadeRamp.LevelDown(elapsed, fade.Duration));
                    applied = true;
                }

                return Task.FromResult(!applied || elapsed >= maxDuration);
            })).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // session disposed mid-fade - disposal owns the teardown
        }
    }

    private sealed record GroupFade(
        TransportGroup Group,
        IArmedClip Clip,
        TimeSpan Duration,
        float StartAudioScale,
        float StartOpacity,
        IReadOnlyList<AudioRouteTarget> RouteTargets);

    /// <summary>Pauses or resumes the active clip on <paramref name="groupId"/> - a seamless toggle (codec
    /// pipelines are not flushed, so resume continues from the same frame, matching the GUI engine's
    /// <c>SkipFlush</c> pause). On pause the player's playhead - and therefore the group's session clock +
    /// transport-snapshot position - freezes; resume continues from there (the playback-clock freeze
    /// contract). No-op when the group has no active clip.</summary>
    public Task SetPausedAsync(bool paused, string groupId = DefaultGroup) =>
        InvokeAsync(() =>
        {
            var group = GetOrAddGroup(groupId);
            if (group.Active is { } active)
            {
                group.PausedByHost = paused; // end-monitor stall detection must not read a host pause as EOF
                // Announce the state change BEFORE applying it: resume's Play() prefills + starts audio
                // hardware, holding IsRunning=false for a while - lock-free snapshot consumers (the deck's
                // 250 ms end poll) key their debounce off the generation, so bumping it up front resets
                // their window at op START instead of only after the (potentially slow) apply.
                group.Timeline.MarkDiscontinuity();
                if (paused)
                    active.Player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                else
                    active.Player.Play();
                group.Timeline.MarkDiscontinuity(); // rate/state change re-anchors the contract (NXT-04)
            }

            return Task.CompletedTask;
        });

    /// <summary>Pauses or resumes EVERY active transport group together - the all-groups form the UI drives, and
    /// the pause parity of <see cref="StopAllAsync"/>. The single-group <see cref="SetPausedAsync"/> only touches
    /// one group, so a multi-group cue show (cues fired onto several groups) would leave the other groups running
    /// when paused. Runs as one dispatcher operation so the groups toggle behind one epoch.</summary>
    public Task SetAllPausedAsync(bool paused) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active is { } active)
                {
                    group.PausedByHost = paused; // see SetPausedAsync - keeps the end monitor's stall check honest
                    group.Timeline.MarkDiscontinuity(); // announce BEFORE the slow apply (see SetPausedAsync)
                    if (paused)
                        active.Player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                    else
                        active.Player.Play();
                    group.Timeline.MarkDiscontinuity(); // see SetPausedAsync (NXT-04)
                }

            return Task.CompletedTask;
        });

    // --- queries (immutable snapshots - D5) --------------------------------------------------------

    /// <summary>An immutable snapshot of each transport group's session time, clip position, and run state.
    /// Lock-free (NXT-16): reads the published group view and pulls live position/run-state off the captured
    /// clock/player without marshaling, so it never queues behind a long-running command on the dispatcher.</summary>
    public Task<IReadOnlyList<TransportSnapshot>> SnapshotAsync() => Task.FromResult(Snapshot());

    /// <summary>The synchronous, lock-free form of <see cref="SnapshotAsync"/> - safe to call from any thread
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
            var liveDisconnected = false;
            var audioChannels = 0;
            var audioSampleRate = 0;
            var timeline = v.Group.Timeline.GetSnapshot();
            var active = v.Player is not null; // has a clip (playing/paused/frozen) - independent of the clock
            try
            {
                now = timeline.MasterTime;
                if (v.Player is { } p)
                {
                    pos = p.Position;
                    dur = p.Duration;
                    running = p.IsRunning;
                    liveDisconnected = p.IsLiveSourceExhausted; // live input dropped (router may still report running)
                    if (p.AudioSource is { } audio)
                    {
                        audioChannels = audio.Format.Channels;
                        audioSampleRate = audio.Format.SampleRate;
                    }
                }
            }
            catch { /* concurrent teardown - leave zeros for this tick */ }
            snaps[i] = new TransportSnapshot(
                v.GroupId, now, pos, dur, running, active, liveDisconnected, audioChannels, audioSampleRate,
                timeline.Generation)
            {
                Timeline = timeline,
            };
        }
        return snaps;
    }

    /// <summary>An immutable snapshot of the loaded cue definitions, ordered by cue number.</summary>
    public Task<IReadOnlyList<CueDefinition>> GetCueDefinitionsAsync()
    {
        // Lock-free (NXT-16 residue): the graph reference is volatile and CueGraph is internally locked, so
        // this UI/fire-failure query never queues behind the dispatcher (a long command would stall it).
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(_cueGraph.Cues);
    }

    /// <summary>The cue ids whose clips are currently prepared (warm) in the standby engine - a UI "ready"
    /// indicator, and how a test confirms the pre-roll ran.</summary>
    public Task<IReadOnlyList<string>> GetPreparedCueIdsAsync()
    {
        // Lock-free (NXT-16 residue): the standby engine is internally locked - no dispatcher round-trip.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult<IReadOnlyList<string>>(_standby.PreparedKeys.Select(k => k.Id).ToArray());
    }

    /// <summary>
    /// Attach a live <see cref="IVideoOutput"/> (e.g. a UI preview surface) to a loaded composition's pump - the
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
    /// composition with that id is loaded - proves the cue→clip→layer→composite path ran (headless).</summary>
    public Task<ClipCompositionRuntimeStats?> GetCompositionStatsAsync(string compositionId) =>
        InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition)
                ? composition.GetStats()
                : (ClipCompositionRuntimeStats?)null));

    /// <summary>Applies (or clears, with <see langword="null"/>) a composition's output mapping at runtime -
    /// projector keystone / multi-panel tiling. Returns false when no composition with that id is loaded.</summary>
    public Task<bool> ApplyCompositionMappingAsync(string compositionId, ClipOutputMappingSpec? mapping) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
                return Task.FromResult(false);
            composition.UpdateCompositionMapping(mapping);
            return Task.FromResult(true);
        });

    /// <summary>Shows (<paramref name="frame"/> non-null) or hides (null) a mapping-calibration test pattern on a
    /// composition - held in a top-most, full-canvas layer so the operator can align one output's warp against the
    /// live grid. The host renders the grid frame (it owns the mapping/section masking) and hands it here; the
    /// session owns the frame after this call. Returns false when the composition id is unknown.</summary>
    public Task<bool> SetCompositionTestPatternAsync(string compositionId, VideoFrame? frame) =>
        InvokeAsync(() =>
        {
            if (!_compositions.TryGetValue(compositionId, out var composition))
            {
                frame?.Dispose();
                return Task.FromResult(false);
            }

            if (frame is null)
            {
                if (_testPatternSlots.Remove(compositionId, out var slot))
                    slot.Dispose(); // removes the top layer from the composition
                return Task.FromResult(true);
            }

            var canvas = composition.CanvasFormat;
            if (!_testPatternSlots.TryGetValue(compositionId, out var existing))
            {
                existing = composition.AddLayer(
                    canvas,
                    new VideoPlacementSpec(compositionId, int.MaxValue, Placement: "stretch"));
                _testPatternSlots[compositionId] = existing;
            }

            existing.Output.Configure(canvas);
            existing.Output.Submit(frame); // Submit takes ownership of the frame
            composition.EnsurePumpStarted();
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
    private void PublishGroupViews()
    {
        _groupViews = _groups
            .Select(kv => new GroupClockView(kv.Key, kv.Value.Active?.Player, kv.Value))
            .ToArray();

        // Audio-pump view: every active clip's device-tagged routed outputs (skips default-device routes, which
        // can't be line-correlated). GetActiveAudioPumpStatsByDevice reads this lock-free.
        var pumps = new List<ActiveAudioPump>();
        foreach (var kv in _groups)
        {
            if (kv.Value.Active?.Player?.AudioRouter is not { } router)
                continue;
            foreach (var (outputId, deviceId) in kv.Value.ActiveAudioPumps)
                pumps.Add(new ActiveAudioPump(router, outputId, deviceId));
        }
        _audioPumpsView = pumps;
    }

    /// <summary>Lock-free per-device audio-pump stats (enqueued/dropped chunks) summed across the active cues'
    /// routed outputs - the audio analogue of <see cref="GetCompositionStats"/> for the outputs-panel line-health
    /// poll. Keyed by the PortAudio device id a cue routed audio to; a UI output line maps its device id into this.
    /// Reads a volatile snapshot (republished on fire/stop) then each router's own thread-safe pump stats - no
    /// dispatcher marshaling. Empty when no active cue routes device-addressed audio.</summary>
    public IReadOnlyDictionary<string, (long Enqueued, long Dropped)> GetActiveAudioPumpStatsByDevice()
    {
        var view = _audioPumpsView;
        var result = new Dictionary<string, (long Enqueued, long Dropped)>(StringComparer.Ordinal);
        foreach (var pump in view)
        {
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                var cur = result.TryGetValue(pump.DeviceId, out var v) ? v : default;
                result[pump.DeviceId] = (cur.Enqueued + st.Enqueued, cur.Dropped + st.Dropped);
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        return result;
    }

    /// <summary>Allocation-free single-device variant of <see cref="GetActiveAudioPumpStatsByDevice"/> for the
    /// per-line 1 Hz health polls (each wants exactly one device id): walks the same lock-free view and sums
    /// only matching pumps instead of building the whole dictionary per poll.</summary>
    public bool TryGetActiveAudioPumpStats(string deviceId, out (long Enqueued, long Dropped) stats)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        long enqueued = 0, dropped = 0;
        var found = false;
        foreach (var pump in _audioPumpsView)
        {
            if (!string.Equals(pump.DeviceId, deviceId, StringComparison.Ordinal))
                continue;
            try
            {
                var st = pump.Router.GetPumpStats(pump.OutputId);
                enqueued += st.Enqueued;
                dropped += st.Dropped;
                found = true;
            }
            catch (ArgumentException) { /* output retired between snapshot publish and read */ }
        }

        stats = (enqueued, dropped);
        return found;
    }

    /// <summary>Republishes the lock-free composition view after a load/dispose changes <see cref="_compositions"/>.
    /// Call on the dispatcher (the only place <see cref="_compositions"/> is mutated).</summary>
    private void PublishCompositionsView() =>
        _compositionsView = new Dictionary<string, ClipCompositionRuntime>(_compositions, StringComparer.Ordinal);

    /// <summary>Lock-free per-composition stats for a UI health poll - no dispatcher marshaling (mirrors
    /// <see cref="SnapshotAsync"/>). Reads a volatile snapshot of the compositions republished on load, then the
    /// runtime's own thread-safe <c>GetStats</c>. Null when no such composition exists (or it is mid-teardown).</summary>
    public ClipCompositionRuntimeStats? GetCompositionStats(string compositionId)
    {
        if (!_compositionsView.TryGetValue(compositionId, out var runtime))
            return null;
        try { return runtime.GetStats(); }
        catch (ObjectDisposedException) { return null; } // retired between snapshot publish and read
    }

    /// <summary>Swaps a group's active clip and republishes the query view so a position/state poll always sees
    /// the new run-state without waiting behind the dispatcher.</summary>
    private async ValueTask ReplaceActiveAsync(
        TransportGroup group, IArmedClip? clip, IReadOnlyList<ClipAudioOutput> outputs,
        IReadOnlyList<PlacedLayer> layers, IReadOnlyList<IDisposable>? subtitleAttachments = null,
        ShowClipBinding? binding = null)
    {
        await group.ReplaceAsync(clip, outputs, layers, subtitleAttachments, binding).ConfigureAwait(false);
        PublishGroupViews();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _fires.CancelActiveFire(); // unblock the dispatcher so disposal isn't stuck behind a long in-flight fire (NXT-03)

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
        await _voicePlayer.ReleaseAllAsync().ConfigureAwait(false);
        foreach (var group in _groups.Values)
            await group.DisposeAsync().ConfigureAwait(false);
        _groups.Clear();
        PublishGroupViews();
        _testPatternSlots.Clear(); // slots are owned by their compositions (disposed below); drop stale refs
        foreach (var composition in _compositions.Values)
            composition.Dispose();
        _compositions.Clear();
        PublishCompositionsView(); // drop the health-poll view's references to the retired compositions
        await _standby.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One transport group: its session clock (D4), active clip, its audio outputs (D11 - first is
    /// master, rest auto-slave), and the composition layer the active clip's video feeds (all released on
    /// clip replace).</summary>
    private sealed class TransportGroup : IAsyncDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
        public TransportTimeline Timeline { get; }
        public IArmedClip? Active { get; private set; }
        private IReadOnlyList<ClipAudioOutput> _outputs = [];
        private IReadOnlyList<IDisposable> _subtitleAttachments = [];
        private IReadOnlyList<PlacedLayer> _layers = [];
        private CancellationTokenSource? _clipWorkCts;
        private ShowClipBinding? _activeBinding;
        private IReadOnlyList<AudioRouteTarget> _activeRouteTargets = [];
        // Device-tagged routed audio outputs of the active clip (OutputId → the PortAudio device it plays on),
        // for the per-line audio-health poll. Only device-addressed routes are tracked (default-device routes
        // can't be line-correlated). Set after the clip commits; reset on replacement.
        private IReadOnlyList<(string OutputId, string DeviceId)> _activeAudioPumps = [];
        private float _activeAudioScale = 1f;
        private int _fadeOutStarted;
        public int LastFiredNumber { get; set; } = int.MinValue;

        public TransportGroup() => Timeline = new TransportTimeline(Clock);

        /// <summary>True while the host holds this group paused (Set(All)PausedAsync). The end monitor's
        /// stall-at-EOF check reads it so a paused clip's stopped clock is never mistaken for a natural end.
        /// Dispatcher-confined (set by the pause commands, read by the monitor's dispatcher-marshalled checks);
        /// cleared when the active clip is replaced.</summary>
        public bool PausedByHost { get; set; }

        public ShowClipBinding? ActiveBinding => _activeBinding;
        public IReadOnlyList<(string OutputId, string DeviceId)> ActiveAudioPumps => _activeAudioPumps;

        /// <summary>Records the active clip's device-tagged routed audio outputs (for the line-health poll). Call
        /// on the dispatcher after the clip's outputs are attached.</summary>
        public void SetActiveAudioPumps(IReadOnlyList<(string OutputId, string DeviceId)> pumps) =>
            _activeAudioPumps = pumps as (string, string)[] ?? pumps.ToArray();

        /// <summary>Replaces the tracked audio outputs for a LIVE rebuild (hot add/remove of a deck output).
        /// Returns the previous set so the caller releases each per its ownership AFTER removing it from the
        /// router - releasing an owned sink while its route still exists would dangle.</summary>
        public IReadOnlyList<ClipAudioOutput> SwapAudioOutputs(IReadOnlyList<ClipAudioOutput> newOutputs)
        {
            var old = _outputs;
            _outputs = newOutputs;
            return old;
        }

        /// <summary>Updates the active route targets (OutputId → target gain) after a rebuild so the fade/gain
        /// ride rides the NEW output set. Keeps the binding and current audio scale.</summary>
        public void SetActiveRouteTargets(IReadOnlyList<AudioRouteTarget> routeTargets) =>
            _activeRouteTargets = routeTargets.ToArray();
        // Every placement fades on one ramp, so the primary (first) layer's opacity is representative for the
        // cross-fade opacity readback.
        public ClipCompositionRuntime.IPlacedClipLayer? ActiveLayer => _layers.Count > 0 ? _layers[0].Slot : null;
        public IReadOnlyList<AudioRouteTarget> ActiveRouteTargets => _activeRouteTargets;
        public float ActiveAudioScale => _activeAudioScale;

        /// <summary>Hands the group the cancellation source for the active clip's background work (the fade-in
        /// ramp + the end-of-clip loop/stop/freeze monitor). Cancelled when the clip is replaced.</summary>
        public void SetClipWorkCts(CancellationTokenSource cts) => _clipWorkCts = cts;

        public void SetActiveFadeMetadata(
            ShowClipBinding binding,
            IReadOnlyList<AudioRouteTarget> routeTargets,
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
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float scale)
        {
            if (Active?.Player != player)
                return;
            _activeAudioScale = Math.Clamp(scale, 0f, 1f);
            if (player.AudioRouter is { } router && player.AudioSourceId is { } sourceId)
                foreach (var target in routeTargets)
                {
                    if (target.Route is { HasGainMatrix: true } matrixRoute)
                        router.ApplyMatrix(
                            sourceId, target.OutputId,
                            matrixRoute.ToGainMatrix(target.TargetGain * _activeAudioScale));
                    else
                        router.SetRouteGain(sourceId, target.OutputId, target.TargetGain * _activeAudioScale);
                }
        }

        public void ApplyFadeLevel(
            S.Media.Players.MediaPlayer player,
            IReadOnlyList<AudioRouteTarget> routeTargets,
            float startAudioScale,
            float startOpacity,
            float scale)
        {
            if (Active?.Player != player)
                return;
            scale = Math.Clamp(scale, 0f, 1f);
            ApplyAudioScale(player, routeTargets, startAudioScale * scale);
            // All of the clip's composition layers fade together on the one ramp.
            var opacity = startOpacity * scale;
            foreach (var placed in _layers)
                placed.Slot.Opacity = opacity;
        }

        /// <summary>Live-repositions the active clip's composition layer identified by
        /// <paramref name="compositionId"/>/<paramref name="layerIndex"/> (the placement the GUI edited). Falls back
        /// to the primary layer when the clip has a single placement. False if the clip has no matching layer.</summary>
        public bool UpdateActivePlacement(string compositionId, int layerIndex, VideoPlacementSpec spec)
        {
            if (_layers.Count == 0)
                return false;
            var target = _layers.FirstOrDefault(
                l => l.CompositionId == compositionId && l.LayerIndex == layerIndex);
            // A single-placement clip predates per-placement addressing: update it regardless of the passed key.
            if (target.Slot is null && _layers.Count == 1)
                target = _layers[0];
            if (target.Slot is null)
                return false;
            target.Slot.UpdatePlacement(spec);
            return true;
        }

        public async ValueTask ReplaceAsync(
            IArmedClip? clip,
            IReadOnlyList<ClipAudioOutput> outputs,
            IReadOnlyList<PlacedLayer> layers,
            IReadOnlyList<IDisposable>? subtitleAttachments = null,
            ShowClipBinding? binding = null)
        {
            // Stop the displaced clip's background work (fade ramp + end-of-clip monitor) before anything else.
            _clipWorkCts?.Cancel();
            _clipWorkCts?.Dispose();
            _clipWorkCts = null;
            _activeBinding = null;
            _activeRouteTargets = [];
            _activeAudioPumps = [];
            _activeAudioScale = 1f;
            PausedByHost = false;
            Volatile.Write(ref _fadeOutStarted, 0);

            Clock.SetReference(clip is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(clip.Player.PlayClock));

            if (clip is null)
            {
                Timeline.Clear();
            }
            else
            {
                var trimStart = binding?.StartOffset ?? TimeSpan.Zero;
                TimeSpan? trimEnd = clip.Player.Duration > TimeSpan.Zero
                    ? clip.Player.Duration - (binding?.EndOffset ?? TimeSpan.Zero)
                    : null;
                if (trimEnd is { } knownEnd && knownEnd < trimStart)
                    trimEnd = trimStart;
                Timeline.BindSource(
                    clip.Player.PlayClock.AsPlayhead(),
                    trimStart,
                    trimEnd,
                    isLive: clip.Player.IsLive);
            }

            var oldActive = Active;
            var oldOutputs = _outputs;
            var oldLayers = _layers;
            var oldSubtitles = _subtitleAttachments;

            Active = clip;
            _outputs = outputs;
            _layers = layers;
            _subtitleAttachments = subtitleAttachments ?? [];

            // Release the displaced clip BEFORE its outputs (the player feeds them). Runs on the serial
            // dispatcher, so the brief Active=new / old-not-yet-released window is never observed.
            foreach (var attachment in oldSubtitles)
                attachment.Dispose();
            foreach (var placed in oldLayers)
                placed.Slot.Dispose();
            if (oldActive is not null)
                await oldActive.ReleaseAsync().ConfigureAwait(false);
            foreach (var output in oldOutputs)
                ReleaseClipAudioOutput(output);
        }

        public ValueTask DisposeAsync() => ReplaceAsync(null, [], []);
    }

    private sealed class PlayheadPlaybackClock(IPlayhead playhead) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playhead.CurrentPosition;
        public bool IsAdvancing => playhead.IsRunning;
    }

}
