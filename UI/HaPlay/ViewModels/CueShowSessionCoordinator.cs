using System.Collections.Specialized;
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

    /// <summary>The cue session's audio-output factory arm for encode ("file-audio:{lineId}") routes.
    /// Runs on the session dispatcher: resolves through OutputManagement's THREAD-SAFE runtime lookup
    /// (never the UI-bound Outputs collection). Rate mismatches get a per-fire resampler the session
    /// owns; the carrier itself is borrowed and released via the lease hook.</summary>
    private ClipAudioOutputLease? BuildCueEncodeAudioLease(string deviceId, AudioFormat format)
    {
        if (!deviceId.StartsWith("file-audio:", StringComparison.Ordinal)
            || !Guid.TryParse(deviceId["file-audio:".Length..], out var lineId))
        {
            return null; // not an encode route - the session opens it as a normal backend device
        }

        if (OutputManagement.TryAcquireEncodeAudioByLineId(lineId) is not { } rawCarrier)
            return null; // disarmed/unknown - the cue plays without this route

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

            OutputManagement.ReleaseEncodeAudioByLineId(lineId);
        }

        if (carrier.Format.SampleRate == format.SampleRate && carrier.Format.Channels == format.Channels)
            return new ClipAudioOutputLease(carrier, DisposeOutputOnRuntimeDispose: false, Release: Release);

        // Rate (or short-matrix channel) mismatch: adapt the clip into the carrier's format. The session
        // disposes only the resampler wrapper; the effect wrapper + carrier retire via the hook above.
        return new ClipAudioOutputLease(
            S.Media.Decode.FFmpeg.Audio.ResamplingAudioOutput.Wrap(carrier, format),
            DisposeOutputOnRuntimeDispose: true,
            Release: Release);
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
    // The selected cue list's node collection we watch so an in-place edit (add/remove cue) reloads the stale
    // ShowSession graph - otherwise only a SelectedCueList *switch* reloaded, so a freshly-built/edited cue fired
    // "cue '…' is not registered". Reloads are debounced so a burst of edits (or loading a list) collapses to one.
    private INotifyCollectionChanged? _subscribedCueNodes;
    private CueListEditorViewModel? _subscribedCueGraphList;
    private readonly HashSet<CueCompositionViewModel> _subscribedCueCompositions = new();
    private readonly HashSet<CueVideoOutputBindingViewModel> _subscribedCueOutputBindings = new();
    private DispatcherTimer? _cueReloadDebounce;
    // Set as soon as the GUI cue model changes and cleared only after the reload commits it. Fire paths
    // flush a dirty graph synchronously on the UI thread, so GO cannot race the 300 ms edit debounce and execute
    // an older cue definition (notably a media cue captured before its Source was assigned).
    private volatile bool _cueShowGraphDirty;
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
                audioOutputFactory: BuildCueEncodeAudioLease,
                // Effect buses (Phase 4): tags + cover art for the metadata hub (visualizers/overlays).
                metadataProbe: S.Media.Decode.FFmpeg.MediaTagProbe.TryRead);

            CuePlayer.CueStandbyInvalidated += (_, _) => ScheduleCueShowSessionReload();
            CuePlayer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CuePlayer.SelectedCueList))
                {
                    SubscribeCueGraphForReload(); // re-bind model watches to the newly-selected list
                    // a list switch is discrete → reload now (its nodes are populated); async (NXT-21)
                    FireAndLog(ReloadCueShowSessionAsync(), "cue ShowSession reload (list switch)");
                }
            };
            SubscribeCueGraphForReload();
            FireAndLog(ReloadCueShowSessionAsync(), "cue ShowSession reload (initial)");

            // Cue output-line health comes from the session's composition throughput, so a cue-driven line's
            // health LED keeps working.
            OutputManagement.CueLineMetricsProbe = TryGetCueShowLineHealthMetrics;

            // Override the transport callbacks to drive ShowSession. The VM resolves WHICH cues fire and hands
            // them to the executors, so we fire by id (FireCueAsync) - independent of ShowSession's GO anchor.
            // Each transport op is guarded so a failure is LOGGED (not only surfaced as a UI notification).
            CuePlayer.StopPlaybackCallback = () => GuardedCueShowOp("stop", () => _cueShowSession!.StopAllAsync());
            // #26 visualizer cue: start/stop the projectM layer on a composition WITH placement (a
            // section of the frame). The layer persists across later cue fires (compositions persist)
            // until a Stop cue - or an edit reload, which re-applies the checkbox-configured baseline.
            CuePlayer.VisualizerCueExecutor = async (viz, _) =>
            {
                if (_cueShowSession is not { } session)
                    return "cue session unavailable";
                // v3: composition + geometry come from the cue's Video-tab placement (media-cue parity);
                // legacy single-rect files were migrated to one placement at load.
                var place = viz.VideoPlacements.FirstOrDefault();
                var compGuid = place?.CompositionId ?? viz.CompositionId;
                var compId = compGuid.ToString();
                if (!viz.StartVisualizer)
                {
                    await session.SetCompositionVisualizerAsync(compId, null).ConfigureAwait(false);
                    return null;
                }

                // Audio-feed routing (#26): FeedAll = every clip; else the cue's selected sources plus
                // media cues flagged "send to visualizer". The set is snapshotted HERE (UI thread) and
                // read lock-free by the session-dispatcher filter; refire the viz cue after changing it.
                Func<string, bool>? feedFilter = null;
                if (!viz.FeedAll)
                {
                    var allowed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in viz.FeedCueIds)
                        allowed.Add(id.ToString());
                    var model = CuePlayer.SelectedCueList?.ToModel();
                    if (model is not null)
                        foreach (var node in FlattenCueNodes(model.Nodes))
                            if (node is MediaCueNode { SendToVisualizer: true } m)
                                allowed.Add(m.Id.ToString());
                    feedFilter = cueId => allowed.Contains(cueId);
                }

                if (!RuntimeModules.IsProjectMAvailable)
                    return "projectM is not available on this machine";
                var comp = CuePlayer.SelectedCueList?.ToModel().Compositions
                    .FirstOrDefault(c => c.Id == compGuid);
                if (comp is null)
                    return "the visualizer cue has no composition placement - add one on its Video tab";

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
                        Shuffle = true,
                    });
                var placement = place is null
                    ? new VideoPlacementSpec(compId, int.MaxValue - 1, Placement: "stretch")
                    : new VideoPlacementSpec(
                        compId, int.MaxValue - 1,
                        Opacity: Math.Clamp(place.Opacity, 0, 1), Placement: "stretch",
                        DestX: place.DestX, DestY: place.DestY,
                        DestWidth: place.DestWidth, DestHeight: place.DestHeight,
                        RotationDegrees: place.RotationDegrees);
                if (!await session.SetCompositionVisualizerAsync(
                            compId, source, placement: placement, audioFeedFilter: feedFilter)
                        .ConfigureAwait(false))
                {
                    source.Dispose();
                    Trace.LogWarning("visualizer cue: attach REFUSED (comp={Comp} - no GL surface host?)", compId);
                    return "visualizer not attached (composition has no GL surface host)";
                }

                Trace.LogInformation(
                    "visualizer cue ATTACHED: comp={Comp} render={W}x{H}@{Fps} dest=({X:0.##},{Y:0.##},{DW:0.##},{DH:0.##}) opacity={Op:0.##} feed={Feed} presets='{Presets}'",
                    compId, renderW, renderH, fps,
                    place?.DestX ?? 0, place?.DestY ?? 0, place?.DestWidth ?? 1, place?.DestHeight ?? 1,
                    place?.Opacity ?? 1, viz.FeedAll ? "all" : "selective", viz.PresetDirectory ?? "(builtin)");
                return null;
            };
            // Pause must hit EVERY active group, not just the default one - a multi-group cue show would otherwise
            // keep the other groups running on pause (parity with StopAllAsync).
            CuePlayer.SetPlaybackPausedCallback = paused => GuardedCueShowOp("pause", () => _cueShowSession!.SetAllPausedAsync(paused));
            CuePlayer.SeekCueCallback = (_, pos) => GuardedCueShowOp("seek", () => _cueShowSession!.SeekAsync(pos));
            // Multi-cue seek goes through the group-seek barrier so every targeted group lands atomically behind one
            // shared epoch (a group runs one active clip, so a cue's seek lands on whatever is active in its group).
            CuePlayer.SeekCuesCallback = positions => GuardedCueShowOp("seek-cues", () =>
                _cueShowSession!.SeekManyAsync(
                    positions.Select(p => (_cueGroupByCueId.GetValueOrDefault(p.CueId, ShowSession.DefaultGroup), p.Position)).ToList()));
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
            CuePlayer.MediaCueGroupExecutor = async (cues, _) =>
            {
                // Flush any pending graph edits ONCE, then fire the whole group through the start barrier so a
                // simultaneous cue group opens its decoders in parallel and starts in sync (parity with the engine's
                // ExecuteGroupAsync) instead of staggered by each cue's open in turn.
                await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                var bound = cues.Where(c => _cueShowBoundCueIds.Contains(c.Id)).ToList();
                foreach (var unbound in cues.Where(c => !_cueShowBoundCueIds.Contains(c.Id)))
                    Trace.LogError("HaPlay: group cue '{Label}' ({Id}) has no current ShowSession media binding",
                        unbound.Label, unbound.Id);
                if (bound.Count == 0)
                    return null;

                IReadOnlyList<CueExecutionStatus> statuses;
                try
                {
                    statuses = await _cueShowSession!.FireCuesAsync(bound.Select(c => c.Id.ToString()).ToList())
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
                        await Dispatcher.UIThread.InvokeAsync(() => MarkCueShowCueStarted(cue.Id));
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
            CuePlayer.CueClipModelStaleCallback = () => _cueShowGraphDirty = true;
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
            _cueShowSession.ClipNaturallyEnded += _ =>
                Dispatcher.UIThread.Post(() => FireAndLog(OnCueClipNaturallyEndedAsync(), "cue auto-follow"));

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
    /// cue routing audio to the line's device (reverse-mapped via its PortAudio device id), scoring a combined
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

        // Audio: reverse-map the line to its PortAudio device and sum this line's active cue-audio pump chunks
        // (enqueued/dropped). Closes the "audio-only cue line reports Idle" gap - an audio-only line now lights up
        // once a cue routes audio to its device. Device-addressed routes only (matches the session-side tracking).
        long audioEnqueued = 0;
        long audioDropped = 0;
        var deviceId = OutputManagement.DefinitionsSnapshot
            .OfType<PortAudioOutputDefinition>()
            .FirstOrDefault(d => d.Id == outputLineId)?.EffectiveAudioBackendDeviceId;
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
        if (_subscribedCueNodes is not null)
            _subscribedCueNodes.CollectionChanged -= OnCueNodesChanged;
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
        _subscribedCueNodes = _subscribedCueGraphList?.Nodes;
        if (_subscribedCueNodes is not null)
            _subscribedCueNodes.CollectionChanged += OnCueNodesChanged;
        if (_subscribedCueGraphList is null)
            return;

        _subscribedCueGraphList.Compositions.CollectionChanged += OnCueCompositionsChanged;
        _subscribedCueGraphList.VideoOutputs.CollectionChanged += OnCueOutputBindingsChanged;
        RebindCueGraphItemSubscriptions();
    }

    private void OnCueNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleCueShowSessionReload();

    private void OnCueCompositionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindCueGraphItemSubscriptions();
        ScheduleCueShowSessionReload();
    }

    private void OnCueOutputBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindCueGraphItemSubscriptions();
        ScheduleCueShowSessionReload();
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
        ScheduleCueShowSessionReload();
    }

    private void OnCueOutputBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mapping edits are live-applied; LineRef is display-only. Device/composition reassignment needs a
        // fresh set of host leases and therefore a document reload.
        if (e.PropertyName is nameof(CueVideoOutputBindingViewModel.Mapping)
            or nameof(CueVideoOutputBindingViewModel.MappingEnabled)
            or nameof(CueVideoOutputBindingViewModel.LineRef))
            return;
        ScheduleCueShowSessionReload();
    }

    /// <summary>Debounces a graph reload (UI thread) so a burst of edits - or loading a list, which adds many
    /// nodes - collapses into a single reload instead of one per node.</summary>
    private void ScheduleCueShowSessionReload()
    {
        if (_cueShowSession is null)
            return;
        _cueShowGraphDirty = true;
        _cueReloadDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cueReloadDebounce.Tick -= OnCueReloadDebounceTick;
        _cueReloadDebounce.Tick += OnCueReloadDebounceTick;
        _cueReloadDebounce.Stop();
        _cueReloadDebounce.Start(); // restart the window on each change → fires 300ms after the last edit
    }

    private void OnCueReloadDebounceTick(object? sender, EventArgs e)
    {
        _cueReloadDebounce?.Stop();
        // Rebuilding the document swaps the graph and disposes the active compositions, which STOPS any running
        // cue. Doing that on every in-place edit ended a playing cue the moment its text/font/routes changed. While
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
    private void MarkCueShowCueStarted(Guid cueId)
    {
        var group = _cueGroupByCueId.GetValueOrDefault(cueId, ShowSession.DefaultGroup);
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

    /// <summary>Polls the per-group session snapshot to drive <see cref="CuePlayerViewModel.OnCueProgress"/> (the
    /// now-playing countdown/scrubber) while a group runs, and <see cref="CuePlayerViewModel.OnCueEnded"/> when it
    /// goes idle (natural end / Stop / Cancel). Per group, so concurrent cues on different groups track
    /// independently; a looping/freeze cue keeps its group "running" and so stays correctly active.</summary>
    private async void OnCueShowProgressPollTick(object? sender, EventArgs e)
    {
        try
        {
            if (_cueShowSession is null || _cueShowActiveByGroup.Count == 0)
            {
                _cueShowProgressPoll?.Stop();
                return;
            }
            var snapshot = await _cueShowSession.SnapshotAsync().ConfigureAwait(true);
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

    private async void OnSoundboardProgressPollTick(object? sender, EventArgs e)
    {
        try
        {
            if (_cueShowSession is null)
            {
                _soundboardProgressPoll?.Stop();
                return;
            }
            var voices = await _cueShowSession.GetVoiceProgressAsync().ConfigureAwait(true);

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
        {
            _cueShowGraphDirty = true; // the in-flight runner re-loops on this before completing
            return inFlight;
        }

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
                _cueShowGraphDirty = false; // this pass captures the current model; a mid-pass edit re-marks it
                if (!await ReloadCueShowSessionOnceAsync().ConfigureAwait(true))
                {
                    _cueShowGraphDirty = true; // failed: stay dirty (the fire-path flush surfaces it) - no spin
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
    private async Task<bool> ReloadCueShowSessionOnceAsync()
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
                    continue;
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
            await session.LoadDocumentAsync(doc).ConfigureAwait(true);
            // Visualizers are CUES now (#26): a VisualizerCueNode attaches/detaches the layer at fire
            // time (with placement + audio-feed routing). The old per-composition checkbox baseline is
            // gone; an edit reload simply clears any fired visualizer (refire to restore).
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
                "HaPlay: cue ShowSession reloaded - {Cues} cues, {Clips} clips, {Comps} compositions, {Lines} video lines",
                doc.Cues.Count, doc.Clips.Count, doc.Compositions.Count, _cueAcquiredVideoLines.Count);
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
    private async Task OnCueClipNaturallyEndedAsync()
    {
        try
        {
            await CuePlayer.OnMediaCueNaturallyEndedAsync();
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
            foreach (var held in _cueAcquiredVideoLines)
                OutputManagement.ReleaseVideoOutputForLine(held);
            _cueAcquiredVideoLines.Clear();

            var session = _cueShowSession;
            _cueShowSession = null;
            session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession shutdown cleanup");
        }
    }
}
