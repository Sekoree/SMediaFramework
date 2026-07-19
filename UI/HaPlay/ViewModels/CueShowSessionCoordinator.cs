using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using HaPlay.Playback;
using HaPlay.Resources;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Session;

namespace HaPlay.ViewModels;

/// <summary>
/// The cue workspace's playback coordination - extracted from <see cref="MainViewModel"/> along its ownership
/// seam (review Part-5 #3). Owns the headless per-app <see cref="ShowSession"/>, the composition→output-line
/// video leases (acquire/hold/detach-before-release - the NXT-20 ordering has ONE home here), the coalescing
/// document reload, the cue/soundboard progress polls, and the wiring of every CuePlayer/Soundboard transport
/// callback onto the session. UI-thread-affine like the view models it coordinates (DispatcherTimers, Avalonia
/// dispatcher marshaling).
/// </summary>
public sealed class CueShowSessionCoordinator
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.ViewModels.CueShowSessionCoordinator");

    private CuePlayerViewModel CuePlayer { get; }
    private SoundboardWorkspaceViewModel Soundboard { get; }
    private OutputManagementViewModel OutputManagement { get; }

    // The cue workspace's playback runtime: the headless per-app ShowSession (Phase-8 cutover complete - the
    // legacy CuePlaybackEngine/SoundboardEngine/HaPlayPlaybackSession fallbacks are deleted).
    private ShowSession? _cueShowSession;

    /// <summary>The cue session for the I/O Debug page's poll (null until the cue workspace builds it).
    /// Lock-free snapshot readers only - see <see cref="ShowSession.GetActiveClipPipelineMetrics"/>.</summary>
    internal ShowSession? PipelineStatsSession => _cueShowSession;

    /// <summary>The cue session's factory for app-owned audio line routes. PortAudio lines acquire a
    /// private input into their persistent shared mixer; encode routes borrow the armed carrier. Runs
    /// on the session dispatcher and resolves only through thread-safe OutputManagement lookups.
    /// Rate mismatches get a per-fire resampler retired with the borrowed lease.</summary>
    private ClipAudioOutputLease? BuildCueAudioLease(string deviceId, AudioFormat format)
    {
        IAudioOutput rawCarrier;
        Action releaseRawCarrier;
        Guid lineId;

        if (OutputAudioRouteDeviceIds.TryParsePortAudio(deviceId, out lineId))
        {
            if (OutputManagement.TryAcquirePortAudioByLineId(lineId, liveMonitoring: true) is not { } lease)
                return null;
            rawCarrier = lease.Output;
            releaseRawCarrier = lease.Dispose;
        }
        else if (OutputAudioRouteDeviceIds.TryParseEncode(deviceId, out lineId))
        {
            if (OutputManagement.TryAcquireEncodeAudioByLineId(lineId) is not { } encodeCarrier)
                return null; // disarmed/unknown - the cue plays without this route
            rawCarrier = encodeCarrier;
            releaseRawCarrier = () => OutputManagement.ReleaseEncodeAudioByLineId(lineId);
        }
        else
        {
            return null; // ordinary backend id - let ShowSession create its own output
        }

        // Line audio effects run between the route and the encoder (same insert point as the deck).
        // The carrier stays BORROWED (disposeInner:false); the Release hook retires OUR effect wrapper
        // (review H4: neither the session's dispose of a resampler nor a borrowed lease reaches it).
        var carrier = OutputManagement.WrapAudioEffectsForLine(lineId, rawCarrier);

        void Release()
        {
            if (!ReferenceEquals(carrier, rawCarrier) && carrier is IDisposable effectWrapper)
            {
                try { effectWrapper.Dispose(); } // effects retire; stops at the borrowed carrier
                catch { /* best effort */ }
            }

            releaseRawCarrier();
        }

        if (carrier.Format.SampleRate == format.SampleRate && carrier.Format.Channels == format.Channels)
            return new ClipAudioOutputLease(carrier, DisposeOutputOnRuntimeDispose: false, Release: Release);

        // Rate (or short-matrix channel) mismatch: adapt the clip into the carrier's format. The release
        // hook retires the adapter first, then the effect wrapper and borrowed carrier in dependency order.
        var adapted = S.Media.Decode.FFmpeg.Audio.ResamplingAudioOutput.Wrap(carrier, format);
        return new ClipAudioOutputLease(
            adapted,
            DisposeOutputOnRuntimeDispose: false,
            Release: () =>
            {
                try { (adapted as IDisposable)?.Dispose(); }
                finally { Release(); }
            });
    }
    // compositionId → the real video outputs it renders to, acquired on the UI thread in the cue reload
    // so ShowSession's video factory is a pure lookup during LoadDocumentAsync.
    private Dictionary<string, CueShowVideoOutput[]> _cueVideoOutputs = new(StringComparer.Ordinal);
    private sealed record CueShowVideoOutput(Guid LineId, IVideoOutput Output, CueOutputMapping? Mapping);
    // The output lines currently held by the re-back (single-holder leases). Holds persist across reloads for
    // lines the new model still binds (no release→re-acquire churn, NXT-20); lines no longer bound are detached
    // from the live compositions first, then released, so the old composition pump never submits into a sink
    // the idle slate just reconfigured.
    private readonly List<Guid> _cueAcquiredVideoLines = new();
    private readonly SemaphoreSlim _cueOutputReconcileGate = new(1, 1);
    private bool _cueOutputAvailabilityWarningActive;
    // Live projectM sources by composition. ShowSession owns their disposal; this reference exists only
    // for the cue drawer's operator-triggered "next preset" action.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, S.Media.Visualizer.ProjectM.ProjectMVisualSource>
        _cueVisualizerSources = new();
    // Every node collection in the selected cue tree (root + nested groups). Watching only the root missed songs
    // inserted into a Group until a later property probe happened to dirty the graph.
    private readonly HashSet<INotifyCollectionChanged> _subscribedCueNodeCollections = new();
    private CueListEditorViewModel? _subscribedCueGraphList;
    private readonly HashSet<CueCompositionViewModel> _subscribedCueCompositions = new();
    private readonly HashSet<CueVideoOutputBindingViewModel> _subscribedCueOutputBindings = new();
    private DispatcherTimer? _cueReloadDebounce;
    // Set as soon as the GUI cue model changes and cleared only after the reload commits it. Fire paths
    // flush a dirty graph synchronously on the UI thread, so GO cannot race the 300 ms edit debounce and execute
    // an older cue definition (notably a media cue captured before its Source was assigned).
    private volatile bool _cueShowGraphDirty;
    // Structural cue edits can reuse an unchanged composition and its persistent visualizer. Composition/output
    // changes and list/project replacement require a full rebuild. Coalescing uses AND semantics: one full-rebuild
    // request in a burst takes precedence over any number of preservation-safe node edits.
    private bool _cueReloadMayPreserveMatchingCompositions;
    // Project restore replaces output runtimes and the cue document as one operation. Automatic list-change /
    // debounce reloads are suspended across that boundary so the session cannot create an SDL/GL compositor
    // concurrently with a restored local-video preview (SDL's Linux EGL window lifecycle is not safe there).
    private int _automaticReloadSuspendCount;
    // Immutable-by-convention snapshot replaced after each document load. Used to turn an unbound/stale media
    // cue into an explicit operator error instead of calling a no-op session action.
    private volatile IReadOnlySet<Guid> _cueShowBoundCueIds = new HashSet<Guid>();
    // Re-back cue → its transport group (from the mapped doc), and each group's currently-active cue, so the
    // snapshot poll can drive BOTH progress (OnCueProgress) and end (OnCueEnded) per group - the ShowSession
    // transport raises neither event, so without this the now-playing countdown is frozen and rows never clear.
    private Dictionary<Guid, string> _cueGroupByCueId = new();
    private readonly Dictionary<string, CueShowActiveState> _cueShowActiveByGroup = new(StringComparer.Ordinal);
    private DispatcherTimer? _cueShowProgressPoll;
    private DispatcherTimer? _soundboardProgressPoll;

    /// <summary>Per-group active-cue state for the re-back progress poll. <see cref="ObservedRunning"/> guards the
    /// warmup race - the play clock takes a moment to start, so the poll must not treat the first not-running tick
    /// as "ended" until the clip has actually been seen running (or its warmup grace elapses).</summary>
    private sealed class CueShowActiveState
    {
        public required Guid CueId;
        public bool ObservedRunning;
        public int NotRunningTicks;
    }
    // soundboard tile → its configured fade-out (ms), captured at play so FadeOutSound (tile-only) can use it.
    private readonly Dictionary<Guid, int> _soundboardFadeMs = new();

    /// <summary>Tiles this host started whose voice may still be live - the progress poll reconciles
    /// it against the session's voice snapshot to catch releases that raise no VoiceEnded (fade-outs).</summary>
    private readonly HashSet<Guid> _soundboardActiveTiles = new();

    public CueShowSessionCoordinator(
        CuePlayerViewModel cuePlayer,
        SoundboardWorkspaceViewModel soundboard,
        OutputManagementViewModel outputManagement)
    {
        CuePlayer = cuePlayer;
        Soundboard = soundboard;
        OutputManagement = outputManagement;
    }

    private static void FireAndLog(Task task, string operation)
    {
        if (task.IsCompletedSuccessfully)
            return;

        _ = ObserveFireAndForgetAsync(task, operation);
    }

    private static async Task ObserveFireAndForgetAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            Trace.LogDebug(ex, "{Operation}: background task cancelled", operation);
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "{Operation}: background task failed", operation);
        }
    }

    /// <summary>Runs the cue workspace's transport + soundboard on the headless <see cref="ShowSession"/> - the
    /// app's ONLY playback runtime since the Phase-8 cutover completed and the legacy engines were deleted.
    /// Audio realizes on the routed/default device via the session's backend; cue video realizes on the
    /// OutputManagement NDI/SDL/local lines acquired per composition→line binding in
    /// <see cref="ReloadCueShowSessionAsync"/> and fanned out through the session's composition-id video factory.
    /// The show reloads on cue-list change.</summary>
    public void WireShowSessionCueTransport()
    {
        Trace.LogInformation("HaPlay cue playback path: ShowSession (cutover complete - no legacy engine).");

        try
        {
            var backend = MediaRuntime.Registry.AudioBackends.FirstOrDefault();
            _cueShowSession = new ShowSession(
                MediaRuntime.Registry,
                backend,
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex),
                // Borrowed lines: the cue workspace owns each output's lifetime (acquire/release via
                // _cueAcquiredVideoLines), so the leases declare DisposeOutputOnRuntimeDispose=false - the session
                // never disposes them (NXT-01).
                (compId, name, _, _) => _cueVideoOutputs.TryGetValue(compId, out var outs)
                    ? outs.Select(o => new ClipCompositionOutputLease(
                        o.LineId.ToString("N"), name, o.Output,
                        DisposeOutputOnRuntimeDispose: false,
                        Mapping: HaPlayShowMapper.ToClipOutputMapping(o.Mapping))).ToArray()
                    : Array.Empty<ClipCompositionOutputLease>(),
                CueCompositionRuntime.CreateShowSessionCompositor,
                // Encode lines as cue audio targets: "file-audio:{lineId}" routes resolve to the ARMED
                // session's combined multi-track sink (borrowed - the runtime owns it; a per-fire
                // resampler adapts the clip rate when needed). Disarmed line ⇒ null ⇒ the cue plays
                // without that route, exactly like an unopenable device.
                audioOutputFactory: BuildCueAudioLease,
                // Effect buses (Phase 4): tags + cover art for the metadata hub (visualizers/overlays).
                metadataProbe: S.Media.Decode.FFmpeg.MediaTagProbe.TryRead);

            CuePlayer.CueStandbyInvalidated += (_, _) => ScheduleCueShowSessionReload();
            CuePlayer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CuePlayer.SelectedCueList))
                {
                    SubscribeCueGraphForReload(); // re-bind model watches to the newly-selected list
                    MarkCueShowGraphDirty(preserveMatchingCompositions: false);
                    // A list switch is discrete → reload now, except while project restore is deliberately
                    // sequencing output-window creation before composition-window creation.
                    if (Volatile.Read(ref _automaticReloadSuspendCount) == 0)
                        FireAndLog(ReloadCueShowSessionAsync(), "cue ShowSession reload (list switch)");
                }
            };
            SubscribeCueGraphForReload();
            MarkCueShowGraphDirty(preserveMatchingCompositions: false);
            FireAndLog(ReloadCueShowSessionAsync(), "cue ShowSession reload (initial)");

            // Cue output-line health comes from the session's composition throughput, so a cue-driven line's
            // health LED keeps working.
            OutputManagement.CueLineMetricsProbe = TryGetCueShowLineHealthMetrics;
            OutputManagement.OutputLineReconfiguringAsync += OnOutputLineReconfiguringAsync;
            OutputManagement.OutputLineReconfiguredAsync += OnOutputLineReconfiguredAsync;
            OutputManagement.RoutingTopologyChanged += OnOutputRoutingTopologyChanged;

            // Override the transport callbacks to drive ShowSession. The VM resolves WHICH cues fire and hands
            // them to the executors, so we fire by id (FireCueAsync) - independent of ShowSession's GO anchor.
            // Each transport op is guarded so a failure is LOGGED (not only surfaced as a UI notification).
            CuePlayer.StopPlaybackCallback = () => GuardedCueShowOp("stop", async () =>
            {
                var session = _cueShowSession!;
                var sourcesAtStop = _cueVisualizerSources.ToArray();
                await session.StopAllAsync().ConfigureAwait(false);
                foreach (var (compositionId, source) in sourcesAtStop)
                {
                    // Do not remove a replacement fired onto this composition during the fade.
                    if (_cueVisualizerSources.TryGetValue(compositionId, out var current)
                        && ReferenceEquals(current, source)
                        && !await session.HasCompositionVisualizerAsync(compositionId.ToString()).ConfigureAwait(false))
                        _cueVisualizerSources.TryRemove(compositionId, out _);
                }
            });
            // #26 visualizer cue: start/stop the projectM layer on a composition WITH placement (a
            // section of the frame). The layer persists across later cue fires and cue-graph edits. If an
            // output-topology change rebuilds its composition, ShowSession recreates only the surface while
            // retaining the same projectM source/tap/preset state; Stop remains the explicit lifetime boundary.
            CuePlayer.VisualizerCueExecutor = async (viz, _) =>
            {
                if (_cueShowSession is not { } session)
                    return "cue session unavailable";
                // v3: composition + geometry come from the cue's Video-tab placements (media-cue parity);
                // legacy single-rect files were migrated to one placement at load. Placements group by
                // composition: ONE projectM source per composition (its audio tap must not double-feed),
                // one surface layer per placement, so several sections of a canvas show the same render.
                var groups = new List<(Guid Comp, IReadOnlyList<CueVideoPlacement?> Places)>();
                foreach (var byComp in viz.VideoPlacements.GroupBy(p => p.CompositionId))
                    if (byComp.Key != Guid.Empty)
                        groups.Add((byComp.Key, byComp.Cast<CueVideoPlacement?>().ToList()));
                if (groups.Count == 0 && viz.CompositionId != Guid.Empty)
                    groups.Add((viz.CompositionId, new CueVideoPlacement?[] { null })); // legacy: full-canvas
                if (groups.Count == 0)
                    return "the visualizer cue has no composition placement - add one on its Video tab";

                if (!viz.StartVisualizer)
                {
                    foreach (var (stopComp, _) in groups)
                    {
                        _cueVisualizerSources.TryGetValue(stopComp, out var sourceAtStop);
                        await session.FadeOutCompositionVisualizerAsync(stopComp.ToString()).ConfigureAwait(false);
                        // A new Start cue may replace this composition's source while the old one fades.
                        // Never let the completing Stop remove that replacement from the operator controls.
                        if (_cueVisualizerSources.TryGetValue(stopComp, out var current)
                            && ReferenceEquals(current, sourceAtStop))
                            _cueVisualizerSources.TryRemove(stopComp, out var _removedSource);
                    }
                    return null;
                }

                // Audio-feed routing (#26): FeedAll = every clip; else the cue's selected sources plus
                // media cues flagged "send to visualizer". The set is snapshotted HERE (UI thread) and
                // read lock-free by the session-dispatcher filter; refire the viz cue after changing it.
                var listModel = CuePlayer.SelectedCueList?.ToModel();
                Func<string, bool>? feedFilter = null;
                if (!viz.FeedAll)
                {
                    var allowed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in viz.FeedCueIds)
                        allowed.Add(id.ToString());
                    if (listModel is not null)
                        foreach (var node in FlattenCueNodes(listModel.Nodes))
                            if (node is MediaCueNode { SendToVisualizer: true } m)
                                allowed.Add(m.Id.ToString());
                    feedFilter = cueId => allowed.Contains(cueId);
                }

                if (!RuntimeModules.IsProjectMAvailable)
                    return "projectM is not available on this machine";

                string? firstError = null;
                foreach (var (compGuid, places) in groups)
                {
                    var compId = compGuid.ToString();
                    var comp = listModel?.Compositions.FirstOrDefault(c => c.Id == compGuid);
                    if (comp is null)
                    {
                        firstError ??= "the visualizer cue's placement points at a composition that no longer exists";
                        continue;
                    }

                    var renderW = viz.RenderWidth > 0 ? viz.RenderWidth : comp.Width > 0 ? comp.Width : 1920;
                    var renderH = viz.RenderHeight > 0 ? viz.RenderHeight : comp.Height > 0 ? comp.Height : 1080;
                    var fps = viz.RenderFps > 0
                        ? viz.RenderFps
                        : comp.FrameRateDen > 0 && comp.FrameRateNum > 0 ? comp.FrameRateNum / comp.FrameRateDen : 60;
                    var source = new S.Media.Visualizer.ProjectM.ProjectMVisualSource(
                        renderW, renderH, new Rational(fps > 0 ? fps : 60, 1),
                        new S.Media.Visualizer.ProjectM.ProjectMOptions
                        {
                            PresetDirectory = string.IsNullOrWhiteSpace(viz.PresetDirectory) ? null : viz.PresetDirectory,
                            RenderWidth = renderW,
                            RenderHeight = renderH,
                            Fps = fps,
                            // Older visualizer cues may contain an explicit zero from before these controls
                            // had model defaults. Zero now consistently means the projectM default (30 s),
                            // while positive values retain the five-second safety minimum.
                            PresetDurationSeconds = viz.PresetDurationSeconds > 0
                                ? Math.Max(5, viz.PresetDurationSeconds)
                                : 30,
                            Shuffle = viz.ShufflePresets,
                            BeatSensitivity = Math.Clamp(viz.BeatSensitivity, 0, 5),
                            TransitionSeconds = Math.Clamp(viz.TransitionSeconds, 0, 30),
                        });
                    var specs = places.Select(p => ToVisualizerPlacement(compId, p)).ToArray();
                    if (!await session.SetCompositionVisualizerAsync(
                                compId, source, placements: specs, audioFeedFilter: feedFilter,
                                preserveAcrossDocumentReload: true)
                            .ConfigureAwait(false))
                    {
                        source.Dispose();
                        Trace.LogWarning("visualizer cue: attach REFUSED (comp={Comp} - no GL surface host?)", compId);
                        firstError ??= "visualizer not attached (composition has no GL surface host)";
                        continue;
                    }

                    _cueVisualizerSources[compGuid] = source;

                    Trace.LogInformation(
                        "visualizer cue ATTACHED: comp={Comp} render={W}x{H}@{Fps} placements={Count} feed={Feed} presets='{Presets}'",
                        compId, renderW, renderH, fps, specs.Length,
                        viz.FeedAll ? "all" : "selective", viz.PresetDirectory ?? "(builtin)");
                }
                return firstError;
            };
            // Pause must hit EVERY active group, not just the default one - a multi-group cue show would otherwise
            // keep the other groups running on pause (parity with StopAllAsync).
            CuePlayer.SetPlaybackPausedCallback = paused => GuardedCueShowOp("pause", () => _cueShowSession!.SetAllPausedAsync(paused));
            CuePlayer.SeekCueCallback = (cueId, pos) => GuardedCueShowOp(
                "seek", () => _cueShowSession!.SeekAsync(pos, ResolveCueShowRuntimeGroup(cueId)));
            // Multi-cue seek goes through the group-seek barrier so every targeted group lands atomically behind one
            // shared epoch (a group runs one active clip, so a cue's seek lands on whatever is active in its group).
            CuePlayer.SeekCuesCallback = positions => GuardedCueShowOp("seek-cues", () =>
                _cueShowSession!.SeekManyAsync(
                    positions.Select(p => (ResolveCueShowRuntimeGroup(p.CueId), p.Position)).ToList()));
            CuePlayer.CancelCueCallback = id => GuardedCueShowOp("cancel", () => _cueShowSession!.StopCueAsync(id.ToString()));
            // Cue preview (audition on the preview/headphone device) goes through ShowSession too - otherwise it was
            // the last cue callback still driving the now-inactive engine under the gate. The PortAudio backend takes
            // the global device index as its device id (see CuePreviewSession), so the preview device selection
            // carries straight through; PreviewEnded flows back to the UI like the engine's event did.
            CuePlayer.PreviewCueCallback = async (cue, _) =>
            {
                try
                {
                    await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                    var deviceId = CuePlayer.PreviewAudioDeviceIndex?.ToString(CultureInfo.InvariantCulture);
                    return await _cueShowSession!.PreviewCueAsync(cue.Id.ToString(), deviceId).ConfigureAwait(false)
                        ? null
                        : "cue has no ShowSession media binding to preview";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            };
            CuePlayer.StopPreviewCallback = () => _cueShowSession!.StopPreviewAsync();
            _cueShowSession.PreviewEnded += id =>
            {
                if (Guid.TryParse(id, out var previewCueId))
                    Dispatcher.UIThread.Post(() => CuePlayer.OnPreviewEnded(previewCueId));
            };
            // Drive the cue-list pre-roll "warm/ready" badges from the session's standby (mirrors the engine's
            // PreparedCuesChanged wiring) - otherwise a warmed cue never showed Ready under the gate.
            _cueShowSession.PreparedCuesChanged += statuses =>
            {
                var warm = new HashSet<Guid>();
                var mapped = new List<Playback.CuePreparationStatus>(statuses.Count);
                foreach (var s in statuses)
                {
                    if (!Guid.TryParse(s.Key.Id, out var cueId))
                        continue; // non-cue keys (e.g. "preview") aren't cue-list rows
                    var state = MapClipPreparationState(s.State);
                    mapped.Add(new Playback.CuePreparationStatus(cueId, state, s.Error));
                    if (state == Playback.PreparedCueState.Ready)
                        warm.Add(cueId);
                }
                Dispatcher.UIThread.Post(() =>
                {
                    CuePlayer.OnPreRollCacheChanged(warm);
                    CuePlayer.OnPreparedCueStatesChanged(mapped);
                });
            };
            CuePlayer.MediaCueExecutor = async (cue, _) =>
            {
                try
                {
                    await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                    if (!_cueShowBoundCueIds.Contains(cue.Id))
                    {
                        var detail = "cue has no current ShowSession media binding; its source may be empty or unsupported";
                        Trace.LogError("HaPlay: cue '{Label}' ({Id}) cannot fire - {Detail}", cue.Label, cue.Id, detail);
                        return detail;
                    }
                    var status = await _cueShowSession!.FireCueAsync(cue.Id.ToString()).ConfigureAwait(false);
                    if (status != CueExecutionStatus.Fired)
                    {
                        var detail = await DescribeCueShowFailureAsync(cue.Id, status).ConfigureAwait(false);
                        Trace.LogWarning(
                            "HaPlay: cue '{Label}' ({Id}) did not fire - status {Status}: {Detail}",
                            cue.Label, cue.Id, status, detail);
                        return detail;
                    }
                    // Engine-callback parity: report the cue active so the UI shows it Triggered/now-playing -
                    // the result handler treats a cue absent from _activeCueIds as "Failed to start" even on a
                    // successful fire, because the ShowSession path doesn't raise the engine's OnCueStarted.
                    await Dispatcher.UIThread.InvokeAsync(() => MarkCueShowCueStarted(cue.Id));
                    return null;
                }
                catch (Exception ex)
                {
                    Trace.LogError(ex, "HaPlay: firing cue '{Label}' ({Id}) through ShowSession failed", cue.Label, cue.Id);
                    return ex.Message;
                }
            };
            CuePlayer.MediaCueIndependentExecutor = async (cue, ct) =>
            {
                try
                {
                    await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                    if (!_cueShowBoundCueIds.Contains(cue.Id))
                        return "cue has no current ShowSession media binding; its source may be empty or unsupported";

                    var runtimeGroup = $"manual:{cue.Id:N}";
                    var status = await _cueShowSession!
                        .FireCueIndependentAsync(cue.Id.ToString(), runtimeGroup, ct)
                        .ConfigureAwait(false);
                    if (status != CueExecutionStatus.Fired)
                        return await DescribeCueShowFailureAsync(cue.Id, status).ConfigureAwait(false);

                    await Dispatcher.UIThread.InvokeAsync(() => MarkCueShowCueStarted(cue.Id, runtimeGroup));
                    return null;
                }
                catch (Exception ex)
                {
                    Trace.LogError(ex, "HaPlay: independently firing grouped cue '{Label}' ({Id}) failed", cue.Label, cue.Id);
                    return ex.Message;
                }
            };
            CuePlayer.MediaCueGroupExecutor = async (cues, ct) =>
            {
                // Flush pending edits once, then give every simultaneous child its own runtime transport group.
                // Authored siblings intentionally share a group for sequential GO/replacement, but one ShowSession
                // transport group owns only one active clip; using it for this batch made the concurrent commits
                // replace one another. The batch API still opens concurrently under one fire lock/cancellation scope.
                await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                var bound = cues.Where(c => _cueShowBoundCueIds.Contains(c.Id)).ToList();
                foreach (var unbound in cues.Where(c => !_cueShowBoundCueIds.Contains(c.Id)))
                    Trace.LogError("HaPlay: group cue '{Label}' ({Id}) has no current ShowSession media binding",
                        unbound.Label, unbound.Id);
                if (bound.Count == 0)
                    return null;

                IReadOnlyList<CueExecutionStatus> statuses;
                var targets = bound
                    .Select(c => (CueId: c.Id.ToString(), RuntimeGroupId: BuildSimultaneousRuntimeGroup(c.Id)))
                    .ToArray();
                try
                {
                    statuses = await _cueShowSession!.FireCuesIndependentAsync(targets, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.LogError(ex, "HaPlay: firing cue group through ShowSession failed");
                    return null;
                }

                for (var i = 0; i < bound.Count; i++)
                {
                    var cue = bound[i];
                    if (statuses[i] == CueExecutionStatus.Fired)
                    {
                        var runtimeGroup = targets[i].RuntimeGroupId;
                        await Dispatcher.UIThread.InvokeAsync(() => MarkCueShowCueStarted(cue.Id, runtimeGroup));
                    }
                    else
                        Trace.LogWarning(
                            "HaPlay: group cue '{Label}' ({Id}) did not fire - status {Status}: {Detail}",
                            cue.Label, cue.Id, statuses[i],
                            await DescribeCueShowFailureAsync(cue.Id, statuses[i]).ConfigureAwait(false));
                }
                return null;
            };

            // The editor callbacks are independent of transport - they must target the live ShowSession
            // composition so a placement/output-layout edit changes the running image, not just the model.
            CuePlayer.UpdateActiveCueVideoPlacementCallback = async (cueId, _, placement) =>
            {
                var updated = await _cueShowSession.UpdateActivePlacementAsync(
                    cueId.ToString(), placement.CompositionId.ToString(), placement.LayerIndex,
                    HaPlayShowMapper.ToShowVideoPlacement(placement)).ConfigureAwait(false);
                if (!updated)
                    Trace.LogWarning("HaPlay: live placement update missed cue {Cue} / composition {Composition}",
                        cueId, placement.CompositionId);
            };
            // placementIndex is the placement's index AMONG THE CUE'S PLACEMENTS ON THIS COMPOSITION
            // (the VM computes it), matching the per-composition layer order the executor attached.
            CuePlayer.UpdateActiveVisualizerPlacementCallback = async (cueId, placementIndex, placement) =>
            {
                var compId = placement.CompositionId.ToString();
                var updated = await _cueShowSession!.UpdateCompositionVisualizerPlacementAsync(
                    compId, ToVisualizerPlacement(compId, placement), placementIndex).ConfigureAwait(false);
                if (!updated)
                    Trace.LogWarning(
                        "HaPlay: live visualizer placement update missed cue {Cue} placement {Index} / composition {Composition}",
                        cueId, placementIndex, placement.CompositionId);
            };
            CuePlayer.NextVisualizerPresetCallback = compositionId =>
            {
                if (!_cueVisualizerSources.TryGetValue(compositionId, out var source))
                    return Task.FromResult(false);
                source.RequestNextPreset();
                return Task.FromResult(true);
            };
            // The audio counterpart of the live placement edit: re-apply the active cue's audio routing to the
            // running clip (was left on the now-inactive engine, so live level/channel tweaks silently no-op'd).
            // Mapped through the same MapAudioRoutes the fire path uses so the clip{i} outputs line up.
            CuePlayer.UpdateActiveCueAudioRoutesCallback = async (cueId, routes) =>
            {
                var mapped = HaPlayShowMapper.MapActiveAudioRoutes(routes, OutputManagement.DefinitionsSnapshot);
                var applied = await _cueShowSession!.ApplyActiveAudioRoutesAsync(cueId.ToString(), mapped)
                    .ConfigureAwait(false);
                if (!applied)
                    Trace.LogWarning("HaPlay: live audio-route update missed cue {Cue} (not the active clip)", cueId);
            };
            // Live-re-render a playing text cue on a text/style edit: render the new frame and swap it onto the
            // running clip's held source in place (IReplaceableFrameSource), so the change shows immediately instead
            // of only on the next fire. No-op when the cue isn't the active clip (the frame is disposed by the session).
            CuePlayer.UpdateActiveCueTextCallback = async (cueId, model) =>
            {
                if (model.Source is not HaPlay.Models.TextPlaylistItem textItem)
                    return;
                var frame = S.Media.Source.Text.TextFrameRenderer.Render(textItem.ToSpec(), new Rational(30, 1));
                if (frame is null)
                {
                    Trace.LogWarning("HaPlay: live text update cue {Cue} - render returned null", cueId);
                    return;
                }
                var w = frame.Format.Width;
                var h = frame.Format.Height;
                var applied = await _cueShowSession!.UpdateActiveClipFrameAsync(cueId.ToString(), frame).ConfigureAwait(false);
                Trace.LogInformation("HaPlay: live text update cue {Cue} - applied={Applied} frame={W}x{H}", cueId, applied, w, h);
            };
            // An idle cue's placement edit only lives in the model; the show document is rebuilt at (re)load,
            // not on placement edits. Mark it stale (flag only - no debounced auto-reload that would interrupt a
            // running composition mid-drag) so the next GO reloads with the current placement. The flush before
            // firing (EnsureCueShowSessionCurrentAsync) then rebuilds the document so the cue fires with the
            // geometry the operator set, instead of the placement captured at the last structural reload.
            CuePlayer.CueClipModelStaleCallback = () =>
                MarkCueShowGraphDirty(preserveMatchingCompositions: true);
            CuePlayer.UpdateOutputMappingCallback = (compositionId, outputLineId, mapping) =>
            {
                FireAndLog(ApplyCueShowOutputMappingAsync(compositionId, outputLineId, mapping),
                    "ShowSession output mapping update");
                return true; // accepted for serialized application by ShowSession
            };
            CuePlayer.UpdateCompositionVideoFxCallback = (compositionId, mapping) =>
            {
                FireAndLog(ApplyCueShowCompositionMappingAsync(compositionId, mapping),
                    "ShowSession composition mapping update");
                return true;
            };
            // Calibration-grid injection on the ShowSession path: render the grid (masked to the visible/binding
            // mapping - the host owns the masking) and hold it in a top-most composition layer via ShowSession.
            CuePlayer.SetCompositionTestPatternCallback = (compositionId, outputLineId, mapping, show) =>
            {
                if (_cueShowSession is null)
                    return false;
                if (!show)
                {
                    FireAndLog(_cueShowSession.SetCompositionTestPatternAsync(compositionId.ToString(), null),
                        "ShowSession test-pattern hide");
                    return true;
                }

                var model = CuePlayer.SelectedCueList?.ToModel();
                if (model?.Compositions.FirstOrDefault(c => c.Id == compositionId) is not { } comp)
                    return false;
                var canvas = new S.Media.Core.Video.VideoFormat(
                    comp.Width, comp.Height, S.Media.Core.Video.PixelFormat.Bgra32,
                    new Rational(comp.FrameRateNum, comp.FrameRateDen));
                var bindingMapping = model.VideoOutputs
                    .FirstOrDefault(b => b.CompositionId == compositionId && b.OutputLineId == outputLineId)?.Mapping;
                var frame = MappingTestPattern.Render(canvas, mapping ?? bindingMapping);
                FireAndLog(_cueShowSession.SetCompositionTestPatternAsync(compositionId.ToString(), frame),
                    "ShowSession test-pattern show");
                return true;
            };

            // Soundboard voices on the same session (task #10): re-back the soundboard transport onto the
            // ShowSession voice subsystem (a streaming player per tile on its routed output line).
            Soundboard.PlaySoundCallback = async req =>
            {
                try
                {
                    var device = OutputManagement.DefinitionsSnapshot
                        .OfType<PortAudioOutputDefinition>()
                        .FirstOrDefault(d => d.Id == req.OutputLineId)?.EffectiveAudioBackendDeviceId;
                    _soundboardFadeMs[req.TileId] = req.FadeOutMs;
                    await _cueShowSession!.FireVoiceAsync(req.TileId.ToString(), req.FilePath, device, (float)req.Volume);
                    _soundboardActiveTiles.Add(req.TileId);
                    Soundboard.OnSoundStarted(req.TileId);
                    StartSoundboardProgressPoll(); // drive per-tile countdown from the session's voice playheads
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            };
            // Explicit stop/fade paths release their voice WITHOUT a VoiceEnded event (the framework's
            // documented contract - VoiceEnded is natural-end only). The tile state must therefore be
            // reset host-side: immediately on explicit stops, and via the progress poll for fade-outs
            // (whose release lands asynchronously when the ramp reaches silence). Without this a
            // faded/stopped tile stayed IsPlaying/IsFading forever and could never be re-triggered.
            Soundboard.StopSoundCallback = async id =>
            {
                await _cueShowSession!.StopVoiceAsync(id.ToString()).ConfigureAwait(true);
                _soundboardActiveTiles.Remove(id);
                Soundboard.OnSoundEnded(id);
            };
            Soundboard.StopAllSoundsCallback = async () =>
            {
                await _cueShowSession!.StopAllVoicesAsync().ConfigureAwait(true);
                _soundboardActiveTiles.Clear();
                Soundboard.OnAllSoundsEnded();
            };
            Soundboard.SetSoundVolumeCallback = (id, vol) => _ = _cueShowSession!.SetVoiceVolumeAsync(id.ToString(), (float)vol);
            Soundboard.FadeOutSoundCallback = id =>
                _cueShowSession!.FadeVoiceAsync(id.ToString(), TimeSpan.FromMilliseconds(_soundboardFadeMs.GetValueOrDefault(id)));
            _cueShowSession.VoiceEnded += id =>
            {
                if (Guid.TryParse(id, out var tileId))
                    Dispatcher.UIThread.Post(() =>
                    {
                        _soundboardActiveTiles.Remove(tileId);
                        Soundboard.OnSoundEnded(tileId);
                    });
            };

            // A cue clip's NATURAL end (out-point stop / fade-out completed - never an operator stop) drives
            // cue auto-follow, the role the legacy engine's NaturalEnd event played.
            _cueShowSession.ClipNaturallyEnded += cueId =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (Guid.TryParse(cueId, out var id))
                        FireAndLog(OnCueClipNaturallyEndedAsync(id), "cue auto-follow");
                });

            Trace.LogInformation("HaPlay: cue transport + soundboard running on ShowSession.");
        }
        catch (Exception ex)
        {
            // With the legacy engines deleted there is no fallback runtime - this is a startup-fatal wiring
            // failure for the cue workspace; log loudly and surface it to the operator.
            Trace.LogError(ex, "HaPlay: ShowSession cue transport wiring FAILED - cue playback is unavailable.");
            CuePlayer.StatusMessage = $"Cue playback unavailable: {ex.Message}";
            _cueShowSession = null;
        }
    }

    /// <summary>ShowSession replacement for <c>CuePlaybackEngine.TryGetLineHealthMetrics</c>: sums the video
    /// throughput of the composition(s) feeding <paramref name="outputLineId"/> AND the audio-pump chunks of any
    /// cue routing audio to the line's stable carrier id, scoring a combined
    /// health state that mirrors the engine. Both sides read the session's lock-free snapshots, so a video, audio,
    /// or audio-only cue line all light up. Null only when the line is genuinely undriven (the outputs panel then
    /// falls back to the media-player probe / Idle). Runs off the UI poll thread with no dispatcher marshaling.</summary>
    // Previous cumulative counters per line for the health poll's recent-rate scoring (called only from
    // the outputs panel's 1 Hz health refresh).
    private readonly Dictionary<Guid, long> _lineHealthPrevVideoLate = new();
    private readonly Dictionary<Guid, long> _lineHealthPrevAudioDropped = new();

    private OutputLineHealthEvaluator.LineHealthMetrics? TryGetCueShowLineHealthMetrics(Guid outputLineId)
    {
        if (_cueShowSession is not { } session)
            return null;

        long videoSubmitted = 0;
        long videoLateTotal = 0;
        var driven = false;
        foreach (var (compId, outs) in _cueVideoOutputs)
        {
            if (!outs.Any(o => o.LineId == outputLineId))
                continue;
            if (session.GetCompositionStats(compId) is not { } stats)
                continue;
            driven = true;
            videoSubmitted += stats.FramesSubmitted;
            // Pump overruns only - SlotOverflowFrames is normal master-aligned pacing (see the deck's
            // TryGetShowSessionLineHealthMetrics for the full rationale) and reported phantom drops.
            videoLateTotal += stats.PumpOverruns;
        }
        var videoLateRecent = driven
            ? OutputLineHealthEvaluator.RecentDelta(_lineHealthPrevVideoLate, outputLineId, videoLateTotal)
            : 0;

        // Audio: map the line to its app-owned shared runtime and sum this line's active cue-audio pump chunks
        // (enqueued/dropped). Closes the "audio-only cue line reports Idle" gap - an audio-only line now lights up
        // once a cue routes audio to its device. Device-addressed routes only (matches the session-side tracking).
        long audioEnqueued = 0;
        long audioDropped = 0;
        var deviceId = OutputManagement.DefinitionsSnapshot
            .OfType<PortAudioOutputDefinition>()
            .Any(d => d.Id == outputLineId)
                ? OutputAudioRouteDeviceIds.PortAudio(outputLineId)
                : null;
        if (deviceId is not null
            && session.TryGetActiveAudioPumpStats(deviceId, out var audio))
        {
            driven = true;
            audioEnqueued = audio.Enqueued;
            audioDropped = OutputLineHealthEvaluator.RecentDelta(
                _lineHealthPrevAudioDropped, outputLineId, audio.Dropped);
        }

        if (!driven || videoSubmitted + audioEnqueued == 0)
            return null;

        var state = OutputLineHealthEvaluator.Score(videoLateRecent, audioDropped);
        return new OutputLineHealthEvaluator.LineHealthMetrics(
            state, videoSubmitted, videoLateRecent, 0, 0, audioEnqueued, audioDropped);
    }

    /// <summary>Watches structural parts of the selected cue list that are compiled into a ShowDocument:
    /// cue nodes, composition definitions, and composition/output bindings. Mapping geometry has its own live
    /// update path and is deliberately excluded from reload-on-property-change so an editor drag cannot stop
    /// the running composition.</summary>
    private void SubscribeCueGraphForReload()
    {
        foreach (var collection in _subscribedCueNodeCollections)
            collection.CollectionChanged -= OnCueNodesChanged;
        _subscribedCueNodeCollections.Clear();
        if (_subscribedCueGraphList is not null)
        {
            _subscribedCueGraphList.Compositions.CollectionChanged -= OnCueCompositionsChanged;
            _subscribedCueGraphList.VideoOutputs.CollectionChanged -= OnCueOutputBindingsChanged;
        }
        foreach (var composition in _subscribedCueCompositions)
            composition.PropertyChanged -= OnCueCompositionPropertyChanged;
        foreach (var binding in _subscribedCueOutputBindings)
            binding.PropertyChanged -= OnCueOutputBindingPropertyChanged;
        _subscribedCueCompositions.Clear();
        _subscribedCueOutputBindings.Clear();

        _subscribedCueGraphList = CuePlayer.SelectedCueList;
        if (_subscribedCueGraphList is null)
            return;

        RebindCueNodeCollectionSubscriptions();
        _subscribedCueGraphList.Compositions.CollectionChanged += OnCueCompositionsChanged;
        _subscribedCueGraphList.VideoOutputs.CollectionChanged += OnCueOutputBindingsChanged;
        RebindCueGraphItemSubscriptions();
    }

    private void RebindCueNodeCollectionSubscriptions()
    {
        foreach (var collection in _subscribedCueNodeCollections)
            collection.CollectionChanged -= OnCueNodesChanged;
        _subscribedCueNodeCollections.Clear();
        if (_subscribedCueGraphList is null)
            return;

        void SubscribeTree(ObservableCollection<CueNodeViewModel> nodes)
        {
            if (_subscribedCueNodeCollections.Add(nodes))
                nodes.CollectionChanged += OnCueNodesChanged;
            foreach (var node in nodes)
                SubscribeTree(node.Children);
        }

        SubscribeTree(_subscribedCueGraphList.Nodes);
    }

    private void OnCueNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RebindCueNodeCollectionSubscriptions();
        ScheduleCueShowSessionReload(preserveMatchingCompositions: true);
    }

    private void OnCueCompositionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindCueGraphItemSubscriptions();
        ScheduleCueShowSessionReload(preserveMatchingCompositions: false);
    }

    private void OnCueOutputBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindCueGraphItemSubscriptions();
        FireAndLog(ReconcileCueOutputTopologyAsync(), "cue output binding reconciliation");
        ScheduleCueShowSessionReload(preserveMatchingCompositions: false);
    }

    private void RebindCueGraphItemSubscriptions()
    {
        foreach (var composition in _subscribedCueCompositions)
            composition.PropertyChanged -= OnCueCompositionPropertyChanged;
        foreach (var binding in _subscribedCueOutputBindings)
            binding.PropertyChanged -= OnCueOutputBindingPropertyChanged;
        _subscribedCueCompositions.Clear();
        _subscribedCueOutputBindings.Clear();

        if (_subscribedCueGraphList is null)
            return;
        foreach (var composition in _subscribedCueGraphList.Compositions)
        {
            _subscribedCueCompositions.Add(composition);
            composition.PropertyChanged += OnCueCompositionPropertyChanged;
        }
        foreach (var binding in _subscribedCueGraphList.VideoOutputs)
        {
            _subscribedCueOutputBindings.Add(binding);
            binding.PropertyChanged += OnCueOutputBindingPropertyChanged;
        }
    }

    private void OnCueCompositionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Video FX is pushed directly through ApplyCompositionMappingAsync while its editor is open.
        if (e.PropertyName is nameof(CueCompositionViewModel.VideoFx)
            or nameof(CueCompositionViewModel.VideoFxEnabled))
            return;
        ScheduleCueShowSessionReload(preserveMatchingCompositions: false);
    }

    private void OnCueOutputBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mapping edits are live-applied; LineRef is display-only. Device/composition reassignment needs a
        // fresh set of host leases and therefore a document reload.
        if (e.PropertyName is nameof(CueVideoOutputBindingViewModel.Mapping)
            or nameof(CueVideoOutputBindingViewModel.MappingEnabled)
            or nameof(CueVideoOutputBindingViewModel.LineRef))
            return;
        if (e.PropertyName is nameof(CueVideoOutputBindingViewModel.OutputLineId)
            or nameof(CueVideoOutputBindingViewModel.CompositionId))
            FireAndLog(ReconcileCueOutputTopologyAsync(), "cue output binding reassignment");
        ScheduleCueShowSessionReload(preserveMatchingCompositions: false);
    }

    /// <summary>Debounces a graph reload (UI thread) so a burst of edits - or loading a list, which adds many
    /// nodes - collapses into a single reload instead of one per node.</summary>
    private void ScheduleCueShowSessionReload(bool preserveMatchingCompositions = true)
    {
        if (_cueShowSession is null)
            return;
        MarkCueShowGraphDirty(preserveMatchingCompositions);
        if (Volatile.Read(ref _automaticReloadSuspendCount) > 0)
        {
            _cueReloadDebounce?.Stop();
            return;
        }
        _cueReloadDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cueReloadDebounce.Tick -= OnCueReloadDebounceTick;
        _cueReloadDebounce.Tick += OnCueReloadDebounceTick;
        _cueReloadDebounce.Stop();
        _cueReloadDebounce.Start(); // restart the window on each change → fires 300ms after the last edit
    }

    private void MarkCueShowGraphDirty(bool preserveMatchingCompositions)
    {
        if (!_cueShowGraphDirty)
            _cueReloadMayPreserveMatchingCompositions = preserveMatchingCompositions;
        else
            _cueReloadMayPreserveMatchingCompositions &= preserveMatchingCompositions;
        _cueShowGraphDirty = true;
    }

    private void OnCueReloadDebounceTick(object? sender, EventArgs e)
    {
        _cueReloadDebounce?.Stop();
        // Rebuilding the document swaps the graph and stops the active media cue. Matching compositions and their
        // visualizers can survive, but doing the graph swap on every in-place edit would still interrupt media. While
        // something is playing, defer: leave the graph marked dirty (EnsureCueShowSessionCurrentAsync rebuilds it
        // before the next fire), so the edit lands on the next GO instead of interrupting the running cue. A live
        // text cue additionally re-renders in place (UpdateActiveCueTextCallback) so its change shows immediately.
        // Use the VM's authoritative active-cue set, NOT the session clock's IsRunning: a video-only text/held clip
        // may report IsRunning=false while clearly on screen, which let the rebuild through and stopped the cue on
        // every edit. HasActiveCues is true from fire until the cue actually ends.
        if (CuePlayer.HasActiveCues)
        {
            _cueReloadDebounce?.Start(); // re-check shortly; reload once playback stops (keeps the edit pending)
            return;
        }

        FireAndLog(ReloadCueShowSessionAsync(), "cue ShowSession reload (debounced edit)");
    }

    /// <summary>Flushes a pending debounced cue-model edit before firing. Media execution runs on a worker
    /// thread, while output acquisition and document mapping must run on Avalonia's UI thread - the load
    /// itself is awaited (never sync-blocked, NXT-21).</summary>
    private async Task EnsureCueShowSessionCurrentAsync()
    {
        if (!_cueShowGraphDirty)
            return;
        if (Volatile.Read(ref _automaticReloadSuspendCount) > 0)
            return; // the restore owner performs one explicit reload after output runtimes are ready

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_cueShowGraphDirty)
                return Task.CompletedTask;
            _cueReloadDebounce?.Stop();
            return ReloadCueShowSessionAsync();
        });

        if (_cueShowGraphDirty)
            throw new InvalidOperationException("The ShowSession cue graph could not be refreshed before firing.");
    }

    /// <summary>Maps the session standby's clip-preparation state to the cue-list's badge state. The two enums are
    /// value-identical today, but a switch keeps them decoupled.</summary>
    private static Playback.PreparedCueState MapClipPreparationState(ClipPreparationState state) => state switch
    {
        ClipPreparationState.Preparing => Playback.PreparedCueState.Preparing,
        ClipPreparationState.Ready => Playback.PreparedCueState.Ready,
        ClipPreparationState.Stale => Playback.PreparedCueState.Stale,
        ClipPreparationState.Failed => Playback.PreparedCueState.Failed,
        _ => Playback.PreparedCueState.Idle,
    };

    /// <summary>Runs a re-back transport op (stop/pause/seek/cancel) and logs any failure, so a fault surfaces in
    /// the console/log instead of only as a UI notification (or being silently swallowed by the caller).</summary>
    private async Task GuardedCueShowOp(string op, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { Trace.LogError(ex, "HaPlay: cue ShowSession {Op} failed", op); }
    }

    private async Task ApplyCueShowOutputMappingAsync(
        Guid compositionId, Guid outputLineId, CueOutputMapping? mapping)
    {
        var applied = _cueShowSession is not null
            && await _cueShowSession.ApplyOutputMappingAsync(
                compositionId.ToString(), outputLineId.ToString("N"),
                HaPlayShowMapper.ToClipOutputMapping(mapping)).ConfigureAwait(false);
        if (!applied)
        {
            Trace.LogWarning(
                "HaPlay: output mapping update missed composition {Composition} / line {Line}",
                compositionId, outputLineId);
            await RunOnUiThreadAsync(async () =>
            {
                MarkCueShowGraphDirty(preserveMatchingCompositions: false);
                CuePlayer.StatusMessage =
                    $"Output mapping pending: line {DescribeOutputLine(outputLineId)} is not attached.";
                await ReconcileCueOutputTopologyAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
            return;
        }

        Trace.LogInformation(
            "HaPlay: output mapping applied - composition={Composition} line={Line} mapping={Mapping}",
            compositionId, outputLineId, DescribeCueOutputMapping(mapping));
    }

    private static string DescribeCueOutputMapping(CueOutputMapping? mapping)
    {
        if (mapping is null)
            return "raw-canvas";
        return $"out={mapping.OutputWidth?.ToString() ?? "canvas"}x{mapping.OutputHeight?.ToString() ?? "canvas"} " +
               $"sections=[{string.Join("; ", mapping.Sections.Select(s =>
                   $"src({s.SrcX:0.###},{s.SrcY:0.###},{s.SrcWidth:0.###},{s.SrcHeight:0.###})" +
                   $"->dst({s.DestX:0.#},{s.DestY:0.#},{s.DestWidth:0.#},{s.DestHeight:0.#})"))}]";
    }


    private static IEnumerable<CueNode> FlattenCueNodes(IEnumerable<CueNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is CueGroupNode g)
                foreach (var child in FlattenCueNodes(g.Children))
                    yield return child;
        }
    }

    private async Task ApplyCueShowCompositionMappingAsync(Guid compositionId, CueOutputMapping? mapping)
    {
        if (_cueShowSession is null
            || !await _cueShowSession.ApplyCompositionMappingAsync(
                compositionId.ToString(), HaPlayShowMapper.ToClipOutputMapping(mapping)).ConfigureAwait(false))
        {
            Trace.LogWarning("HaPlay: composition mapping update missed composition {Composition}", compositionId);
        }
    }

    /// <summary>Detaches a cue-held output before its backing runtime is reconfigured or destroyed. The output
    /// manager awaits this callback, so no composition pump can submit into the old sink after teardown starts.</summary>
    private Task OnOutputLineReconfiguringAsync(OutputLineViewModel line) =>
        RunOnUiThreadAsync(() => DetachCueOutputLineAsync(line.Definition.Id));

    /// <summary>Reacquires a hot-reconfigured output without rebuilding the show document or interrupting active
    /// cues. Project restore uses <see cref="ReloadAfterOutputRestoreAsync"/> after all new runtimes have started.</summary>
    private Task OnOutputLineReconfiguredAsync(OutputLineViewModel line) =>
        RunOnUiThreadAsync(ReconcileCueOutputTopologyAsync);

    private void OnOutputRoutingTopologyChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        // CollectionChanged raises before every observer has rebuilt its derived snapshots. Post the
        // reconciliation to the back of the UI queue so it sees the committed output topology.
        Dispatcher.UIThread.Post(() =>
        {
            MarkCueShowGraphDirty(preserveMatchingCompositions: false);
            FireAndLog(ReconcileCueOutputTopologyAsync(), "cue output topology reconciliation");
        }, DispatcherPriority.Background);
    }

    private static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task DetachCueOutputLineAsync(Guid lineId)
    {
        if (_cueShowSession is not { } session || !_cueAcquiredVideoLines.Contains(lineId))
            return;

        foreach (var (compositionId, outputs) in _cueVideoOutputs.ToArray())
        {
            if (!outputs.Any(output => output.LineId == lineId))
                continue;
            await session.RemoveCompositionOutputAsync(compositionId, lineId.ToString("N"))
                .ConfigureAwait(true);

            var remaining = outputs.Where(output => output.LineId != lineId).ToArray();
            if (remaining.Length == 0)
                _cueVideoOutputs.Remove(compositionId);
            else
                _cueVideoOutputs[compositionId] = remaining;
        }

        OutputManagement.ReleaseVideoOutputForLine(lineId);
        _cueAcquiredVideoLines.Remove(lineId);
        Trace.LogInformation("HaPlay: cue output detached before runtime teardown - line={Line}", lineId);
    }

    private async Task AttachCueOutputLineAsync(Guid lineId)
    {
        if (_cueShowSession is not { } session || _cueAcquiredVideoLines.Contains(lineId))
            return;
        var model = CuePlayer.SelectedCueList?.ToModel();
        var binding = model?.VideoOutputs.FirstOrDefault(candidate => candidate.OutputLineId == lineId);
        if (model is null || binding is null)
            return;

        var output = OutputManagement.AcquireVideoOutputForLine(lineId);
        if (output is null)
        {
            Trace.LogWarning("HaPlay: cue output could not be reacquired after runtime reconfigure - line={Line}", lineId);
            return;
        }

        var effectiveMappings = HaPlayShowMapper.ResolveEffectiveVideoOutputMappings(
            model, OutputManagement.DefinitionsSnapshot);
        var mapping = effectiveMappings.GetValueOrDefault(binding.Id);
        var compositionId = binding.CompositionId.ToString();
        var lease = new ClipCompositionOutputLease(
            lineId.ToString("N"),
            OutputManagement.DefinitionsSnapshot.FirstOrDefault(definition => definition.Id == lineId)?.DisplayName
                ?? lineId.ToString("N"),
            output,
            DisposeOutputOnRuntimeDispose: false,
            Mapping: HaPlayShowMapper.ToClipOutputMapping(mapping));

        if (!await session.AddCompositionOutputAsync(compositionId, lease).ConfigureAwait(true))
        {
            OutputManagement.ReleaseVideoOutputForLine(lineId);
            Trace.LogWarning(
                "HaPlay: cue output reattach missed composition {Composition} after runtime reconfigure - line={Line}",
                compositionId, lineId);
            return;
        }

        _cueAcquiredVideoLines.Add(lineId);
        var attached = new CueShowVideoOutput(lineId, output, mapping);
        _cueVideoOutputs[compositionId] = _cueVideoOutputs.TryGetValue(compositionId, out var existing)
            ? [.. existing, attached]
            : [attached];
        Trace.LogInformation(
            "HaPlay: cue output reattached after runtime reconfigure - composition={Composition} line={Line}",
            compositionId, lineId);
    }

    /// <summary>Single owner for binding ↔ live output-lease reconciliation. It is safe while cues are
    /// active because ShowSession adds/removes composition outputs without rebuilding the document.</summary>
    private async Task ReconcileCueOutputTopologyAsync()
    {
        if (_cueShowSession is null || CuePlayer.SelectedCueList is null)
            return;

        await _cueOutputReconcileGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ReconcileCueOutputTopologyCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _cueOutputReconcileGate.Release();
        }
    }

    private async Task ReconcileCueOutputTopologyCoreAsync()
    {
        if (_cueShowSession is null || CuePlayer.SelectedCueList is null)
            return;

        var model = CuePlayer.SelectedCueList.ToModel();
        var availableLines = OutputManagement.DefinitionsSnapshot.Select(d => d.Id).ToHashSet();
        var desiredByLine = model.VideoOutputs
            .Where(binding => binding.OutputLineId != Guid.Empty && availableLines.Contains(binding.OutputLineId))
            .GroupBy(binding => binding.OutputLineId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var (compositionId, outputs) in _cueVideoOutputs.ToArray())
        {
            foreach (var output in outputs)
            {
                if (desiredByLine.TryGetValue(output.LineId, out var desired)
                    && string.Equals(compositionId, desired.CompositionId.ToString(), StringComparison.Ordinal))
                    continue;
                await DetachCueOutputLineAsync(output.LineId).ConfigureAwait(true);
            }
        }

        foreach (var lineId in desiredByLine.Keys)
            if (!_cueAcquiredVideoLines.Contains(lineId))
                await AttachCueOutputLineAsync(lineId).ConfigureAwait(true);

        var unavailable = model.VideoOutputs
            .Where(binding => binding.OutputLineId != Guid.Empty
                              && (!_cueAcquiredVideoLines.Contains(binding.OutputLineId)
                                  || !availableLines.Contains(binding.OutputLineId)))
            .Select(binding => DescribeOutputLine(binding.OutputLineId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unavailable.Length > 0)
        {
            _cueOutputAvailabilityWarningActive = true;
            CuePlayer.StatusMessage = $"Cue output unavailable: {string.Join(", ", unavailable)}.";
            Trace.LogWarning("HaPlay: cue output reconciliation left unavailable lines: {Lines}", unavailable);
        }
        else if (_cueOutputAvailabilityWarningActive)
        {
            _cueOutputAvailabilityWarningActive = false;
            CuePlayer.StatusMessage = "Cue outputs reconnected.";
        }
    }

    private string DescribeOutputLine(Guid lineId) =>
        OutputManagement.DefinitionsSnapshot.FirstOrDefault(definition => definition.Id == lineId)?.DisplayName
        ?? lineId.ToString("N");

    /// <summary>Forces a document/output-lease rebuild after project restore has created all output runtimes.</summary>
    public Task ReloadAfterOutputRestoreAsync() => RunOnUiThreadAsync(async () =>
    {
        MarkCueShowGraphDirty(preserveMatchingCompositions: false);
        _cueReloadDebounce?.Stop();
        await ReloadCueShowSessionAsync().ConfigureAwait(true);
        if (_cueShowGraphDirty)
            throw new InvalidOperationException("The cue session could not attach the restored output runtimes.");
    });

    /// <summary>Defers automatic cue-document reloads while a project replaces/starts output runtimes.
    /// Disposing the scope leaves the graph dirty; the project loader follows it with one awaited
    /// <see cref="ReloadAfterOutputRestoreAsync"/> call.</summary>
    public async Task<IDisposable> SuspendAutomaticReloadsForProjectRestoreAsync()
    {
        Interlocked.Increment(ref _automaticReloadSuspendCount);
        _cueReloadDebounce?.Stop();
        try
        {
            // A reload already running before the suspension may still be constructing a compositor/window.
            // Drain it before the project loader starts restored output runtimes, closing the other half of the
            // SDL/EGL race (suppressing only newly-scheduled reloads would leave this one overlapping).
            if (_cueReloadTask is { } inFlight)
                await inFlight.ConfigureAwait(true);
            return new ReloadSuspension(this);
        }
        catch
        {
            Interlocked.Decrement(ref _automaticReloadSuspendCount);
            throw;
        }
    }

    private sealed class ReloadSuspension(CueShowSessionCoordinator owner) : IDisposable
    {
        private CueShowSessionCoordinator? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                Interlocked.Decrement(ref current._automaticReloadSuspendCount);
        }
    }

    /// <summary>Preserves the cue graph's actual failure message when its policy returns a Failed status
    /// instead of throwing. Without this lookup the UI and console only said "not ready (Failed)", hiding
    /// device/open errors that explain an immediate cue termination.</summary>
    private async Task<string> DescribeCueShowFailureAsync(Guid cueId, CueExecutionStatus status)
    {
        if (_cueShowSession is null)
            return $"cue session unavailable ({status})";

        try
        {
            var log = await _cueShowSession.GetCueExecutionLogAsync().ConfigureAwait(false);
            var entry = log.LastOrDefault(x => string.Equals(x.CueId, cueId.ToString(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(entry?.Message))
                return entry.Message!;
        }
        catch (Exception ex)
        {
            Trace.LogDebug(ex, "HaPlay: could not read ShowSession execution detail for cue {Cue}", cueId);
        }

        return $"cue did not fire ({status})";
    }

    /// <summary>(UI thread) Records a re-back cue as started - drives the GUI's Triggered/now-playing state via
    /// <see cref="CuePlayerViewModel.OnCueStarted"/> and starts the snapshot poll that feeds progress/end. The
    /// ShowSession transport raises no cue lifecycle events, so the poll (below) is how the now-playing countdown
    /// advances and rows clear.</summary>
    private void MarkCueShowCueStarted(Guid cueId, string? runtimeGroup = null)
    {
        var group = runtimeGroup ?? _cueGroupByCueId.GetValueOrDefault(cueId, ShowSession.DefaultGroup);
        // A new cue replacing a still-active one on the same group → end the prior so its row doesn't orphan.
        if (_cueShowActiveByGroup.TryGetValue(group, out var prior) && prior.CueId != cueId)
            CuePlayer.OnCueEnded(prior.CueId);
        _cueShowActiveByGroup[group] = new CueShowActiveState { CueId = cueId };
        CuePlayer.OnCueStarted(cueId);
        Trace.LogInformation("HaPlay: cue {Cue} started in group '{Group}'", cueId, group);

        _cueShowProgressPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _cueShowProgressPoll.Tick -= OnCueShowProgressPollTick;
        _cueShowProgressPoll.Tick += OnCueShowProgressPollTick;
        _cueShowProgressPoll.Start();
    }

    /// <summary>A stable per-cue runtime group for a simultaneously-fired authored group. Stability means re-firing
    /// the same cue replaces its own prior run, while distinct siblings never displace each other.</summary>
    internal static string BuildSimultaneousRuntimeGroup(Guid cueId) => $"simultaneous:{cueId:N}";

    /// <summary>Returns the group the cue is active on, falling back to its authored group while idle. Runtime group
    /// resolution keeps per-cue and aggregate seeks targeting independently-fired simultaneous children.</summary>
    private string ResolveCueShowRuntimeGroup(Guid cueId)
    {
        foreach (var (group, state) in _cueShowActiveByGroup)
            if (state.CueId == cueId)
                return group;
        return _cueGroupByCueId.GetValueOrDefault(cueId, ShowSession.DefaultGroup);
    }

    /// <summary>Polls the per-group session snapshot to drive <see cref="CuePlayerViewModel.OnCueProgress"/> (the
    /// now-playing countdown/scrubber) while a group runs, and <see cref="CuePlayerViewModel.OnCueEnded"/> when it
    /// goes idle (natural end / Stop / Cancel). Per group, so concurrent cues on different groups track
    /// independently; a looping/freeze cue keeps its group "running" and so stays correctly active.</summary>
    private void OnCueShowProgressPollTick(object? sender, EventArgs e)
    {
        try
        {
            if (_cueShowSession is null || _cueShowActiveByGroup.Count == 0)
            {
                _cueShowProgressPoll?.Stop();
                return;
            }
            var snapshot = _cueShowSession.Snapshot();
            var byGroup = snapshot.ToDictionary(s => s.GroupId, StringComparer.Ordinal);
            foreach (var (group, st) in _cueShowActiveByGroup.ToArray())
            {
                // Use IsActive (the group still holds a clip), NOT IsRunning (clock advancing): a video-only
                // held/text clip is on screen with IsRunning=false, and keying end-detection off IsRunning marked
                // such a cue "ended" after the grace, which cleared now-playing AND let an edit's document rebuild
                // through (it tore the clip down abruptly). IsActive stays true until the clip is really released.
                if (byGroup.TryGetValue(group, out var s) && s.IsActive)
                {
                    st.ObservedRunning = true;
                    st.NotRunningTicks = 0;
                    CuePlayer.OnCueProgress(new CuePlaybackProgress(st.CueId, s.ClipPosition, s.ClipDuration));
                    continue;
                }

                // No active clip. End it only if it was actually seen active (so it really ended), OR it never
                // committed within the warmup grace (~3s at 200ms) - the latter is logged as it indicates a clip
                // that never became active (e.g. a failed open).
                if (st.ObservedRunning || ++st.NotRunningTicks > 15)
                {
                    if (!st.ObservedRunning)
                        Trace.LogWarning(
                            "HaPlay: cue {Cue} never started running within grace - group '{Group}' present={Present} (snapshot: [{Snap}])",
                            st.CueId, group, byGroup.ContainsKey(group),
                            string.Join(", ", snapshot.Select(x => $"{x.GroupId}:run={x.IsRunning}:pos={x.ClipPosition}:dur={x.ClipDuration}")));
                    CuePlayer.OnCueEnded(st.CueId);
                    _cueShowActiveByGroup.Remove(group);
                }
            }
            if (_cueShowActiveByGroup.Count == 0)
                _cueShowProgressPoll?.Stop();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: cue ShowSession progress poll failed");
        }
    }

    /// <summary>Starts (or re-arms) the poll that drives per-tile soundboard countdowns from the session's voice
    /// playheads - the engine had a <c>SoundProgress</c> event; the re-back has none, so we poll instead.</summary>
    private void StartSoundboardProgressPoll()
    {
        _soundboardProgressPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _soundboardProgressPoll.Tick -= OnSoundboardProgressPollTick;
        _soundboardProgressPoll.Tick += OnSoundboardProgressPollTick;
        _soundboardProgressPoll.Start();
    }

    private void OnSoundboardProgressPollTick(object? sender, EventArgs e)
    {
        try
        {
            if (_cueShowSession is null)
            {
                _soundboardProgressPoll?.Stop();
                return;
            }
            var voices = _cueShowSession.GetVoiceProgress();

            // Reconcile: any tile we started whose voice is gone ended WITHOUT a VoiceEnded event -
            // that's how fade-outs finish (the ramp releases the voice silently). Reset those tiles
            // here or they stay stuck in the playing/fading state and can never be re-triggered.
            if (_soundboardActiveTiles.Count > 0)
            {
                var live = new HashSet<Guid>();
                foreach (var v in voices)
                    if (Guid.TryParse(v.VoiceId, out var liveId))
                        live.Add(liveId);
                foreach (var tileId in _soundboardActiveTiles.Where(t => !live.Contains(t)).ToArray())
                {
                    _soundboardActiveTiles.Remove(tileId);
                    Soundboard.OnSoundEnded(tileId);
                }
            }

            if (voices.Count == 0)
            {
                _soundboardProgressPoll?.Stop();
                return;
            }
            foreach (var v in voices)
                if (Guid.TryParse(v.VoiceId, out var tileId))
                    Soundboard.OnSoundProgress(new SoundboardSoundProgress(tileId, v.Position, v.Duration, null));
        }
        catch (Exception ex)
        {
            Trace.LogDebug(ex, "HaPlay: soundboard voice-progress poll");
        }
    }

    // The in-flight reload (UI thread only). Reloads await the session's LoadDocumentAsync (NXT-21), so a
    // second trigger can arrive mid-reload - it marks the graph dirty and shares this task; the runner loops
    // until clean, so line holds (single-holder!) are never acquired by two overlapping passes.
    private Task? _cueReloadTask;

    /// <summary>(UI thread) Loads the selected cue list into the re-back <see cref="ShowSession"/> (no-op when
    /// disabled). Single-runner: a reload triggered while one is in flight is coalesced into it (the runner
    /// re-checks the dirty flag before finishing). The returned task completes when the graph is current.</summary>
    private Task ReloadCueShowSessionAsync()
    {
        if (_cueReloadTask is { } inFlight)
            return inFlight;

        // Defensive default for direct callers: an unmarked reload is a full rebuild. Normal edit/list/restore
        // paths mark the graph first with the appropriate preservation policy.
        if (!_cueShowGraphDirty)
            MarkCueShowGraphDirty(preserveMatchingCompositions: false);

        var run = RunCueReloadLoopAsync();
        _cueReloadTask = run.IsCompleted ? null : run;
        return run;
    }

    private async Task RunCueReloadLoopAsync()
    {
        try
        {
            do
            {
                var preserveMatchingCompositions = _cueReloadMayPreserveMatchingCompositions;
                _cueShowGraphDirty = false; // this pass captures the current model; a mid-pass edit re-marks it
                _cueReloadMayPreserveMatchingCompositions = true;
                if (!await ReloadCueShowSessionOnceAsync(preserveMatchingCompositions).ConfigureAwait(true))
                {
                    // Failed: stay dirty (the fire-path flush surfaces it) without weakening a full-rebuild
                    // requirement if another edit arrived during the failed pass.
                    if (!_cueShowGraphDirty)
                        _cueReloadMayPreserveMatchingCompositions = preserveMatchingCompositions;
                    else
                        _cueReloadMayPreserveMatchingCompositions &= preserveMatchingCompositions;
                    _cueShowGraphDirty = true;
                    return;
                }
            }
            while (_cueShowGraphDirty);
        }
        finally
        {
            _cueReloadTask = null;
        }
    }

    /// <summary>One reload pass (UI thread). Returns false on failure (logged; the graph stays dirty).</summary>
    private async Task<bool> ReloadCueShowSessionOnceAsync(bool preserveMatchingCompositions)
    {
        if (_cueShowSession is not { } session || CuePlayer.SelectedCueList is not { } list)
            return true; // nothing to load - not a failure
        try
        {
            var model = list.ToModel();

            // Mapping=null historically meant "raw full canvas" to the runtime, but the layout editor presents
            // an unmapped output as an implicit native-sized tile (for example 1280x720 at the top-left of a
            // 1920x1080 composition). Feeding null here made the running output disagree with the editor until
            // the first Save, at which point it appeared to resize from the full composition to the tile. Resolve
            // those same implicit tiles up front so initial playback and the editor describe one layout.
            var effectiveMappings = HaPlayShowMapper.ResolveEffectiveVideoOutputMappings(
                model, OutputManagement.DefinitionsSnapshot);

            // (UI thread) Line holds across the reload (NXT-20): lines still bound by the new model KEEP their
            // hold and output - no release→re-acquire churn, so the idle slate never touches a line that stays
            // in use. Lines the new model no longer binds are DETACHED from the live compositions FIRST, then
            // released: the old composition pump outlives its clip and keeps submitting, and releasing first
            // reconfigures the sink (idle slate) while frames still flow into it - the format-mismatch flood
            // the deck stop path documents. Acquisition stays up front so ShowSession's video factory remains a
            // pure lookup during the load.
            var previousOutputsByLine = new Dictionary<Guid, IVideoOutput>();
            foreach (var outs in _cueVideoOutputs.Values)
                foreach (var o in outs)
                    previousOutputsByLine.TryAdd(o.LineId, o.Output);
            var neededLines = model.VideoOutputs
                .Where(b => b.OutputLineId != Guid.Empty)
                .Select(b => b.OutputLineId)
                .ToHashSet();

            foreach (var heldLine in _cueAcquiredVideoLines.Where(l => !neededLines.Contains(l)).ToList())
            {
                foreach (var (compId, outs) in _cueVideoOutputs)
                {
                    if (!outs.Any(o => o.LineId == heldLine))
                        continue;
                    try
                    {
                        await session.RemoveCompositionOutputAsync(compId, heldLine.ToString("N"))
                            .ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Trace.LogWarning(ex, "HaPlay: ShowSession detach of dropped line {Line} failed", heldLine);
                    }
                }

                OutputManagement.ReleaseVideoOutputForLine(heldLine);
                _cueAcquiredVideoLines.Remove(heldLine);
            }

            var videoOutputs = new Dictionary<string, CueShowVideoOutput[]>(StringComparer.Ordinal);
            var usedThisPass = new HashSet<Guid>();
            var unavailableLines = new List<string>();
            foreach (var binding in model.VideoOutputs)
            {
                if (binding.OutputLineId == Guid.Empty)
                    continue;
                if (!usedThisPass.Add(binding.OutputLineId))
                    continue; // one physical line drives one binding (parity: a duplicate was dropped before too)

                IVideoOutput? output;
                if (_cueAcquiredVideoLines.Contains(binding.OutputLineId))
                {
                    output = previousOutputsByLine.GetValueOrDefault(binding.OutputLineId);
                    if (output is null)
                    {
                        // Stale hold with no tracked output - resync by releasing and re-acquiring.
                        OutputManagement.ReleaseVideoOutputForLine(binding.OutputLineId);
                        _cueAcquiredVideoLines.Remove(binding.OutputLineId);
                        output = OutputManagement.AcquireVideoOutputForLine(binding.OutputLineId);
                        if (output is not null)
                            _cueAcquiredVideoLines.Add(binding.OutputLineId);
                    }
                }
                else
                {
                    output = OutputManagement.AcquireVideoOutputForLine(binding.OutputLineId);
                    if (output is not null)
                        _cueAcquiredVideoLines.Add(binding.OutputLineId);
                }

                if (output is null)
                {
                    unavailableLines.Add(DescribeOutputLine(binding.OutputLineId));
                    continue;
                }
                var key = binding.CompositionId.ToString();
                var target = new CueShowVideoOutput(
                    binding.OutputLineId,
                    output,
                    effectiveMappings.GetValueOrDefault(binding.Id));
                videoOutputs[key] = videoOutputs.TryGetValue(key, out var existing) ? [.. existing, target] : [target];
            }
            _cueVideoOutputs = videoOutputs;

            var doc = HaPlayShowMapper.ToShowDocument(model, OutputManagement.DefinitionsSnapshot);
            // NXT-21: await the load - blocking the UI thread on the session dispatcher would turn any
            // dispatcher stall into a whole-app freeze.
            await session.LoadDocumentAsync(doc, preserveMatchingCompositions).ConfigureAwait(true);

            if (unavailableLines.Count > 0)
            {
                _cueOutputAvailabilityWarningActive = true;
                CuePlayer.StatusMessage =
                    $"Cue output unavailable: {string.Join(", ", unavailableLines.Distinct(StringComparer.Ordinal))}.";
                Trace.LogWarning(
                    "HaPlay: cue ShowSession loaded with unavailable output lines: {Lines}", unavailableLines);
            }

            // Node-only edits keep same-shaped compositions alive. Full output-topology rebuilds now reattach
            // explicitly-persistent visualizer slots to the replacement composition without restarting their
            // source. Reconcile only a slot that truly could not survive (composition removed/incompatible).
            foreach (var (compositionId, _) in _cueVisualizerSources.ToArray())
            {
                if (await session.HasCompositionVisualizerAsync(compositionId.ToString()).ConfigureAwait(true))
                    continue;
                _cueVisualizerSources.TryRemove(compositionId, out _);
                CuePlayer.OnVisualizerLayerCleared(compositionId);
            }
            // Map each cue → its transport group so the progress poll can attribute per-group snapshot state.
            var groupByCue = new Dictionary<Guid, string>();
            foreach (var c in doc.Cues)
                if (Guid.TryParse(c.Id, out var cueGuid))
                    groupByCue[cueGuid] = c.GroupId ?? ShowSession.DefaultGroup;
            _cueGroupByCueId = groupByCue;
            _cueShowBoundCueIds = doc.Clips
                .Select(clip => Guid.TryParse(clip.CueId, out var cueId) ? cueId : Guid.Empty)
                .Where(cueId => cueId != Guid.Empty)
                .ToHashSet();
            Trace.LogInformation(
                "HaPlay: cue ShowSession reloaded - {Cues} cues, {Clips} clips, {Comps} compositions, {Lines} video lines, preserveVisualizers={Preserve}",
                doc.Cues.Count, doc.Clips.Count, doc.Compositions.Count, _cueAcquiredVideoLines.Count,
                preserveMatchingCompositions);
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession LoadDocument from cue list failed");
            return false;
        }
    }

    /// <summary>A headless cue clip reached its natural end (<see cref="ShowSession.ClipNaturallyEnded"/>) -
    /// run the cue auto-follow.</summary>
    private async Task OnCueClipNaturallyEndedAsync(Guid cueId)
    {
        try
        {
            await CuePlayer.OnMediaCueNaturallyEndedAsync(cueId);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "OnCueClipNaturallyEndedAsync: cue auto-follow failed");
            CuePlayer.StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowFailedFormat), ex.Message);
        }
    }

    /// <summary>Pre-roll warm-through: flushes pending edits (only while nothing is playing - the flush is a full
    /// LoadDocument that would tear down a running cue) then warms the session's upcoming cue(s). No-op when the
    /// session is unavailable. See <c>MainViewModel.RefreshCuePreRollAsync</c> for the trigger/coalescing.</summary>
    public async Task WarmUpcomingForPreRollAsync(int count)
    {
        if (_cueShowSession is not { } showSession)
            return;
        // Prepare through the SAME ShowSession graph that fires. Flush pending edits first, then warm
        // its upcoming cue(s) - but NOT while a cue is playing: the flush is a full LoadDocument that
        // rebuilds the graph and tears down the running cue (pre-roll refresh fires per edit). While
        // playing, the edit lands live (the frame swap) or on the next fire's own flush; warm the
        // current graph as-is.
        if (!CuePlayer.HasActiveCues)
            await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
        await showSession.WarmUpcomingAsync(count: count).ConfigureAwait(false);
    }

    /// <summary>Stops every soundboard voice (project-load teardown). No-op when the session is unavailable.</summary>
    public Task StopAllVoicesAsync() =>
        _cueShowSession is { } session ? session.StopAllVoicesAsync() : Task.CompletedTask;

    /// <summary>Stops active cue clips before a full project replacement changes their document and outputs.</summary>
    public Task StopAllPlaybackAsync() =>
        _cueShowSession is { } session ? session.StopAllAsync() : Task.CompletedTask;

    /// <summary>Best-effort shutdown teardown of the cue re-back (no-op when unavailable): release its video
    /// leases (UI thread) and dispose the headless <see cref="ShowSession"/>. The session disposes on its own
    /// dispatcher (no Avalonia marshalling), so the bounded block here can't deadlock the UI thread. Players are
    /// reclaimed by process-exit, as before.</summary>
    public void ShutdownCleanup()
    {
        if (_cueShowSession is null)
            return;
        try
        {
            OutputManagement.OutputLineReconfiguringAsync -= OnOutputLineReconfiguringAsync;
            OutputManagement.OutputLineReconfiguredAsync -= OnOutputLineReconfiguredAsync;
            OutputManagement.RoutingTopologyChanged -= OnOutputRoutingTopologyChanged;
            foreach (var held in _cueAcquiredVideoLines)
                OutputManagement.ReleaseVideoOutputForLine(held);
            _cueAcquiredVideoLines.Clear();
            _cueVisualizerSources.Clear();

            var session = _cueShowSession;
            _cueShowSession = null;
            session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession shutdown cleanup");
        }
    }

    /// <summary>Maps the cue editor's top-left-origin placement exactly like a regular media layer.</summary>
    private static VideoPlacementSpec ToVisualizerPlacement(string compositionId, CueVideoPlacement? placement)
    {
        if (placement is null)
            return new VideoPlacementSpec(compositionId, 0, Placement: "stretch");

        var mapped = HaPlayShowMapper.ToShowVideoPlacement(placement);
        return new VideoPlacementSpec(
            compositionId,
            placement.LayerIndex,
            Opacity: Math.Clamp(mapped.Opacity, 0, 1),
            Placement: mapped.Fit,
            DestX: mapped.DestX,
            DestY: mapped.DestY,
            DestWidth: mapped.DestWidth,
            DestHeight: mapped.DestHeight,
            CropLeft: mapped.CropLeft,
            CropTop: mapped.CropTop,
            CropRight: mapped.CropRight,
            CropBottom: mapped.CropBottom,
            RotationDegrees: mapped.RotationDegrees,
            VideoFx: mapped.VideoFx);
    }
}
