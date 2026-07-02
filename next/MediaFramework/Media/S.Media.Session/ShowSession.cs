using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>Immutable per-group transport snapshot — a query result, never mutated by the caller (D5).</summary>
/// <param name="IsActive">True when the group currently holds a clip (playing, paused, or frozen) — the reliable
/// "is this cue still up" signal. Distinct from <paramref name="IsRunning"/> (the clock is advancing), which a
/// video-only held/text clip can report <c>false</c> for while still on screen.</param>
public sealed record TransportSnapshot(
    string GroupId,
    TimeSpan SessionTime,
    TimeSpan ClipPosition,
    TimeSpan ClipDuration,
    bool IsRunning,
    bool IsActive = false,
    bool LiveSourceDisconnected = false,
    int AudioChannels = 0,
    int AudioSampleRate = 0);

/// <summary>A soundboard voice's playhead — for the UI's per-tile progress/countdown.</summary>
public readonly record struct VoiceProgress(string VoiceId, TimeSpan Position, TimeSpan Duration);

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
    // Lock-free view of the compositions for the UI health poll: republished (on the dispatcher) whenever
    // _compositions changes, so GetCompositionStats can read it — and the runtime's own thread-safe GetStats —
    // off any thread without marshaling (mirrors _groupViews / SnapshotAsync).
    private volatile IReadOnlyDictionary<string, ClipCompositionRuntime> _compositionsView =
        new Dictionary<string, ClipCompositionRuntime>(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipCompositionRuntime.LayerSlot> _testPatternSlots = new(StringComparer.Ordinal);
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
    // Host audio-output factory (route deviceId, format) → a borrowed sink for that device, or null to let the
    // IAudioBackend create it. Mirrors _videoOutputFactory: a returned lease with DisposeOutputOnRuntimeDispose
    // = false is NEVER disposed by the session (the host owns it — e.g. an NDI sender's audio side sharing the
    // carrier that also emits the composition's video). Null ⇒ every route uses the backend device.
    private readonly Func<string, AudioFormat, ClipAudioOutputLease?>? _audioOutputFactory;
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
    // Voice opens in flight (NXT-19): voiceId → the open's claim CTS, published so a stop / re-fire / dispose
    // preempts the OFF-dispatcher open before it commits. Owned by the dispatcher; a canceller only Cancel()s —
    // the open flow that created the CTS is the one that disposes it (the blocked open still holds its token).
    private readonly Dictionary<string, CancellationTokenSource> _pendingVoiceOpens = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    // Lock-free query view (NXT-16): a volatile snapshot of each group's clock + active player, republished on
    // the dispatcher whenever the group set or active clip changes. Snapshot() reads THIS and pulls live
    // (thread-safe) position/duration/run-state off the captured references, so a position/state poll never
    // serializes behind a long-running command on the dispatcher.
    private volatile IReadOnlyList<GroupClockView> _groupViews = [];

    private sealed record GroupClockView(string GroupId, SessionClock Clock, S.Media.Players.MediaPlayer? Player);

    // Lock-free per-device audio-pump view for the outputs-panel line-health poll (the audio analogue of
    // _compositionsView): republished on the dispatcher whenever the active clips change, so a UI poll sums a
    // routed line's enqueued/dropped chunks off-thread without marshaling. Keyed to the device the cue routed to.
    private volatile IReadOnlyList<ActiveAudioPump> _audioPumpsView = [];

    private readonly record struct ActiveAudioPump(AudioRouter Router, string OutputId, string DeviceId);

    // A clip's attached audio output plus its ownership. The session disposes it on clip replace only when
    // DisposeOnRelease (a backend-created device it owns); a host lease (e.g. an NDI carrier's audio) is
    // BORROWED — never disposed, only its Release hook is invoked so the host can drop its reference.
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
    /// logged and skipped so the clip still plays on its remaining routes — instead of one bad device faulting
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
        string CompositionId, int LayerIndex, ClipCompositionRuntime.LayerSlot Slot);

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
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null,
        Func<string, AudioFormat, ClipAudioOutputLease?>? audioOutputFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _audioBackend = audioBackend;
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

    /// <summary>Raised (with the cue id) when a transport-group clip reaches its NATURAL end and is released by
    /// the end-of-clip machinery — the trimmed/duration out-point's plain stop, or a natural fade-out completing.
    /// Never raised for an operator stop/cancel/reload, a loop (keeps running), or a freeze (stays up) — it is
    /// the host's cue auto-follow trigger (the legacy engine's <c>NaturalEnd</c>). Raised from the session
    /// dispatcher; marshal in the handler.</summary>
    public event Action<string>? ClipNaturallyEnded;

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
        // dispatcher's AsyncLocal identity into the warm task's continuations — a future InvokeAsync from such a
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
            // fan onto several — the group + generation are all the pre-open setup needs.
            return Task.FromResult((generation, group: grp));
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
                // A cue may place its ONE decoded source onto several composition layers at once — PiP, the same
                // feed in two regions, or mirrored to a second canvas. Fan the player's video out to each: one
                // LayerSlot per placement, all fed by the same VideoRouter input through a unique output id.
                // PlacementResolver scales source pixels into the normalized destination rectangle; passing the
                // negotiated source format (not the canvas) keeps a clip smaller than the canvas correctly sized
                // rather than identity-stretched.
                //
                // NXT-04: clock-master each DISTINCT composition pump once to this group's SessionClock, so it
                // composites at the group cadence and selects frames against the clip's playhead instead of
                // free-running (latest-frame). The group clock is stable across cues (it re-references each active
                // clip and survives replacement), so the once-only master stays valid while the per-clip playhead
                // updates each fire. ShowSession feeds raw source frames (no retiming) — playhead and frame PTS
                // share the source timebase. Live sources keep the free-run "latest frame" path (live-sync is
                // separate NXT-04 work). The same composition placed twice (PiP) is mastered only on its first slot.
                var mastered = new HashSet<string>(StringComparer.Ordinal);
                var fanoutIndex = 0;
                foreach (var placement in binding.GetPlacements())
                {
                    if (!_compositions.TryGetValue(placement.CompositionId, out var comp))
                        continue;
                    var slot = comp.AddLayer(
                        videoSource.Format,
                        BuildVideoPlacementSpec(placement.CompositionId, placement.LayerIndex, placement.Placement));
                    player.AttachVideoOutput(slot.Output, id: $"comp{fanoutIndex++}"); // unique id ⇒ router fans out
                    if (!player.IsLive && mastered.Add(placement.CompositionId))
                        comp.SetClockMaster(new SessionClockMaster(group.Clock), player.PlayClock);
                    layers.Add(new PlacedLayer(placement.CompositionId, placement.LayerIndex, slot));
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
                            continue; // one un-openable device must not fault the whole cue — play the rest
                        routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                        if (route.DeviceId is { } clipDevice)
                            audioPumps.Add((outputId, clipDevice));
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
                            overlay, () => player.Position, nextLayerIndex++));
                    }
                }
            }

            armed.Start();
            await ReplaceActiveAsync(group, armed, outputs, layers, subtitleAttachments).ConfigureAwait(false);
            group.SetActiveFadeMetadata(binding, routeTargets, fadeIn ? 0f : 1f);
            // Publish the device-tagged audio outputs for the line-health poll (ReplaceActiveAsync republished
            // the group views before these were set, so refresh once more now they're known).
            group.SetActiveAudioPumps(audioPumps);
            PublishGroupViews();

            // Background per-clip work — the fade-in ramp + the end-of-clip (loop/trim-out/freeze) monitor —
            // shares one cancellation, cancelled when the clip is replaced. Both gated, so a plain
            // play-to-end cue with no fade starts nothing. End handling needs a known duration (live = 0).
            var end = player.Duration - binding.EndOffset;
            var endHandling = (binding.Loop || binding.EndBehavior != ClipEndBehavior.Stop
                               || binding.EndOffset > TimeSpan.Zero || binding.FadeOut > TimeSpan.Zero
                               || binding.EndAtDuration)
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

    // NXT-24: backend device enumeration is not free (PortAudio walks the host API's device table, and a flaky
    // ALSA setup makes it worse) and the spec builder runs on EVERY fire / warm / voice. Cache the list briefly;
    // thread-safe because the fire path builds specs OFF the dispatcher.
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromSeconds(5);
    private readonly object _deviceCacheGate = new();
    private IReadOnlyList<AudioDeviceInfo>? _cachedOutputDevices;
    private long _cachedOutputDevicesAtMs;

    /// <summary>The backend's output devices, cached for <see cref="DeviceCacheTtl"/> (NXT-24) — device
    /// hot-plug is still picked up on the next refresh, but a burst of fires/warms enumerates once.</summary>
    private IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevicesCached()
    {
        var now = Environment.TickCount64;
        lock (_deviceCacheGate)
        {
            if (_cachedOutputDevices is { } cached && now - _cachedOutputDevicesAtMs < (long)DeviceCacheTtl.TotalMilliseconds)
                return cached;
        }

        var devices = _audioBackend!.EnumerateOutputDevices();
        lock (_deviceCacheGate)
        {
            _cachedOutputDevices = devices;
            _cachedOutputDevicesAtMs = now;
        }

        return devices;
    }

    /// <summary>Returns the hardware/backend nominal rate for a device. JACK devices expose their fixed
    /// graph rate here; opening PortAudio at the media's source rate would fail for 44.1 kHz media on a
    /// 48 kHz JACK graph.</summary>
    private int? ResolveBackendSampleRate(string? deviceId)
    {
        if (_audioBackend is null)
            return null;
        var devices = EnumerateOutputDevicesCached();
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

    /// <summary>Hot-detaches an output (by its lease <c>OutputId</c>) from a LIVE composition — the GUI's
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
    /// channel map/full gain matrix + gain) while it plays — the GUI's <c>UpdateActiveCueAudioRoutes</c> under the
    /// ShowSession path. Each route <c>i</c> replaces every route for the clip's <c>clip{i}</c> output, then installs
    /// either its legacy channel map or its per-cell matrix. Returns false when
    /// the cue isn't the active clip on any group. If the live clip-output count no longer matches the edited route
    /// count (a line was added/removed/muted mid-playback, which reorders the positional <c>clip{i}</c> ids), the
    /// live apply is skipped so nothing is mis-patched — that change lands cleanly on the next fire instead.</summary>
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
                    // edited routes (stable composition — the common level/channel tweak). A count change reorders
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
                            // A fully-unrouted line carries no map — nothing to re-apply. Its previously
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
                            // channel count mismatch vs the live output — lands on the next fire
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

    /// <summary>REBUILDS the active cue's audio outputs from a fresh route set while it plays — the count-change
    /// counterpart of <see cref="ApplyActiveAudioRoutesAsync"/> (which only re-applies in place for a stable
    /// count). Removes EVERY current <c>clip{i}</c> output from the router (its <c>_audio_discard</c>
    /// negotiation-lead sink stays, so the router keeps running — the clip plays on even with ZERO device
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

    /// <summary>Live-swap the active cue's held video frame — a text / still cue whose content was edited while it
    /// plays — with no reload or re-fire. Finds the cue's active clip and, if its source supports it
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
    public async Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // --- SETUP (dispatcher): stop any current preview / pending preview open, resolve the binding, claim.
        var setup = await InvokeAsync<(ClipSpec Spec, CancellationTokenSource Cts)?>(async () =>
        {
            await ReleasePreviewAsync().ConfigureAwait(false);
            if (!_clipsByCue.TryGetValue(cueId, out var binding))
                return null;
            var claim = new CancellationTokenSource();
            _previewCts = claim; // published: ReleasePreviewAsync cancels it to preempt the open
            return (BuildClipSpec(binding, "preview"), claim);
        }).ConfigureAwait(false);
        if (setup is not { } s)
            return false;

        // --- OPEN (OFF the dispatcher): the long part — the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(s.Spec, s.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // preempted by StopPreview / a replacing preview / dispose — not an error
        }

        // --- COMMIT (dispatcher): only if our claim is still the current preview.
        try
        {
            return await CommitPreviewAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit — release the orphaned clip directly.
            await armed.ReleaseAsync().ConfigureAwait(false);
            return false;
        }

        Task<bool> CommitPreviewAsync() => InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_previewCts, s.Cts) || s.Cts.IsCancellationRequested || _disposed)
            {
                await armed.ReleaseAsync().ConfigureAwait(false);
                return false;
            }

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
                StartPreviewEndMonitor(cueId, player, s.Cts.Token);
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
    }

    /// <summary>Stops the current preview, if any (the GUI's <c>StopPreview</c>) — including one still opening
    /// (NXT-19). Does not raise <see cref="PreviewEnded"/>.</summary>
    public Task StopPreviewAsync() => InvokeAsync(() => ReleasePreviewAsync().AsTask());

    private async ValueTask ReleasePreviewAsync()
    {
        // Cancel only — never Dispose the CTS here: a preempted preview open (NXT-19) may still hold its token
        // off-dispatcher. A cancelled CTS with no timer holds no unmanaged state, so GC reclaims it.
        _previewCts?.Cancel();
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
    /// <paramref name="voiceId"/>. Polyphonic across ids; re-firing the same id replaces its voice (including a
    /// still-opening one). Raises <see cref="VoiceEnded"/> at the voice's natural end. The media open runs OFF
    /// the serial dispatcher (NXT-19) — a slow open never parks transport — and
    /// <see cref="StopVoiceAsync"/>/<see cref="StopAllVoicesAsync"/>/a re-fire/dispose preempt it; a preempted
    /// fire completes without error (the voice simply never started). (Loop is a later refinement.)</summary>
    public async Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId = null, float volume = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var outputId = $"voice:{voiceId}";

        // --- SETUP (dispatcher): replace any prior voice / pending open and claim this open.
        var (spec, cts) = await InvokeAsync(async () =>
        {
            await ReleaseVoiceAsync(voiceId).ConfigureAwait(false); // re-trigger replaces the prior voice
            var targetAudioRate = ResolveBackendSampleRate(deviceId ?? _outputDeviceId);
            var clipSpec = new ClipSpec(
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
            var claim = new CancellationTokenSource();
            _pendingVoiceOpens[voiceId] = claim;
            return (clipSpec, claim);
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): the long part — the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(spec, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException;
            try
            {
                await InvokeAsync(() =>
                {
                    if (_pendingVoiceOpens.TryGetValue(voiceId, out var current) && ReferenceEquals(current, cts))
                        _pendingVoiceOpens.Remove(voiceId);
                    cts.Dispose(); // the open flow owns the claim CTS; the open is over, no one else holds the token
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // disposed mid-open — DisposeStateAsync already dropped/cancelled the pending claim
            }

            if (cancelled)
                return; // preempted by stop/re-fire/dispose — not an error, the voice just never started
            throw; // a real open failure (bad path/device) surfaces to the caller as before
        }

        // --- COMMIT (dispatcher): only if our claim is still current (not stopped/re-fired during the open).
        try
        {
            await CommitVoiceAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit — release the orphaned clip directly (the
            // standby engine is internally thread-safe; nothing registered it, so nothing else will).
            await armed.ReleaseAsync().ConfigureAwait(false);
        }

        Task CommitVoiceAsync() => InvokeAsync(async () =>
        {
            var current = _pendingVoiceOpens.TryGetValue(voiceId, out var pending) && ReferenceEquals(pending, cts);
            if (current)
                _pendingVoiceOpens.Remove(voiceId);
            if (!current || cts.IsCancellationRequested || _disposed)
            {
                cts.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                return;
            }

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
                // The claim CTS becomes the running voice's CTS (cancels the end monitor on release).
                _voices[voiceId] = new VoiceHandle(armed, outputs, outputId, cts);
                StartVoiceEndMonitor(voiceId, player, cts.Token);
            }
            catch
            {
                foreach (var output in outputs)
                    (output as IDisposable)?.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                cts.Dispose();
                throw;
            }
        });
    }

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask());

    /// <summary>Stops every soundboard voice (the GUI's StopAllSounds) — including any still opening (NXT-19).</summary>
    public Task StopAllVoicesAsync() =>
        InvokeAsync(async () =>
        {
            foreach (var id in _voices.Keys.Concat(_pendingVoiceOpens.Keys).Distinct().ToArray())
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

    /// <summary>Per-voice playhead (id, position, duration) for every currently-playing soundboard voice — the
    /// source for the UI's per-tile progress/countdown. Empty when nothing is playing.</summary>
    public Task<IReadOnlyList<VoiceProgress>> GetVoiceProgressAsync() =>
        InvokeAsync<IReadOnlyList<VoiceProgress>>(() =>
        {
            var snaps = new VoiceProgress[_voices.Count];
            var i = 0;
            foreach (var (id, v) in _voices)
            {
                TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;
                try { pos = v.Clip.Player.Position; dur = v.Clip.Player.Duration; }
                catch { /* concurrent teardown — zeros for this tick */ }
                snaps[i++] = new VoiceProgress(id, pos, dur);
            }
            return Task.FromResult<IReadOnlyList<VoiceProgress>>(snaps);
        });

    private async ValueTask ReleaseVoiceAsync(string voiceId)
    {
        // Preempt a still-opening voice (NXT-19): cancel its claim so the off-dispatcher open aborts and its
        // commit is refused. Only Cancel here — the open flow that created the CTS disposes it (it still holds
        // the token inside the blocked open).
        if (_pendingVoiceOpens.Remove(voiceId, out var pending))
            pending.Cancel();

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
    /// fires it without awaiting so the opens run in the background while the current cue plays. Only the
    /// cue/group state read marshals onto the dispatcher — the standby refresh (which OPENS media) runs OFF
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
            // best-effort pre-roll — a failed warm just means the next GO opens on demand
        }
    }

    private static readonly TimeSpan FadeStepInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Ramps each route's gain from silence up to its configured target over <paramref name="duration"/>
    /// (the clip was attached silent). The ramp fraction multiplies each route's <c>TargetGain</c>, so a route
    /// set below or above unity fades up to exactly that level rather than to a hardcoded 1.0 (NXT-07). A
    /// background poll that marshals each gain step onto the dispatcher; cancelled when the clip is replaced.</summary>
    private void StartFadeIn(string groupId, S.Media.Players.MediaPlayer player,
        IReadOnlyList<AudioRouteTarget> routes, TimeSpan duration, CancellationToken ct)
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
        IReadOnlyList<AudioRouteTarget> routeTargets,
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
                                {
                                    var endedCueId = clip.Spec.Id;
                                    await ReplaceActiveAsync(group, null, [], []).ConfigureAwait(false);
                                    ClipNaturallyEnded?.Invoke(endedCueId); // natural fade-out completed
                                }
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

    /// <summary>Fires several cues together with a coordinated start — the fire-time counterpart of the seek/pause
    /// barriers (NXT-04 start skew / old-engine <c>FireGroupAsync</c> parity). Every cue's clip opens
    /// <em>concurrently</em> instead of each open serializing behind the previous cue's start, so a simultaneous
    /// cue group (the UI's coordinated trigger step) starts together rather than staggered by the sum of the opens
    /// — the cue graph is thread-safe for concurrent fires. All share ONE cancellation source published to
    /// <see cref="_activeFireCts"/>, so a STOP/LOAD/DISPOSE aborts the whole group. Returns the per-cue statuses in
    /// input order (a cancelled cue reports <see cref="CueExecutionStatus.Failed"/>). Runs OFF the serial
    /// dispatcher (NXT-03) and holds the fire-lock for the whole group so no GO/fire interleaves.</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        ArgumentNullException.ThrowIfNull(cueIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cueIds.Count == 0)
            return [];
        if (cueIds.Count == 1)
            return [await FireCueAsync(cueIds[0]).ConfigureAwait(false)];

        await _fireLock.WaitAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try
        {
            var fires = new Task<CueExecutionStatus>[cueIds.Count];
            for (var i = 0; i < cueIds.Count; i++)
                fires[i] = FireForGroupAsync(cueIds[i], cts.Token);
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        finally
        {
            _activeFireCts = null;
            _fireLock.Release();
        }
    }

    /// <summary>One cue's fire within a <see cref="FireCuesAsync"/> group: maps cancellation to a non-throwing
    /// <see cref="CueExecutionStatus.Failed"/> (so one cancelled cue doesn't fault the whole <c>WhenAll</c>); a
    /// <see cref="CueFaultPolicy.StopShow"/> fault still propagates, matching single-cue fire.</summary>
    private async Task<CueExecutionStatus> FireForGroupAsync(string cueId, CancellationToken token)
    {
        try { return await _cueGraph.FireAsync(cueId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; }
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
            if (GetOrAddGroup(groupId).Active is { } active)
            {
                // SeekCoordinated pauses+seeks but does NOT resume, so preserve the pre-seek play state: a
                // scrub while playing must keep playing, not freeze. Without this the clip is left paused
                // (IsRunning=false) after every seek, and the media-player deck's poll reads that as "ended"
                // and tears the deck down — i.e. seek "stops playback" (matches SeekManyAsync's resume).
                var wasRunning = active.Player.IsRunning;
                active.Player.SeekCoordinated(position);
                if (wasRunning)
                    active.Player.Play();
            }
            return Task.CompletedTask;
        });

    /// <summary>Seeks several groups together behind one shared epoch — the group-seek barrier (NXT-04 /
    /// old-engine <c>group_seek_barrier</c> parity). Every target group is paused first so its clock freezes,
    /// each is seeked (coordinated), then the ones that were running resume together — so a multi-cue seek lands
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
            var targets = new List<(S.Media.Players.MediaPlayer Player, TimeSpan Position, bool Resume)>(seeks.Count);
            foreach (var (groupId, position) in seeks)
            {
                if (GetOrAddGroup(groupId).Active is not { } active)
                    continue;
                var wasRunning = active.Player.IsRunning;
                if (wasRunning)
                    active.Player.Pause(flushPolicy: S.Media.Players.PauseFlushPolicy.SkipFlush);
                targets.Add((active.Player, position, wasRunning));
            }

            // 2) Seek all with clocks frozen, then 3) release the running ones together from the shared epoch.
            foreach (var (player, position, _) in targets)
                player.SeekCoordinated(position);
            foreach (var (player, _, resume) in targets)
                if (resume)
                    player.Play();
            return Task.CompletedTask;
        });
    }

    /// <summary>Soft-stops and releases the active clip on <paramref name="groupId"/>. The cue's configured
    /// fade-out is used, falling back to the legacy HaPlay 750 ms stop fade. Cancels any in-flight cue fire first
    /// so STOP never waits behind a long pre-wait/open (NXT-03). The fade ramp itself runs OFF the serial
    /// dispatcher (NXT-18) — only short gain/opacity steps and the final release marshal onto it — so other
    /// commands (pause, seek, load, a following GO's commit) never queue behind a long configured fade. The
    /// returned task still completes only after the clip is released ("stopped means stopped").</summary>
    /// <param name="fade">When true (the cue Stop/Panic default) the group's audio + layers ramp to silence over
    /// its <see cref="ShowClipBinding.FadeOut"/> or <see cref="SoftStopFadeDuration"/> first; when false the clip is
    /// cut immediately (the media-player deck stops hard — it has no soft-stop fade).</param>
    public Task StopAsync(string groupId = DefaultGroup, bool fade = true)
    {
        CancelActiveFire();
        return StopGroupsCoreAsync(() => [GetOrAddGroup(groupId)], fade);
    }

    /// <summary>Soft-stops every active transport group together (HaPlay Stop/Panic parity).</summary>
    public Task StopAllAsync()
    {
        CancelActiveFire();
        return StopGroupsCoreAsync(() => _groups.Values.Where(group => group.Active is not null).ToArray(), fade: true);
    }

    /// <summary>Stops the cue with <paramref name="cueId"/> wherever it is the active clip (per-cue stop /
    /// cancel — the GUI's <c>CancelCueCallback</c>). No-op when that cue isn't currently playing.</summary>
    public Task StopCueAsync(string cueId)
    {
        CancelActiveFire();
        return StopGroupsCoreAsync(
            () => _groups.Values.Where(group => group.Active?.Spec.Id == cueId).ToArray(), fade: true);
    }

    /// <summary>What one stop targeted, captured on the dispatcher at claim time: the group, the clip that was
    /// active THEN (the only clip this stop may release), and — when the fade claim succeeded — its ramp.</summary>
    private sealed record StopClaim(TransportGroup Group, IArmedClip? Clip, GroupFade? Fade);

    /// <summary>The shared stop path (NXT-18): resolves the target groups and claims their fades ON the
    /// dispatcher, ramps OFF it (<see cref="RunStopFadeAsync"/>), then releases each claimed clip back ON the
    /// dispatcher — identity-guarded, so a cue fired DURING the fade survives (a stop only releases the clip it
    /// saw at claim time; the previous on-dispatcher implementation got the same outcome by queuing the fire's
    /// commit behind the whole fade). A group whose fade claim lost to an in-flight natural fade-out skips the
    /// ramp (that fade task owns the levels) but is still released here — a STOP preempts a natural fade.
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
            // The session was disposed mid-stop — disposal releases every clip itself.
        }
    }

    /// <summary>Ramps the claimed stop fades to silence OFF the dispatcher: each step marshals one short
    /// gain/opacity commit onto it (the <see cref="StartNaturalFadeOut"/> pattern), so the serial loop is never
    /// parked for the fade duration (NXT-18). All fades advance from one stopwatch so Panic fades groups
    /// concurrently. Exits early when every claimed clip has been replaced (nothing left to fade).</summary>
    private async Task RunStopFadeAsync(IReadOnlyList<GroupFade> fades)
    {
        var maxDuration = TimeSpan.Zero;
        foreach (var fade in fades)
            if (fade.Duration > maxDuration)
                maxDuration = fade.Duration;
        if (maxDuration <= TimeSpan.Zero)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var elapsed = sw.Elapsed;
            bool anyStillActive;
            try
            {
                anyStillActive = await InvokeAsync(() =>
                {
                    var applied = false;
                    foreach (var fade in fades)
                    {
                        if (!ReferenceEquals(fade.Group.Active, fade.Clip))
                            continue; // replaced/ended during the fade — leave the new clip alone
                        var scale = (float)Math.Clamp(
                            1d - elapsed.TotalMilliseconds / fade.Duration.TotalMilliseconds, 0d, 1d);
                        fade.Group.ApplyFadeLevel(
                            fade.Clip.Player, fade.RouteTargets, fade.StartAudioScale, fade.StartOpacity, scale);
                        applied = true;
                    }

                    return Task.FromResult(applied);
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return; // session disposed mid-fade — disposal owns the teardown
            }

            if (!anyStillActive || elapsed >= maxDuration)
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
        IReadOnlyList<AudioRouteTarget> RouteTargets);

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

    /// <summary>Pauses or resumes EVERY active transport group together — the all-groups form the UI drives, and
    /// the pause parity of <see cref="StopAllAsync"/>. The single-group <see cref="SetPausedAsync"/> only touches
    /// one group, so a multi-group cue show (cues fired onto several groups) would leave the other groups running
    /// when paused. Runs as one dispatcher operation so the groups toggle behind one epoch.</summary>
    public Task SetAllPausedAsync(bool paused) =>
        InvokeAsync(() =>
        {
            foreach (var group in _groups.Values)
                if (group.Active is { } active)
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
            var liveDisconnected = false;
            var audioChannels = 0;
            var audioSampleRate = 0;
            var active = v.Player is not null; // has a clip (playing/paused/frozen) — independent of the clock
            try
            {
                now = v.Clock.Now;
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
            catch { /* concurrent teardown — leave zeros for this tick */ }
            snaps[i] = new TransportSnapshot(
                v.GroupId, now, pos, dur, running, active, liveDisconnected, audioChannels, audioSampleRate);
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

    /// <summary>Shows (<paramref name="frame"/> non-null) or hides (null) a mapping-calibration test pattern on a
    /// composition — held in a top-most, full-canvas layer so the operator can align one output's warp against the
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
            .Select(kv => new GroupClockView(kv.Key, kv.Value.Clock, kv.Value.Active?.Player))
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
    /// routed outputs — the audio analogue of <see cref="GetCompositionStats"/> for the outputs-panel line-health
    /// poll. Keyed by the PortAudio device id a cue routed audio to; a UI output line maps its device id into this.
    /// Reads a volatile snapshot (republished on fire/stop) then each router's own thread-safe pump stats — no
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

    /// <summary>Republishes the lock-free composition view after a load/dispose changes <see cref="_compositions"/>.
    /// Call on the dispatcher (the only place <see cref="_compositions"/> is mutated).</summary>
    private void PublishCompositionsView() =>
        _compositionsView = new Dictionary<string, ClipCompositionRuntime>(_compositions, StringComparer.Ordinal);

    /// <summary>Lock-free per-composition stats for a UI health poll — no dispatcher marshaling (mirrors
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
        IReadOnlyList<PlacedLayer> layers, IReadOnlyList<IDisposable>? subtitleAttachments = null)
    {
        await group.ReplaceAsync(clip, outputs, layers, subtitleAttachments).ConfigureAwait(false);
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
        foreach (var id in _voices.Keys.Concat(_pendingVoiceOpens.Keys).Distinct().ToArray())
            await ReleaseVoiceAsync(id).ConfigureAwait(false);
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

    /// <summary>One transport group: its session clock (D4), active clip, its audio outputs (D11 — first is
    /// master, rest auto-slave), and the composition layer the active clip's video feeds (all released on
    /// clip replace).</summary>
    private sealed class TransportGroup : IAsyncDisposable
    {
        public SessionClock Clock { get; } = new(new MonotonicWallClock(start: false));
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

        public ShowClipBinding? ActiveBinding => _activeBinding;
        public IReadOnlyList<(string OutputId, string DeviceId)> ActiveAudioPumps => _activeAudioPumps;

        /// <summary>Records the active clip's device-tagged routed audio outputs (for the line-health poll). Call
        /// on the dispatcher after the clip's outputs are attached.</summary>
        public void SetActiveAudioPumps(IReadOnlyList<(string OutputId, string DeviceId)> pumps) =>
            _activeAudioPumps = pumps as (string, string)[] ?? pumps.ToArray();

        /// <summary>Replaces the tracked audio outputs for a LIVE rebuild (hot add/remove of a deck output).
        /// Returns the previous set so the caller releases each per its ownership AFTER removing it from the
        /// router — releasing an owned sink while its route still exists would dangle.</summary>
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
        public ClipCompositionRuntime.LayerSlot? ActiveLayer => _layers.Count > 0 ? _layers[0].Slot : null;
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
            IReadOnlyList<IDisposable>? subtitleAttachments = null)
        {
            // Stop the displaced clip's background work (fade ramp + end-of-clip monitor) before anything else.
            _clipWorkCts?.Cancel();
            _clipWorkCts?.Dispose();
            _clipWorkCts = null;
            _activeBinding = null;
            _activeRouteTargets = [];
            _activeAudioPumps = [];
            _activeAudioScale = 1f;
            Volatile.Write(ref _fadeOutStarted, 0);

            Clock.SetReference(clip is null
                ? new MonotonicWallClock(start: false)
                : new PlayheadPlaybackClock(clip.Player.PlayClock));

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
