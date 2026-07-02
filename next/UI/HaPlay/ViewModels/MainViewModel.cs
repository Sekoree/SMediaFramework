using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;
using Microsoft.Extensions.Logging;
using S.Control;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Session;
using OSCLib;
using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.ViewModels.MainViewModel");

    private int _nextPlayerNumber = 1;
    private readonly object _midiInitSync = new();
    private readonly Playback.CuePlaybackEngine _cuePlaybackEngine;
    private readonly Playback.SoundboardEngine _soundboardEngine;
    // Phase-8 convergence: the cue transport runs on this headless session by default (2026-07-01 flip); the engine
    // above is the HAPLAY_USE_SHOWSESSION=0 fallback. See TryWireShowSessionCueTransport / ShowSessionGate.
    private ShowSession? _cueShowSession;
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
    // ShowSession graph — otherwise only a SelectedCueList *switch* reloaded, so a freshly-built/edited cue fired
    // "cue '…' is not registered". Reloads are debounced so a burst of edits (or loading a list) collapses to one.
    private INotifyCollectionChanged? _subscribedCueNodes;
    private CueListEditorViewModel? _subscribedCueGraphList;
    private readonly HashSet<CueCompositionViewModel> _subscribedCueCompositions = new();
    private readonly HashSet<CueVideoOutputBindingViewModel> _subscribedCueOutputBindings = new();
    private DispatcherTimer? _cueReloadDebounce;
    // Set as soon as the GUI cue model changes and cleared only after ReloadCueShowSession commits it. Fire paths
    // flush a dirty graph synchronously on the UI thread, so GO cannot race the 300 ms edit debounce and execute
    // an older cue definition (notably a media cue captured before its Source was assigned).
    private volatile bool _cueShowGraphDirty;
    // Immutable-by-convention snapshot replaced after each document load. Used to turn an unbound/stale media
    // cue into an explicit operator error instead of calling a no-op session action.
    private volatile IReadOnlySet<Guid> _cueShowBoundCueIds = new HashSet<Guid>();
    // Re-back cue → its transport group (from the mapped doc), and each group's currently-active cue, so the
    // snapshot poll can drive BOTH progress (OnCueProgress) and end (OnCueEnded) per group — the ShowSession
    // transport raises neither event, so without this the now-playing countdown is frozen and rows never clear.
    private Dictionary<Guid, string> _cueGroupByCueId = new();
    private readonly Dictionary<string, CueShowActiveState> _cueShowActiveByGroup = new(StringComparer.Ordinal);
    private DispatcherTimer? _cueShowProgressPoll;
    private DispatcherTimer? _soundboardProgressPoll;

    /// <summary>Per-group active-cue state for the re-back progress poll. <see cref="ObservedRunning"/> guards the
    /// warmup race — the play clock takes a moment to start, so the poll must not treat the first not-running tick
    /// as "ended" until the clip has actually been seen running (or its warmup grace elapses).</summary>
    private sealed class CueShowActiveState
    {
        public required Guid CueId;
        public bool ObservedRunning;
        public int NotRunningTicks;
    }
    // soundboard tile → its configured fade-out (ms), captured at play so FadeOutSound (tile-only) can use it.
    private readonly Dictionary<Guid, int> _soundboardFadeMs = new();
    private bool _midiInitialized;
    private CancellationTokenSource? _endpointHealthCts;
    private DispatcherTimer? _endpointHealthTimer;
    private int _endpointHealthRefreshInFlight;

    // Pre-roll refresh is suggested in bursts (standby moves, property edits). Collapse those to a
    // latest-request-wins refresh: a new request cancels the in-flight one's token, and the serial
    // gate ensures only one refresh touches the engine/player caches at a time.
    private CancellationTokenSource? _preRollRefreshCts;
    private readonly SemaphoreSlim _preRollRefreshGate = new(1, 1);

    public MainViewModel()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MainViewModel.ctor", slowWarningMs: 1000);
        OutputManagement = new OutputManagementViewModel();
        CuePlayer = new CuePlayerViewModel();
        Control = new ControlWorkspaceViewModel
        {
            EnsureProjectSavedAsync = EnsureProjectSavedForScriptsAsync,
        };
        CuePlayer.SetAvailableOutputs(OutputManagement.Outputs);
        Players = new ObservableCollection<MediaPlayerViewModel>();
        Players.CollectionChanged += (_, _) => OnPlayersChangedForPlayerTabs();
        RecentProjects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRecentProjects));
        // First player can't be removed — there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];
        _cuePlaybackEngine = new Playback.CuePlaybackEngine(OutputManagement, CuePlayer);
        OutputManagement.CueLineMetricsProbe = _cuePlaybackEngine.TryGetLineHealthMetrics;
        _cuePlaybackEngine.NaturalEnd += OnCuePlaybackEngineNaturalEndAsync;
        _cuePlaybackEngine.CueStarted += (_, id) => CuePlayer.OnCueStarted(id);
        _cuePlaybackEngine.CueEnded += (_, id) => CuePlayer.OnCueEnded(id);
        _cuePlaybackEngine.CueProgress += (_, p) => CuePlayer.OnCueProgress(p);
        _cuePlaybackEngine.PreparedCuesChanged += ids =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                CuePlayer.OnPreRollCacheChanged(ids);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CuePlayer.OnPreRollCacheChanged(ids));
        };
        _cuePlaybackEngine.PreparedCueStatesChanged += states =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                CuePlayer.OnPreparedCueStatesChanged(states);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CuePlayer.OnPreparedCueStatesChanged(states));
        };
        _cuePlaybackEngine.PreviewEnded += (_, id) => CuePlayer.OnPreviewEnded(id);
        CuePlayer.CancelCueCallback = _cuePlaybackEngine.StopCueAsync;
        CuePlayer.MediaCueExecutor = _cuePlaybackEngine.ExecuteAsync;
        CuePlayer.MediaCueGroupExecutor = _cuePlaybackEngine.ExecuteGroupAsync;
        CuePlayer.StopPlaybackCallback = _cuePlaybackEngine.StopAsync;
        CuePlayer.SetPlaybackPausedCallback = _cuePlaybackEngine.SetPausedAsync;
        CuePlayer.PreviewCueCallback = async (cue, ct) =>
        {
            _cuePlaybackEngine.PreviewAudioDeviceIndex = CuePlayer.PreviewAudioDeviceIndex;
            return await _cuePlaybackEngine.PreviewCueAsync(cue, ct);
        };
        CuePlayer.StopPreviewCallback = _cuePlaybackEngine.StopPreviewAsync;
        CuePlayer.SeekCueCallback = _cuePlaybackEngine.SeekCueAsync;
        CuePlayer.SeekCuesCallback = _cuePlaybackEngine.SeekCuesAsync;
        Soundboard = new SoundboardWorkspaceViewModel(OutputManagement);
        _soundboardEngine = new Playback.SoundboardEngine(_cuePlaybackEngine);
        Soundboard.PlaySoundCallback = _soundboardEngine.PlayAsync;
        Soundboard.FadeOutSoundCallback = _soundboardEngine.FadeOutAsync;
        Soundboard.StopSoundCallback = _soundboardEngine.StopAsync;
        Soundboard.StopAllSoundsCallback = _soundboardEngine.StopAllAsync;
        Soundboard.SetSoundVolumeCallback = _soundboardEngine.SetVolume;
        Soundboard.ProbeDurationCallback = CueMediaProbe.TryProbeDurationMsAsync;
        _soundboardEngine.SoundStarted += (_, id) => Soundboard.OnSoundStarted(id);
        _soundboardEngine.SoundProgress += (_, p) => Soundboard.OnSoundProgress(p);
        _soundboardEngine.SoundEnded += (_, id) => Soundboard.OnSoundEnded(id);
        CuePlayer.UpdateActiveCueVideoPlacementCallback = _cuePlaybackEngine.UpdateActiveCueVideoPlacementAsync;
        CuePlayer.UpdateActiveCueAudioRoutesCallback = _cuePlaybackEngine.UpdateActiveCueAudioRoutesAsync;
        CuePlayer.UpdateOutputMappingCallback = _cuePlaybackEngine.UpdateCompositionOutputMapping;
        CuePlayer.UpdateCompositionVideoFxCallback = _cuePlaybackEngine.UpdateCompositionVideoFx;
        CuePlayer.SetCompositionTestPatternCallback = (compositionId, outputLineId, mapping, show) =>
            _cuePlaybackEngine.SetCompositionTestPattern(
                show ? CuePlayer.SelectedCueList?.ToModel() : null, compositionId, outputLineId, mapping, show);
        _cuePlaybackEngine.ReleaseConflictingPlayerOutputsAsync = ReleaseMediaPlayerOutputsForCueAsync;
        CuePlayer.ActionCueExecutor = ExecuteCueActionAsync;
        CuePlayer.PreRollRefreshSuggested += (_, _) =>
            FireAndLog(RefreshCuePreRollAsync(), "RefreshCuePreRollAsync suggested");
        CuePlayer.CueStandbyInvalidated += (_, cueId) =>
        {
            _cuePlaybackEngine.MarkPreparedCueStale(cueId);
            ScheduleCueShowSessionReload();
        };
        CuePlayer.RefreshPreviewAudioDevices();
        TryWireShowSessionCueTransport(); // convergence: cue transport on ShowSession by default (HAPLAY_USE_SHOWSESSION=0 opts out)
        foreach (var player in Players)
            player.NaturalPlaybackEnded += OnPlayerNaturalPlaybackEnded;
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        ActionEndpoints.CollectionChanged += OnActionEndpointsCollectionChanged;
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        RefreshMidiDeviceCatalog();
        FireAndLog(RefreshAllEndpointHealthAsync(), "RefreshAllEndpointHealthAsync startup");
        // Keep endpoint LEDs current even when devices/network state changes after project load.
        _endpointHealthTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            FireAndLog(RefreshAllEndpointHealthAsync(), "RefreshAllEndpointHealthAsync timer");
        })
        {
            IsEnabled = true,
        };

        // Phase B (§3.6) — give the Edit dialog a way to ask "is any player playing through this line?".
        // Iterating the Players collection on each probe is fine: outputs are edited rarely, never
        // during a hot loop, and this is the single source of truth that doesn't require a new event.
        OutputManagement.PlaybackUsageProbe =
            line => Players.Any(p => p.IsActivelyPlayingThroughLine(line));
        OutputManagement.ActivePlayersProbe = () => Players;

        LoadRecentProjects();
        _appSettings = AppSettings.Load();
        _sidebarCollapsed = _appSettings.SidebarCollapsed;
        // Theme/density (§8.6) — load saved values and apply them immediately. The OnXChanged hooks
        // would re-save on first set; seed via backing fields so we don't fire that pointlessly.
        _theme = _appSettings.Theme;
        _density = _appSettings.Density;
        AppearanceController.ApplyTheme(_theme);
        AppearanceController.ApplyDensity(_density);
        if (!Playback.PlaybackVideoPipeline.CliRequestedUyvyPassthrough)
            Playback.PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = _appSettings.PreferLiveUyvyPassthrough;
        var lastWorkspaceId = WorkspaceItem.MigrateLegacyId(_appSettings.LastSelectedWorkspace);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == lastWorkspaceId)
                            ?? WorkspaceItem.Players;
        ToastCenter.Sink = OnToastPosted;

        // Remote API (per-machine setting) — seed via backing fields so the OnXChanged hooks don't
        // re-save during construction, then bring the listener up if it was left enabled.
        _restApiEnabled = _appSettings.RestApiEnabled;
        _restApiPort = _appSettings.RestApiPort is >= 1 and <= 65535 ? _appSettings.RestApiPort : 8990;
        _restApiAllowLan = _appSettings.RestApiAllowLan;
        _restApiAccessToken = EnsureRestApiAccessToken(_appSettings);
        RestartRestApi();
        timing?.SetOutcome($"players={Players.Count} outputs={OutputManagement.Outputs.Count} endpoints={ActionEndpoints.Count} restApi={RestApiEnabled}");
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

    // ----- Remote API (HTTP) ---------------------------------------------------------------------

    /// <summary>Phase-8 convergence: run the cue workspace's transport on the headless <see cref="ShowSession"/>
    /// instead of <c>CuePlaybackEngine</c>. This is the <b>default</b> as of the 2026-07-01 flip; set
    /// <c>HAPLAY_USE_SHOWSESSION=0</c> to fall back to the engine (see <see cref="ShowSessionGate"/>). Audio
    /// realizes on the default device via the session's backend; cue video realizes on the OutputManagement
    /// NDI/SDL/local lines acquired per composition→line binding in <see cref="ReloadCueShowSessionAsync"/> and fanned
    /// out through the session's composition-id video factory. Best-effort: any failure logs and leaves the engine
    /// active as a safety net. The show reloads on cue-list change.</summary>
    private void TryWireShowSessionCueTransport()
    {
        // One unambiguous startup line records which playback path this run uses, so a hardware-soak log makes the
        // active engine obvious from the top instead of having to infer it from later behaviour. ShowSession is the
        // default now (2026-07-01 flip); HAPLAY_USE_SHOWSESSION=0 forces the legacy engine as a no-rebuild fallback.
        var useShowSession = ShowSessionGate.UseShowSession;
        Trace.LogInformation(
            "HaPlay cue playback path: {Path} (HAPLAY_USE_SHOWSESSION={Flag}).",
            useShowSession ? "ShowSession (convergence default)" : "legacy CuePlaybackEngine (opt-out)",
            ShowSessionGate.DescribeOptOut());
        if (!useShowSession)
            return;

        try
        {
            var backend = MediaRuntime.Registry.AudioBackends.FirstOrDefault();
            _cueShowSession = new ShowSession(
                MediaRuntime.Registry,
                backend,
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex),
                // Borrowed lines: the cue workspace owns each output's lifetime (acquire/release via
                // _cueAcquiredVideoLines), so the leases declare DisposeOutputOnRuntimeDispose=false — the session
                // never disposes them (NXT-01).
                (compId, name, _, _) => _cueVideoOutputs.TryGetValue(compId, out var outs)
                    ? outs.Select(o => new ClipCompositionOutputLease(
                        o.LineId.ToString("N"), name, o.Output,
                        DisposeOutputOnRuntimeDispose: false,
                        Mapping: HaPlayShowMapper.ToClipOutputMapping(o.Mapping))).ToArray()
                    : Array.Empty<ClipCompositionOutputLease>(),
                CueCompositionRuntime.CreateShowSessionCompositor);

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

            // Cue output-line health now comes from the session's composition throughput (overrides the engine
            // probe wired at construction) — so a cue-driven line's health LED keeps working on the ShowSession
            // path, and deleting CuePlaybackEngine later won't leave those lines dark.
            OutputManagement.CueLineMetricsProbe = TryGetCueShowLineHealthMetrics;

            // Override the transport callbacks to drive ShowSession. The VM resolves WHICH cues fire and hands
            // them to the executors, so we fire by id (FireCueAsync) — independent of ShowSession's GO anchor.
            // Each transport op is guarded so a failure is LOGGED (not only surfaced as a UI notification).
            CuePlayer.StopPlaybackCallback = () => GuardedCueShowOp("stop", () => _cueShowSession!.StopAllAsync());
            // Pause must hit EVERY active group, not just the default one — a multi-group cue show would otherwise
            // keep the other groups running on pause (parity with StopAllAsync).
            CuePlayer.SetPlaybackPausedCallback = paused => GuardedCueShowOp("pause", () => _cueShowSession!.SetAllPausedAsync(paused));
            CuePlayer.SeekCueCallback = (_, pos) => GuardedCueShowOp("seek", () => _cueShowSession!.SeekAsync(pos));
            // Multi-cue seek goes through the group-seek barrier so every targeted group lands atomically behind one
            // shared epoch (a group runs one active clip, so a cue's seek lands on whatever is active in its group).
            CuePlayer.SeekCuesCallback = positions => GuardedCueShowOp("seek-cues", () =>
                _cueShowSession!.SeekManyAsync(
                    positions.Select(p => (_cueGroupByCueId.GetValueOrDefault(p.CueId, ShowSession.DefaultGroup), p.Position)).ToList()));
            CuePlayer.CancelCueCallback = id => GuardedCueShowOp("cancel", () => _cueShowSession!.StopCueAsync(id.ToString()));
            // Cue preview (audition on the preview/headphone device) goes through ShowSession too — otherwise it was
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
            // PreparedCuesChanged wiring) — otherwise a warmed cue never showed Ready under the gate.
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
                        Trace.LogError("HaPlay: cue '{Label}' ({Id}) cannot fire — {Detail}", cue.Label, cue.Id, detail);
                        return detail;
                    }
                    var status = await _cueShowSession!.FireCueAsync(cue.Id.ToString()).ConfigureAwait(false);
                    if (status != CueExecutionStatus.Fired)
                    {
                        var detail = await DescribeCueShowFailureAsync(cue.Id, status).ConfigureAwait(false);
                        Trace.LogWarning(
                            "HaPlay: cue '{Label}' ({Id}) did not fire — status {Status}: {Detail}",
                            cue.Label, cue.Id, status, detail);
                        return detail;
                    }
                    // Engine-callback parity: report the cue active so the UI shows it Triggered/now-playing —
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
                            "HaPlay: group cue '{Label}' ({Id}) did not fire — status {Status}: {Detail}",
                            cue.Label, cue.Id, statuses[i],
                            await DescribeCueShowFailureAsync(cue.Id, statuses[i]).ConfigureAwait(false));
                }
                return null;
            };

            // The editor callbacks are independent of transport. Leaving these on CuePlaybackEngine made every
            // placement/output-layout edit target an engine with no active ShowSession composition, so the model
            // persisted but the running image never changed.
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
                var frame = TextFrameRenderer.Render(textItem, new Rational(30, 1));
                if (frame is null)
                {
                    Trace.LogWarning("HaPlay: live text update cue {Cue} — render returned null", cueId);
                    return;
                }
                var w = frame.Format.Width;
                var h = frame.Format.Height;
                var applied = await _cueShowSession!.UpdateActiveClipFrameAsync(cueId.ToString(), frame).ConfigureAwait(false);
                Trace.LogInformation("HaPlay: live text update cue {Cue} — applied={Applied} frame={W}x{H}", cueId, applied, w, h);
            };
            // An idle cue's placement edit only lives in the model; the show document is rebuilt at (re)load,
            // not on placement edits. Mark it stale (flag only — no debounced auto-reload that would interrupt a
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
            // mapping — the host owns the masking) and hold it in a top-most composition layer via ShowSession.
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
                    Soundboard.OnSoundStarted(req.TileId);
                    StartSoundboardProgressPoll(); // drive per-tile countdown from the session's voice playheads
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            };
            Soundboard.StopSoundCallback = id => _cueShowSession!.StopVoiceAsync(id.ToString());
            Soundboard.StopAllSoundsCallback = () => _cueShowSession!.StopAllVoicesAsync();
            Soundboard.SetSoundVolumeCallback = (id, vol) => _ = _cueShowSession!.SetVoiceVolumeAsync(id.ToString(), (float)vol);
            Soundboard.FadeOutSoundCallback = id =>
                _cueShowSession!.FadeVoiceAsync(id.ToString(), TimeSpan.FromMilliseconds(_soundboardFadeMs.GetValueOrDefault(id)));
            _cueShowSession.VoiceEnded += id =>
            {
                if (Guid.TryParse(id, out var tileId))
                    Dispatcher.UIThread.Post(() => Soundboard.OnSoundEnded(tileId));
            };

            Trace.LogInformation("HaPlay: cue transport + soundboard running on ShowSession (convergence default).");
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession cue re-back failed; staying on the engine.");
            _cueShowSession = null;
        }
    }

    /// <summary>ShowSession replacement for <c>CuePlaybackEngine.TryGetLineHealthMetrics</c>: sums the video
    /// throughput of the composition(s) feeding <paramref name="outputLineId"/> AND the audio-pump chunks of any
    /// cue routing audio to the line's device (reverse-mapped via its PortAudio device id), scoring a combined
    /// health state that mirrors the engine. Both sides read the session's lock-free snapshots, so a video, audio,
    /// or audio-only cue line all light up. Null only when the line is genuinely undriven (the outputs panel then
    /// falls back to the media-player probe / Idle). Runs off the UI poll thread with no dispatcher marshaling.</summary>
    private OutputLineHealthEvaluator.LineHealthMetrics? TryGetCueShowLineHealthMetrics(Guid outputLineId)
    {
        if (_cueShowSession is not { } session)
            return null;

        long videoSubmitted = 0;
        long videoDropped = 0;
        var driven = false;
        foreach (var (compId, outs) in _cueVideoOutputs)
        {
            if (!outs.Any(o => o.LineId == outputLineId))
                continue;
            if (session.GetCompositionStats(compId) is not { } stats)
                continue;
            driven = true;
            videoSubmitted += stats.FramesSubmitted;
            videoDropped += stats.PumpOverruns + stats.SlotOverflowFrames;
        }

        // Audio: reverse-map the line to its PortAudio device and sum this line's active cue-audio pump chunks
        // (enqueued/dropped). Closes the "audio-only cue line reports Idle" gap — an audio-only line now lights up
        // once a cue routes audio to its device. Device-addressed routes only (matches the session-side tracking).
        long audioEnqueued = 0;
        long audioDropped = 0;
        var deviceId = OutputManagement.DefinitionsSnapshot
            .OfType<PortAudioOutputDefinition>()
            .FirstOrDefault(d => d.Id == outputLineId)?.EffectiveAudioBackendDeviceId;
        if (deviceId is not null
            && session.GetActiveAudioPumpStatsByDevice().TryGetValue(deviceId, out var audio))
        {
            driven = true;
            audioEnqueued = audio.Enqueued;
            audioDropped = audio.Dropped;
        }

        var totalSubmitted = videoSubmitted + audioEnqueued;
        if (!driven || totalSubmitted == 0)
            return null;

        var totalDropped = videoDropped + audioDropped;
        var state = totalDropped == 0
            ? OutputLineHealthState.Healthy
            : totalDropped > 120 || (double)totalDropped / totalSubmitted > 0.05
                ? OutputLineHealthState.Error
                : OutputLineHealthState.Warning;
        return new OutputLineHealthEvaluator.LineHealthMetrics(
            state, videoSubmitted, videoDropped, 0, 0, audioEnqueued, audioDropped);
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

    /// <summary>Debounces a graph reload (UI thread) so a burst of edits — or loading a list, which adds many
    /// nodes — collapses into a single reload instead of one per node.</summary>
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
    /// thread, while output acquisition and document mapping must run on Avalonia's UI thread — the load
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
            "HaPlay: output mapping applied — composition={Composition} line={Line} mapping={Mapping}",
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

    /// <summary>(UI thread) Records a re-back cue as started — drives the GUI's Triggered/now-playing state via
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
                // committed within the warmup grace (~3s at 200ms) — the latter is logged as it indicates a clip
                // that never became active (e.g. a failed open).
                if (st.ObservedRunning || ++st.NotRunningTicks > 15)
                {
                    if (!st.ObservedRunning)
                        Trace.LogWarning(
                            "HaPlay: cue {Cue} never started running within grace — group '{Group}' present={Present} (snapshot: [{Snap}])",
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
    /// playheads — the engine had a <c>SoundProgress</c> event; the re-back has none, so we poll instead.</summary>
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
    // second trigger can arrive mid-reload — it marks the graph dirty and shares this task; the runner loops
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
                    _cueShowGraphDirty = true; // failed: stay dirty (the fire-path flush surfaces it) — no spin
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
            return true; // nothing to load — not a failure
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
            // hold and output — no release→re-acquire churn, so the idle slate never touches a line that stays
            // in use. Lines the new model no longer binds are DETACHED from the live compositions FIRST, then
            // released: the old composition pump outlives its clip and keeps submitting, and releasing first
            // reconfigures the sink (idle slate) while frames still flow into it — the format-mismatch flood
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
                        // Stale hold with no tracked output — resync by releasing and re-acquiring.
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
            // NXT-21: await the load — blocking the UI thread on the session dispatcher would turn any
            // dispatcher stall into a whole-app freeze.
            await session.LoadDocumentAsync(doc).ConfigureAwait(true);
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
                "HaPlay: cue ShowSession reloaded — {Cues} cues, {Clips} clips, {Comps} compositions, {Lines} video lines",
                doc.Cues.Count, doc.Clips.Count, doc.Compositions.Count, _cueAcquiredVideoLines.Count);
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession LoadDocument from cue list failed");
            return false;
        }
    }


    /// <summary>Best-effort shutdown teardown of the gated cue re-back (no-op when disabled): release its video
    /// leases (UI thread) and dispose the headless <see cref="ShowSession"/>. The session disposes on its own
    /// dispatcher (no Avalonia marshalling), so the bounded block here can't deadlock the UI thread. Players and
    /// the engine path are reclaimed by process-exit, as before.</summary>
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

    private readonly Remote.RestApiServer _restApiServer = new();
    private Remote.RemoteApiDispatcher? _restApiDispatcher;

    /// <summary>Per-machine: serve the HTTP remote API. Off by default; token-protected when enabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestApiBaseUrlDisplay))]
    private bool _restApiEnabled;

    [ObservableProperty]
    private int _restApiPort = 8990;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestApiBaseUrlDisplay))]
    [NotifyPropertyChangedFor(nameof(RestApiSecurityStatus))]
    private bool _restApiAllowLan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestApiSecurityStatus))]
    private string _restApiAccessToken = string.Empty;

    /// <summary>Shown in the Project workspace card (and the base for Copy-API-URL menus).</summary>
    public string RestApiBaseUrlDisplay =>
        _restApiServer.IsRunning && _restApiServer.BaseUrl is { } url
            ? url
            : Strings.RemoteApiDisabledStatus;

    /// <summary>Degradation note (e.g. Windows loopback fallback) or bind error; null when clean.</summary>
    public string? RestApiStatusNote => _restApiServer.StatusNote;

    public string RestApiSecurityStatus =>
        RestApiAllowLan
            ? Strings.RemoteApiSecurityLan
            : Strings.RemoteApiSecurityLoopback;

    /// <summary>Endpoint cheat-sheet rendered as a list by the Project workspace card. Paths are
    /// protocol, not prose — only the descriptions localize.</summary>
    public IReadOnlyList<Remote.RemoteApiEndpointDoc> RestApiEndpointDocs { get; } =
    [
        new("/api/v1/cues/go · pause · resume · stop · panic", Strings.RemoteApiDocCues),
        new("/api/v1/players/{player}/play · pause · toggle · stop · next · prev", Strings.RemoteApiDocPlayerTransport),
        new("/api/v1/players/{player}/volume?db=-10", Strings.RemoteApiDocPlayerVolume),
        new("/api/v1/players/{player}/hold?on=true", Strings.RemoteApiDocPlayerHold),
        new("/api/v1/players/{player}/{playlist}/{item}", Strings.RemoteApiDocPlaylistItem),
        new("/api/v1/soundboards/{board}/{tile}/tap · play · stop · fade", Strings.RemoteApiDocTile),
        new("/api/v1/soundboards/stop", Strings.RemoteApiDocSoundboardStop),
        new("/api/v1/control/arm · disarm", Strings.RemoteApiDocControl),
        new("/api/v1/status", Strings.RemoteApiDocStatus),
    ];

    partial void OnRestApiEnabledChanged(bool value)
    {
        _appSettings.RestApiEnabled = value;
        _appSettings.Save();
        RestartRestApi();
    }

    partial void OnRestApiPortChanged(int value)
    {
        _appSettings.RestApiPort = value;
        _appSettings.Save();
        RestartRestApi();
    }

    partial void OnRestApiAllowLanChanged(bool value)
    {
        _appSettings.RestApiAllowLan = value;
        _appSettings.Save();
        RestartRestApi();
    }

    private void RestartRestApi()
    {
        _restApiDispatcher ??= new Remote.RemoteApiDispatcher(CuePlayer, () => Players, Soundboard, Control);
        _restApiServer.Stop();
        if (RestApiEnabled && RestApiPort is >= 1 and <= 65535)
            _restApiServer.Start(RestApiPort, _restApiDispatcher, RestApiAccessToken, RestApiAllowLan);

        // Copy-API-URL menus keep working while the listener is off — the copied URL targets the
        // configured port and becomes live the moment the API is enabled.
        Remote.RemoteApi.AccessToken = RestApiAccessToken;
        Remote.RemoteApi.BaseUrl = _restApiServer.BaseUrl
                                   ?? $"http://{Remote.RestApiServer.ResolveAdvertisedHost(RestApiAllowLan)}:{RestApiPort}";
        OnPropertyChanged(nameof(RestApiBaseUrlDisplay));
        OnPropertyChanged(nameof(RestApiStatusNote));
        OnPropertyChanged(nameof(RestApiSecurityStatus));
    }

    private static string EnsureRestApiAccessToken(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RestApiAccessToken))
            return settings.RestApiAccessToken;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        settings.RestApiAccessToken = token;
        settings.Save();
        return token;
    }

    // ----- UI rewrite P1 (plan §1): toast overlay queue -----------------------------------------

    private const int MaxVisibleToasts = 3;
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(6);
    private DispatcherTimer? _toastSweepTimer;

    /// <summary>Visible toasts, newest last. Rendered by the MainView overlay — never part of layout.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    private void OnToastPosted(ToastSeverity severity, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnToastPosted(severity, message));
            return;
        }

        // Repeats (per-second drift/pressure updates) refresh the existing toast's clock instead of
        // stacking duplicates and churning the overlay.
        if (Toasts.Count > 0 && Toasts[^1].Message == message && Toasts[^1].Severity == severity)
        {
            Toasts[^1].DeadlineTicks = Environment.TickCount64 + (long)ToastLifetime.TotalMilliseconds;
            return;
        }

        while (Toasts.Count >= MaxVisibleToasts)
            Toasts.RemoveAt(0);
        Toasts.Add(new ToastViewModel(severity, message, t => Toasts.Remove(t))
        {
            DeadlineTicks = Environment.TickCount64 + (long)ToastLifetime.TotalMilliseconds,
        });

        _toastSweepTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, SweepExpiredToasts);
        _toastSweepTimer.Start();
    }

    private void SweepExpiredToasts(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        for (var i = Toasts.Count - 1; i >= 0; i--)
        {
            if (!Toasts[i].IsPinned && Toasts[i].DeadlineTicks <= now)
                Toasts.RemoveAt(i);
        }

        if (Toasts.Count == 0)
            _toastSweepTimer?.Stop();
    }

    // ----- Phase B (§12.1): App-shell sidebar -------------------------------------------------

    private readonly AppSettings _appSettings;

    public IReadOnlyList<WorkspaceItem> Workspaces { get; } =
    [
        WorkspaceItem.Players,
        WorkspaceItem.Cues,
        WorkspaceItem.Soundboard,
        WorkspaceItem.Control,
        WorkspaceItem.Io,
        WorkspaceItem.Project,
    ];

    /// <summary>True when the sidebar is in icon-only mode (~48 px). Toggled by the hamburger or Ctrl+B.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _sidebarCollapsed;

    /// <summary>Width binding for the sidebar column. 48 px collapsed, 180 px expanded (§12.1).</summary>
    public double SidebarWidth => SidebarCollapsed ? 48 : 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayersWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsCuesWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsSoundboardWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsIoWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsControlWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsProjectWorkspaceSelected))]
    private WorkspaceItem _selectedWorkspace = WorkspaceItem.Players;

    public bool IsPlayersWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Players;
    public bool IsCuesWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Cues;
    public bool IsSoundboardWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Soundboard;
    public bool IsIoWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Io;
    public bool IsControlWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Control;
    public bool IsProjectWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Project;

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _appSettings.SidebarCollapsed = value;
        _appSettings.Save();
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItem value)
    {
        _appSettings.LastSelectedWorkspace = value.Id;
        _appSettings.Save();
    }

    // ----- Phase E (§8.7): Main window state persistence -----------------------------------------

    /// <summary>Phase E (§8.7) — last saved main-window placement, or <see langword="null"/> on first
    /// launch. The window code-behind calls this on Opened to restore size/position; values are
    /// validated against the current screen layout before being applied.</summary>
    public WindowStateSnapshot? GetSavedWindowState() => _appSettings.MainWindow;

    /// <summary>Phase E (§8.7) — persist the current main-window placement. Called from the window
    /// code-behind on Closing (or on debounced size/move/state changes). Writes through to
    /// <c>app-settings.json</c> immediately so a hard kill still preserves the last-known good state.</summary>
    public void SaveWindowState(WindowStateSnapshot snapshot)
    {
        _appSettings.MainWindow = snapshot;
        _appSettings.Save();
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void SelectWorkspace(WorkspaceItem workspace) => SelectedWorkspace = workspace;

    [RelayCommand]
    private async Task OpenTargetConfigurationAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;
        var dialog = new Views.Dialogs.TargetConfigurationDialog { DataContext = this };
        await dialog.ShowDialog(owner);
    }

    /// <summary>Ctrl+1..N keyboard handler. Index is 1-based to match the modifier key. (§12.5)</summary>
    public void SelectWorkspaceByIndex(int oneBasedIndex)
    {
        var idx = oneBasedIndex - 1;
        if (idx >= 0 && idx < Workspaces.Count)
            SelectedWorkspace = Workspaces[idx];
    }

    private async Task ReleaseMediaPlayerOutputsForCueAsync(IReadOnlyCollection<Guid> outputLineIds)
    {
        if (outputLineIds.Count == 0)
            return;

        var wanted = outputLineIds.ToHashSet();
        var targets = await Dispatcher.UIThread.InvokeAsync(() =>
            Players.Where(p => p.IsHoldingAnyOutputLine(wanted)).ToList());

        foreach (var player in targets)
            await player.ReleaseSessionForExternalPlaybackAsync().ConfigureAwait(false);
    }

    public OutputManagementViewModel OutputManagement { get; }
    public CuePlayerViewModel CuePlayer { get; }
    public SoundboardWorkspaceViewModel Soundboard { get; }
    public ControlWorkspaceViewModel Control { get; }
    public ObservableCollection<MediaPlayerViewModel> Players { get; }
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    // ----- Phase E (§8.6): Theme & Density -------------------------------------------------------

    public IReadOnlyList<AppThemeMode> ThemeChoices { get; } = Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<AppDensityMode> DensityChoices { get; } = Enum.GetValues<AppDensityMode>();

    /// <summary>Phase E (§8.6) — chrome theme. Setting this both persists the choice and applies it
    /// live to <see cref="Application.RequestedThemeVariant"/> so the UI repaints without restart.</summary>
    [ObservableProperty]
    private AppThemeMode _theme;

    /// <summary>Phase E (§8.6) — Fluent density. Setting this both persists and applies the change to
    /// the live <see cref="Avalonia.Themes.Fluent.FluentTheme.DensityStyle"/>.</summary>
    [ObservableProperty]
    private AppDensityMode _density;

    partial void OnThemeChanged(AppThemeMode value)
    {
        _appSettings.Theme = value;
        _appSettings.Save();
        AppearanceController.ApplyTheme(value);
    }

    partial void OnDensityChanged(AppDensityMode value)
    {
        _appSettings.Density = value;
        _appSettings.Save();
        AppearanceController.ApplyDensity(value);
    }

    // ----- Player tabs --------------------------------------------------------------------------
    // The full player list remains the project/remote/source of truth. The tab collection excludes
    // detached players so a player VM has exactly one live view: either a tab or its floating window.

    private readonly ObservableCollection<MediaPlayerViewModel> _playerTabs = new();

    public ObservableCollection<MediaPlayerViewModel> PlayerTabs => _playerTabs;

    private void RebuildPlayerTabs()
    {
        _playerTabs.Clear();
        foreach (var player in Players)
        {
            if (!_detachedPlayerWindows.ContainsKey(player))
                _playerTabs.Add(player);
        }
    }

    private void EnsureSelectedPlayerVisible()
    {
        if (SelectedPlayer is null || !Players.Contains(SelectedPlayer))
        {
            SelectedPlayer = _playerTabs.FirstOrDefault() ?? Players.FirstOrDefault();
            return;
        }

        if (_playerTabs.Count > 0 && !_playerTabs.Contains(SelectedPlayer))
            SelectedPlayer = _playerTabs[0];
    }

    /// <summary>Called from the Players.CollectionChanged hook: keeps the tab collection and selection valid.</summary>
    private void OnPlayersChangedForPlayerTabs()
    {
        RebuildPlayerTabs();
        EnsureSelectedPlayerVisible();
    }

    /// <summary>OSC endpoints with persistent health LEDs for the OSC sidebar workspace.</summary>
    public ObservableCollection<ActionEndpointRowViewModel> OscEndpointRows { get; } = new();

    /// <summary>MIDI endpoints with persistent health LEDs for the MIDI sidebar workspace.</summary>
    public ObservableCollection<ActionEndpointRowViewModel> MidiEndpointRows { get; } = new();

    [ObservableProperty]
    private ActionEndpointRowViewModel? _selectedOscEndpointRow;

    [ObservableProperty]
    private ActionEndpointRowViewModel? _selectedMidiEndpointRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedActionEndpoint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedOscEndpoint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMidiEndpoint))]
    private ActionEndpoint? _selectedActionEndpoint;

    public bool HasSelectedActionEndpoint => SelectedActionEndpoint is not null;
    public bool IsSelectedOscEndpoint => SelectedActionEndpoint is OscActionEndpoint;
    public bool IsSelectedMidiEndpoint => SelectedActionEndpoint is MidiActionEndpoint;

    [ObservableProperty]
    private string _endpointEditName = string.Empty;

    [ObservableProperty]
    private string _oscEditHost = "127.0.0.1";

    [ObservableProperty]
    private int _oscEditPort = 9000;

    [ObservableProperty]
    private string _midiEditDeviceName = string.Empty;

    [ObservableProperty]
    private int? _midiEditDeviceId;

    [ObservableProperty]
    private int _midiEditChannel;

    public ObservableCollection<MidiOutputOption> MidiOutputOptions { get; } = new();
    public ObservableCollection<MidiInputOption> MidiInputOptions { get; } = new();
    public ObservableCollection<ProjectMidiInputRowViewModel> ProjectMidiInputRows { get; } = new();
    public ObservableCollection<ProjectMidiOutputRowViewModel> ProjectMidiOutputRows { get; } = new();
    public bool HasNoProjectMidiInputs => ProjectMidiInputRows.Count == 0;
    public bool HasNoProjectMidiOutputs => ProjectMidiOutputRows.Count == 0;
    public bool IsMidiAvailable => RuntimeModules.IsMidiAvailable;
    public string MidiUnavailableStatus => RuntimeModules.MidiUnavailableReason ?? "MIDI runtime unavailable.";

    [ObservableProperty]
    private string? _midiDeviceStatus;

    [ObservableProperty]
    private MidiOutputOption? _selectedMidiOutputOption;

    [ObservableProperty]
    private MidiInputOption? _selectedMidiInputOption;

    [ObservableProperty]
    private ProjectMidiInputRowViewModel? _selectedProjectMidiInputRow;

    [ObservableProperty]
    private ProjectMidiOutputRowViewModel? _selectedProjectMidiOutputRow;

    [ObservableProperty]
    private string? _endpointTestStatus;

    partial void OnSelectedMidiInputOptionChanged(MidiInputOption? value)
    {
        _ = value;
        AddSelectedMidiInputToControlCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMidiOutputOptionChanged(MidiOutputOption? value)
    {
        _ = value;
        UseSelectedMidiOutputCommand.NotifyCanExecuteChanged();
        AddSelectedMidiOutputToControlCommand.NotifyCanExecuteChanged();
        AddSelectedMidiOutputEndpointCommand.NotifyCanExecuteChanged();
        AddSelectedMidiOutputToProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProjectMidiInputRowChanged(ProjectMidiInputRowViewModel? value)
    {
        _ = value;
        RemoveSelectedProjectMidiInputCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProjectMidiOutputRowChanged(ProjectMidiOutputRowViewModel? value)
    {
        _ = value;
        RemoveSelectedProjectMidiOutputCommand.NotifyCanExecuteChanged();
        TestSelectedProjectMidiOutputCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        if (value is null)
        {
            EndpointEditName = string.Empty;
            OscEditHost = "127.0.0.1";
            OscEditPort = 9000;
            MidiEditDeviceName = string.Empty;
            MidiEditDeviceId = null;
            MidiEditChannel = 0;
        }
        else
        {
            EndpointEditName = value.Name;
            switch (value)
            {
                case OscActionEndpoint osc:
                    OscEditHost = osc.Host;
                    OscEditPort = osc.Port;
                    break;
                case MidiActionEndpoint midi:
                    MidiEditDeviceName = midi.DeviceName ?? string.Empty;
                    MidiEditDeviceId = midi.DeviceId;
                    MidiEditChannel = midi.Channel;
                    SelectedMidiOutputOption = MidiOutputOptions.FirstOrDefault(o => o.Id == midi.DeviceId);
                    break;
            }
        }
        RemoveActionEndpointCommand.NotifyCanExecuteChanged();
        SaveActionEndpointEditsCommand.NotifyCanExecuteChanged();
        RefreshMidiOutputsCommand.NotifyCanExecuteChanged();
        TestSelectedOscEndpointCommand.NotifyCanExecuteChanged();
        TestSelectedMidiEndpointCommand.NotifyCanExecuteChanged();
        EndpointTestStatus = null;
        SyncEndpointRowSelectionFromEndpoint();
    }

    partial void OnSelectedOscEndpointRowChanged(ActionEndpointRowViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedActionEndpoint, value.Endpoint))
            SelectedActionEndpoint = value.Endpoint;
    }

    partial void OnSelectedMidiEndpointRowChanged(ActionEndpointRowViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedActionEndpoint, value.Endpoint))
            SelectedActionEndpoint = value.Endpoint;
    }

    private void SyncEndpointRowSelectionFromEndpoint()
    {
        var id = SelectedActionEndpoint?.Id;
        SelectedOscEndpointRow = id is null
            ? null
            : OscEndpointRows.FirstOrDefault(r => r.Endpoint.Id == id);
        SelectedMidiEndpointRow = id is null
            ? null
            : MidiEndpointRows.FirstOrDefault(r => r.Endpoint.Id == id);
    }

    private void OnActionEndpointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        RemoveActionEndpointCommand.NotifyCanExecuteChanged();
        FireAndLog(RefreshAllEndpointHealthAsync(), "RefreshAllEndpointHealthAsync endpoints-changed");
    }

    private void RebuildEndpointWorkspaceLists()
    {
        var oscHealth = OscEndpointRows.ToDictionary(r => r.Endpoint.Id, r => (r.Health, r.HealthDetail));
        OscEndpointRows.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<OscActionEndpoint>())
        {
            var row = new ActionEndpointRowViewModel(endpoint);
            if (oscHealth.TryGetValue(endpoint.Id, out var h))
            {
                row.Health = h.Health;
                row.HealthDetail = h.HealthDetail;
            }

            OscEndpointRows.Add(row);
        }

        var midiHealth = MidiEndpointRows.ToDictionary(r => r.Endpoint.Id, r => (r.Health, r.HealthDetail));
        MidiEndpointRows.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<MidiActionEndpoint>())
        {
            var row = new ActionEndpointRowViewModel(endpoint);
            if (midiHealth.TryGetValue(endpoint.Id, out var h))
            {
                row.Health = h.Health;
                row.HealthDetail = h.HealthDetail;
            }

            MidiEndpointRows.Add(row);
        }

        SyncEndpointRowSelectionFromEndpoint();
        RebuildProjectMidiDeviceRows();
    }

    private ActionEndpointRowViewModel? FindEndpointRow(Guid endpointId) =>
        OscEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId)
        ?? MidiEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId);

    private void RebuildProjectMidiDeviceRows()
    {
        var selectedInputKey = SelectedProjectMidiInputRow?.MatchKey;
        var selectedOutputKey = SelectedProjectMidiOutputRow?.MatchKey;

        ProjectMidiInputRows.Clear();
        foreach (var row in BuildProjectMidiInputRows(Control.BuildSnapshot()))
            ProjectMidiInputRows.Add(row);

        ProjectMidiOutputRows.Clear();
        foreach (var row in BuildProjectMidiOutputRows(Control.BuildSnapshot(), ActionEndpoints))
            ProjectMidiOutputRows.Add(row);
        OnPropertyChanged(nameof(HasNoProjectMidiInputs));
        OnPropertyChanged(nameof(HasNoProjectMidiOutputs));

        SelectedProjectMidiInputRow = selectedInputKey is null
            ? ProjectMidiInputRows.FirstOrDefault()
            : ProjectMidiInputRows.FirstOrDefault(r => r.MatchKey == selectedInputKey) ?? ProjectMidiInputRows.FirstOrDefault();
        SelectedProjectMidiOutputRow = selectedOutputKey is null
            ? ProjectMidiOutputRows.FirstOrDefault()
            : ProjectMidiOutputRows.FirstOrDefault(r => r.MatchKey == selectedOutputKey) ?? ProjectMidiOutputRows.FirstOrDefault();

        RemoveSelectedProjectMidiInputCommand.NotifyCanExecuteChanged();
        RemoveSelectedProjectMidiOutputCommand.NotifyCanExecuteChanged();
        TestSelectedProjectMidiOutputCommand.NotifyCanExecuteChanged();
    }

    internal static IReadOnlyList<ProjectMidiInputRowViewModel> BuildProjectMidiInputRows(ControlSystemConfig config) =>
        config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi)
            .Where(d => d.Binding.MidiInputDeviceId is not null || !string.IsNullOrWhiteSpace(d.Binding.MidiInputDeviceName))
            .Select(d => new ProjectMidiInputRowViewModel(
                d.Id,
                d.Binding.MidiInputDeviceId,
                d.Binding.MidiInputDeviceName ?? d.Name))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<ProjectMidiOutputRowViewModel> BuildProjectMidiOutputRows(
        ControlSystemConfig config,
        IEnumerable<ActionEndpoint> endpoints)
    {
        var rows = new List<ProjectMidiOutputRowBuilder>();

        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Midi))
        {
            var deviceId = device.Binding.MidiOutputDeviceId;
            var deviceName = device.Binding.MidiOutputDeviceName;
            if (deviceId is null && string.IsNullOrWhiteSpace(deviceName))
                continue;
            deviceName ??= device.Name;

            var row = FindOrAddProjectMidiOutputRow(rows, deviceId, deviceName);
            row.ControlDeviceId = device.Id;
            row.DeviceId ??= deviceId;
            row.DeviceName = PreferMidiDeviceName(row.DeviceName, deviceName);
        }

        foreach (var endpoint in endpoints.OfType<MidiActionEndpoint>())
        {
            var deviceName = endpoint.DeviceName ?? endpoint.Name;
            if (endpoint.DeviceId is null && string.IsNullOrWhiteSpace(deviceName))
                continue;

            var row = FindOrAddProjectMidiOutputRow(rows, endpoint.DeviceId, deviceName);
            row.CueEndpointId = endpoint.Id;
            row.DeviceId ??= endpoint.DeviceId;
            row.DeviceName = PreferMidiDeviceName(row.DeviceName, deviceName);
        }

        return rows
            .Select(r => r.ToRow())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProjectMidiOutputRowBuilder FindOrAddProjectMidiOutputRow(
        List<ProjectMidiOutputRowBuilder> rows,
        int? deviceId,
        string? deviceName)
    {
        var existing = rows.FirstOrDefault(r => MatchesMidiBinding(r.DeviceId, r.DeviceName, deviceId, deviceName));
        if (existing is not null)
            return existing;

        var row = new ProjectMidiOutputRowBuilder
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
        };
        rows.Add(row);
        return row;
    }

    private static bool MatchesMidiBinding(int? firstId, string? firstName, int? secondId, string? secondName)
    {
        if (firstId is not null && secondId is not null && firstId == secondId)
            return true;

        return !string.IsNullOrWhiteSpace(firstName)
               && !string.IsNullOrWhiteSpace(secondName)
               && string.Equals(firstName.Trim(), secondName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? PreferMidiDeviceName(string? existing, string? candidate) =>
        string.IsNullOrWhiteSpace(existing)
            ? candidate
            : existing;

    public async Task RefreshAllEndpointHealthAsync()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MainViewModel.RefreshAllEndpointHealthAsync", slowWarningMs: 1500);
        if (Interlocked.CompareExchange(ref _endpointHealthRefreshInFlight, 1, 0) != 0)
        {
            timing?.SetOutcome("already-in-flight");
            return;
        }

        CancellationTokenSource? newCts = null;
        _endpointHealthCts?.Cancel();
        _endpointHealthCts?.Dispose();
        _endpointHealthCts = newCts = new CancellationTokenSource();
        var ct = newCts.Token;

        try
        {
            var probed = 0;
            foreach (var row in OscEndpointRows.Concat(MidiEndpointRows))
            {
                if (ct.IsCancellationRequested)
                {
                    timing?.SetOutcome($"cancelled probed={probed}");
                    return;
                }
                await ProbeEndpointRowAsync(row, ct).ConfigureAwait(false);
                probed++;
            }
            timing?.SetOutcome($"probed={probed}");
        }
        finally
        {
            // Keep the latest CTS alive for targeted manual probes; only dispose if this run's CTS was replaced.
            if (!ReferenceEquals(_endpointHealthCts, newCts))
            {
                try { newCts.Dispose(); } catch { /* best effort */ }
            }

            Interlocked.Exchange(ref _endpointHealthRefreshInFlight, 0);
        }
    }

    private async Task ProbeEndpointRowAsync(ActionEndpointRowViewModel row, CancellationToken ct)
    {
        if (Trace.IsEnabled(LogLevel.Trace))
            Trace.LogTrace("ProbeEndpointRowAsync: endpoint={EndpointId} kind={Kind} name={Name}", row.Endpoint.Id, row.Endpoint.KindLabel, row.Endpoint.Name);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            row.Health = ActionEndpointHealthState.Checking;
            row.HealthDetail = null;
        });

        var (ok, detail) = row.Endpoint switch
        {
            OscActionEndpoint osc => await ActionEndpointProbe.TryProbeOscAsync(osc, ct).ConfigureAwait(false),
            MidiActionEndpoint midi => ActionEndpointProbe.TryProbeMidi(midi, EnsureMidiInitialized),
            _ => (false, Strings.UnknownEndpointKind),
        };

        if (ct.IsCancellationRequested)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            row.Health = ok ? ActionEndpointHealthState.Ok : ActionEndpointHealthState.Failed;
            row.HealthDetail = detail;
        });
        Trace.LogDebug("ProbeEndpointRowAsync: endpoint={EndpointId} kind={Kind} ok={Ok} detail={Detail}", row.Endpoint.Id, row.Endpoint.KindLabel, ok, detail);
    }

    [ObservableProperty]
    private MediaPlayerViewModel? _selectedPlayer;

    [RelayCommand]
    private void AddPlayer()
    {
        var p = CreatePlayer(removable: true);
        Players.Add(p);
        SelectedPlayer = p;
    }

    private MediaPlayerViewModel CreatePlayer(bool removable)
    {
        var name = Strings.Format(nameof(Strings.PlayerNameFormat), _nextPlayerNumber++);
        var player = new MediaPlayerViewModel(OutputManagement, name, removable ? RemovePlayer : null);
        player.NaturalPlaybackEnded += OnPlayerNaturalPlaybackEnded;
        player.DetachRequested += OnPlayerDetachRequested;
        player.CuePreRollChanged += ids =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                CuePlayer.OnPreRollCacheChanged(ids);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CuePlayer.OnPreRollCacheChanged(ids));
        };
        return player;
    }

    private async void OnPlayerNaturalPlaybackEnded(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await CuePlayer.OnMediaCueNaturallyEndedAsync();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "OnPlayerNaturalPlaybackEnded: cue auto-follow failed");
            CuePlayer.StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowFailedFormat), ex.Message);
        }
    }

    private async Task OnCuePlaybackEngineNaturalEndAsync()
    {
        try
        {
            await CuePlayer.OnMediaCueNaturallyEndedAsync();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "OnCuePlaybackEngineNaturalEndAsync: cue auto-follow failed");
            CuePlayer.StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowFailedFormat), ex.Message);
        }
    }

    private async Task RemovePlayer(MediaPlayerViewModel player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0) return;
        if (_detachedPlayerWindows.TryGetValue(player, out var detachedWindow))
            detachedWindow.Close();
        player.NaturalPlaybackEnded -= OnPlayerNaturalPlaybackEnded;
        player.DetachRequested -= OnPlayerDetachRequested;
        await player.DisposeAsync();
        Players.RemoveAt(idx);
        if (SelectedPlayer == player)
            SelectedPlayer = Players.Count > 0 ? Players[Math.Min(idx, Players.Count - 1)] : null;
    }

    /// <summary>Tracks the floating window per detached player — gates double-detach (second click
    /// just activates the window) and lets <see cref="RemovePlayer"/> close it.</summary>
    private readonly Dictionary<MediaPlayerViewModel, Views.Dialogs.DetachedPlayerWindow> _detachedPlayerWindows = new();

    private void OnPlayerDetachRequested(MediaPlayerViewModel player)
    {
        if (_detachedPlayerWindows.TryGetValue(player, out var existing))
        {
            existing.Activate();
            return;
        }

        if (!Players.Contains(player))
            return;

        // Non-modal: the operator keeps using the shell while players float on other screens.
        // The player stays in Players (saves/probes/pre-roll); it only leaves the tab list.
        var window = new Views.Dialogs.DetachedPlayerWindow { DataContext = player };
        _detachedPlayerWindows[player] = window;
        RebuildPlayerTabs();
        EnsureSelectedPlayerVisible();
        window.Closed += (_, _) =>
        {
            _detachedPlayerWindows.Remove(player);
            RebuildPlayerTabs();
            if (SelectedPlayer is null || !Players.Contains(SelectedPlayer))
                EnsureSelectedPlayerVisible();
        };

        if (TryGetOwnerWindow() is { } owner)
            window.Show(owner);
        else
            window.Show();
    }

    private async Task RefreshCuePreRollAsync()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MainViewModel.RefreshCuePreRollAsync", slowWarningMs: 1000);
        // Latest-request-wins: install our own token and cancel whatever was in flight. We only
        // Cancel the predecessor here (never Dispose) — each invocation disposes its own CTS in its
        // finally once it's done reading the token, which avoids a use-after-dispose race.
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _preRollRefreshCts, cts);
        try { previous?.Cancel(); } catch { /* predecessor already finishing */ }
        var ct = cts.Token;

        try
        {
            try
            {
                await _preRollRefreshGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timing?.SetOutcome("cancelled-before-gate");
                return; // superseded before we acquired the gate
            }

            try
            {
                if (ct.IsCancellationRequested)
                {
                    timing?.SetOutcome("cancelled");
                    return;
                }

                var player = SelectedPlayer;
                var list = CuePlayer.SelectedCueList;
                if (list is null)
                {
                    timing?.SetOutcome("no-cue-list");
                    return;
                }

                var engineTargets = CuePlayer.GetPreparedMediaCueTargets();
                var ndiTargets = CuePlayer.GetNdiPreConnectTargets();
                var paTargets = CuePlayer.GetPortAudioPreConnectTargets();
                Trace.LogDebug(
                    "RefreshCuePreRollAsync: engineTargets={EngineTargets} ndiTargets={NdiTargets} portAudioTargets={PortAudioTargets} player={Player}",
                    engineTargets.Count,
                    ndiTargets.Count,
                    paTargets.Count,
                    player?.Name ?? "<none>");

                player?.InvalidateCuePreRoll();
                if (_cueShowSession is { } showSession)
                {
                    // The gated transport must prepare through the same ShowSession graph it fires. Running the
                    // legacy CuePlaybackEngine pre-roll here opened a second, differently-configured player
                    // (often video-only when cue audio routes existed), obscured diagnostics, and could leave the
                    // actual ShowSession graph stale. Flush pending edits first, then warm its upcoming cue(s).
                    // BUT NOT while a cue is playing: the flush is a full LoadDocument that rebuilds the graph and
                    // tears down the running cue — which is exactly what stopped a playing text cue on every edit
                    // (pre-roll refresh fires per edit). While playing, the edit lands live (the frame swap) or on
                    // the next fire's own flush; warm the current graph as-is.
                    if (!CuePlayer.HasActiveCues)
                        await EnsureCueShowSessionCurrentAsync().ConfigureAwait(false);
                    await showSession.WarmUpcomingAsync(count: Math.Max(1, engineTargets.Count)).ConfigureAwait(false);
                }
                else
                {
                    await _cuePlaybackEngine.RefreshPreparedCuesAsync(engineTargets, ct).ConfigureAwait(false);
                }
                if (player is null)
                {
                    timing?.SetOutcome("no-selected-player");
                    return;
                }
                ct.ThrowIfCancellationRequested();
                await player.RefreshNdiPreConnectAsync(ndiTargets).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await player.RefreshPortAudioPreConnectAsync(paTargets).ConfigureAwait(false);
                timing?.SetOutcome($"engine={engineTargets.Count} ndi={ndiTargets.Count} portAudio={paTargets.Count}");
            }
            catch (OperationCanceledException)
            {
                timing?.SetOutcome("superseded");
                /* superseded by a newer refresh */
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "RefreshCuePreRollAsync: pre-roll refresh failed");
                timing?.SetOutcome("failed");
                /* best effort — pre-roll must not break transport */
            }
            finally
            {
                _preRollRefreshGate.Release();
            }
        }
        finally
        {
            // Clear our slot if it's still ours, then dispose the CTS we own.
            Interlocked.CompareExchange(ref _preRollRefreshCts, null, cts);
            cts.Dispose();
        }
    }

    private async Task<string?> ExecuteCueActionAsync(ActionCueNode cue, CancellationToken ct)
    {
        try
        {
            return cue.ActionKind switch
            {
                CueActionKind.OscOut => await ExecuteCueOscAsync(cue, ct),
                CueActionKind.MidiOut => await Task.Run(() => ExecuteCueMidi(cue, ct), ct),
                _ => Strings.Format(nameof(Strings.ActionKindNotWiredFormat), cue.ActionKind),
            };
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> ExecuteCueOscAsync(ActionCueNode cue, CancellationToken ct)
    {
        OscActionEndpoint? endpoint = null;
        if (cue.EndpointId is { } endpointId)
        {
            endpoint = ActionEndpoints
                .OfType<OscActionEndpoint>()
                .FirstOrDefault(e => e.Id == endpointId);
            if (endpoint is null)
                return Strings.Format(nameof(Strings.OscEndpointMissingFormat), endpointId);
        }

        var spec = ParseOscSpec(cue.AddressOrMessage, endpoint);
        using var client = await OSCClient.CreateAsync(spec.Host, spec.Port, cancellationToken: ct);
        await client.SendMessageAsync(spec.Address, spec.Arguments, ct);
        return Strings.Format(nameof(Strings.OscSendResultFormat), spec.Host, spec.Port, spec.Address);
    }

    private string ExecuteCueMidi(ActionCueNode cue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var endpoint = cue.EndpointId is { } endpointId
            ? ActionEndpoints.OfType<MidiActionEndpoint>().FirstOrDefault(e => e.Id == endpointId)
            : ActionEndpoints.OfType<MidiActionEndpoint>().FirstOrDefault();
        if (cue.EndpointId is not null && endpoint is null)
            return Strings.Format(nameof(Strings.MidiEndpointMissingFormat), cue.EndpointId);

        var initErr = EnsureMidiInitialized();
        if (initErr is not null)
            return initErr;

        var devices = PMUtil.GetOutputDevices();
        if (devices.Count == 0)
            return Strings.MidiNoOutputDevices;

        var device = ResolveMidiDevice(endpoint, devices);
        if (device is null)
            return endpoint is null
                ? Strings.MidiNoSuitableOutputDevice
                : Strings.Format(nameof(Strings.MidiEndpointDeviceNotFoundFormat), endpoint.DeviceName ?? endpoint.DeviceId?.ToString() ?? Strings.UnsetLabel);

        var spec = ParseMidiSpec(cue.AddressOrMessage, endpoint, device.Value.Id);
        using var outDevice = new MIDIOutputDevice(spec.DeviceId);
        var openErr = outDevice.Open();
        if (openErr != PmError.NoError)
            return Strings.Format(nameof(Strings.MidiOpenFailedFormat), PMUtil.GetErrorText(openErr) ?? openErr.ToString());
        var writeErr = outDevice.Write(spec.Message);
        if (writeErr != PmError.NoError)
            return Strings.Format(nameof(Strings.MidiWriteFailedFormat), PMUtil.GetErrorText(writeErr) ?? writeErr.ToString());
        return Strings.Format(nameof(Strings.MidiSendResultFormat), device.Value.Name ?? Strings.Format(nameof(Strings.DeviceHashIdFormat), device.Value.Id), spec.Description);
    }

    private string? EnsureMidiInitialized()
    {
        lock (_midiInitSync)
        {
            if (_midiInitialized)
                return null;
            var err = PMUtil.Initialize();
            if (err != PmError.NoError)
                return Strings.Format(nameof(Strings.MidiInitFailedFormat), PMUtil.GetErrorText(err) ?? err.ToString());
            _midiInitialized = true;
            return null;
        }
    }

    private static PmDeviceEntry? ResolveMidiDevice(MidiActionEndpoint? endpoint, IReadOnlyList<PmDeviceEntry> outputs)
    {
        if (endpoint is { DeviceId: { } id })
        {
            var byId = outputs.FirstOrDefault(d => d.Id == id);
            if (byId.Id == id)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(endpoint?.DeviceName))
        {
            var byName = outputs.FirstOrDefault(d =>
                string.Equals(d.Name, endpoint.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(byName.Name))
                return byName;
        }

        return outputs.FirstOrDefault();
    }

    private static OscSpec ParseOscSpec(string raw, OscActionEndpoint? endpoint)
    {
        var host = string.IsNullOrWhiteSpace(endpoint?.Host) ? "127.0.0.1" : endpoint.Host;
        var port = endpoint is { Port: > 0 } ? endpoint.Port : 9000;
        var tokens = raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count == 0)
            return new OscSpec(host, port, "/cue/go", []);

        string address;
        var argsStart = 1;

        if (tokens[0].StartsWith("osc://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(tokens[0], UriKind.Absolute, out var uri))
        {
            host = string.IsNullOrWhiteSpace(uri.Host) ? host : uri.Host;
            port = uri.Port > 0 ? uri.Port : port;
            address = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/cue/go" : uri.AbsolutePath;
        }
        else if (tokens[0].Contains(':') && tokens.Count > 1 && tokens[1].StartsWith('/'))
        {
            var hp = tokens[0].Split(':', 2, StringSplitOptions.TrimEntries);
            host = string.IsNullOrWhiteSpace(hp[0]) ? host : hp[0];
            if (hp.Length > 1 && int.TryParse(hp[1], out var parsedPort) && parsedPort is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
                port = parsedPort;
            address = tokens[1];
            argsStart = 2;
        }
        else
        {
            address = tokens[0].StartsWith('/') ? tokens[0] : "/" + tokens[0];
        }

        if (!OSCMessage.IsValidAddress(address))
            address = "/cue/go";

        var args = new List<OSCArgument>();
        for (var i = argsStart; i < tokens.Count; i++)
            args.Add(ParseOscArgumentToken(tokens[i]));

        return new OscSpec(host, port, address, args);
    }

    private static MidiSpec ParseMidiSpec(string raw, MidiActionEndpoint? endpoint, int fallbackDeviceId)
    {
        var deviceId = endpoint?.DeviceId ?? fallbackDeviceId;
        var parsed = CueMidiActionMessage.CreateMessage(raw, endpoint?.Channel ?? 0);
        return new MidiSpec(deviceId, parsed.Message, parsed.Description);
    }

    private static OSCArgument ParseOscArgumentToken(string token)
    {
        if (bool.TryParse(token, out var b))
            return b ? OSCArgument.True() : OSCArgument.False();
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32))
            return OSCArgument.Int32(i32);
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var f32))
            return OSCArgument.Float32(f32);
        return OSCArgument.String(token);
    }

    private sealed record OscSpec(string Host, int Port, string Address, IReadOnlyList<OSCArgument> Arguments);
    private sealed record MidiSpec(int DeviceId, IMIDIMessage Message, string Description);
    public sealed record MidiInputOption(int Id, string Name);
    public sealed record MidiOutputOption(int Id, string Name);

    /// <summary>
    /// Phase A — build a <see cref="HaPlayProject"/> snapshot from the current VM state. Pure projection,
    /// no I/O. Phase B will wire this through a File → Save menu; for now tests and programmatic callers
    /// can round-trip via <see cref="ProjectIO"/>.
    /// </summary>
    public HaPlayProject BuildProjectSnapshot() => BuildProjectSnapshot(sections: null);

    /// <summary>
    /// Scoped snapshot (save/load rework 2026-06-10): when <paramref name="sections"/> is non-null,
    /// only the listed <see cref="ProjectSections"/> leaves are filled in and the file records them
    /// in <see cref="HaPlayProject.SavedSections"/> — loading such a file applies only those parts.
    /// </summary>
    public HaPlayProject BuildProjectSnapshot(IReadOnlyCollection<string>? sections)
    {
        bool Has(string leaf) => ProjectSections.Includes(sections, leaf);

        var outputs = OutputManagement.Outputs
            .Select(o => o.Definition)
            .Where(d => d is PortAudioOutputDefinition ? Has(ProjectSections.OutputsAudio) : Has(ProjectSections.OutputsVideo))
            .ToList();

        return new HaPlayProject
        {
            SchemaVersion = HaPlayProject.CurrentSchemaVersion,
            HaPlayVersion = typeof(MainViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(),
            SavedSections = sections is null ? null : ProjectSections.Normalize(sections),
            Outputs = outputs,
            SharedHeadphonesBuses = Has(ProjectSections.OutputsAudio)
                ? OutputManagement.BuildSharedHeadphonesBusesSnapshot().ToList()
                : [],
            Players = Has(ProjectSections.Players) ? Players.Select(p => p.BuildPlayerConfigSnapshot()).ToList() : [],
            ActionEndpoints = ActionEndpoints
                .Where(e => e is MidiActionEndpoint ? Has(ProjectSections.TargetsMidi) : Has(ProjectSections.TargetsOsc))
                .ToList(),
            CueLists = Has(ProjectSections.CueLists) ? CuePlayer.BuildCueListsSnapshot() : [],
            Soundboards = Has(ProjectSections.Soundboards) ? Soundboard.BuildSnapshot() : [],
            ControlSystem = Has(ProjectSections.Control) ? Control.BuildSnapshot() : new ControlSystemConfig(),
        };
    }

    /// <summary>
    /// Applies a previously-saved <see cref="HaPlayProject"/> to this VM. Player count is matched (extra
    /// players added, surplus removed); outputs replace the existing list. Routing references inside
    /// player configs are matched by <see cref="OutputDefinition.DisplayName"/> per the existing
    /// <see cref="MediaPlayerConfig.SelectedOutputDisplayNames"/> contract.
    /// </summary>
    /// <remarks>
    /// Phase A intentionally does NOT spin up the underlying runtimes (PortAudio open / NDI start /
    /// preview windows) — that's a Phase B concern. Tests use this to verify round-trip projection
    /// without touching real devices.
    /// </remarks>
    public void ApplyProjectSnapshot(HaPlayProject project)
    {
        // Save/load rework 2026-06-10: a partial file (SavedSections != null) applies ONLY its own
        // sections; everything else in the live show is left untouched, so partial project files
        // double as section imports. null = full project, original replace-everything semantics.
        var sections = project.SavedSections;
        bool Has(string leaf) => ProjectSections.Includes(sections, leaf);
        var hasAudioOut = Has(ProjectSections.OutputsAudio);
        var hasVideoOut = Has(ProjectSections.OutputsVideo);

        if (hasAudioOut || hasVideoOut)
        {
            // Merge: keep current definitions of the kinds the file does NOT carry.
            var merged = new List<OutputDefinition>();
            if (!hasAudioOut)
                merged.AddRange(OutputManagement.Outputs.Select(o => o.Definition).Where(d => d is PortAudioOutputDefinition));
            if (!hasVideoOut)
                merged.AddRange(OutputManagement.Outputs.Select(o => o.Definition).Where(d => d is not PortAudioOutputDefinition));
            merged.AddRange(project.Outputs);
            OutputManagement.ReplaceDefinitionsForLoad(merged);
        }

        // UI rewrite P2: the virtual-audio-channel ("VOut") model was removed in favor of output
        // aliases + per-player matrix presets. Old projects still load; tell the operator once.
        if (project.VirtualAudioChannels.Count > 0)
            ToastCenter.Info(Strings.VirtualChannelsMigratedToast);
        if (hasAudioOut)
            OutputManagement.ApplySharedHeadphonesBuses(project.SharedHeadphonesBuses);

        var hasMidiTargets = Has(ProjectSections.TargetsMidi);
        var hasOscTargets = Has(ProjectSections.TargetsOsc);
        if (hasMidiTargets || hasOscTargets)
        {
            var keep = ActionEndpoints
                .Where(e => e is MidiActionEndpoint ? !hasMidiTargets : !hasOscTargets)
                .ToList();
            ActionEndpoints.Clear();
            foreach (var endpoint in keep.Concat(project.ActionEndpoints))
                ActionEndpoints.Add(endpoint);
            RebuildEndpointWorkspaceLists();
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        }

        if (Has(ProjectSections.CueLists))
            CuePlayer.ApplyCueLists(project.CueLists);
        if (Has(ProjectSections.Soundboards))
        {
            FireAndLog(_soundboardEngine.StopAllAsync(), "SoundboardEngine.StopAllAsync project-load");
            Soundboard.ApplySnapshot(project.Soundboards);
        }
        if (Has(ProjectSections.Control))
            Control.LoadConfig(project.ControlSystem);
        RebuildProjectMidiDeviceRows();

        if (!Has(ProjectSections.Players))
        {
            FireAndLog(RefreshCuePreRollAsync(), "RefreshCuePreRollAsync project-load-no-players");
            return;
        }

        // Reconcile players: extend or shrink to match the project's player count, then apply each one.
        while (Players.Count < project.Players.Count)
            Players.Add(new MediaPlayerViewModel(OutputManagement, Strings.Format(nameof(Strings.PlayerNameFormat), _nextPlayerNumber++), RemovePlayer));
        while (Players.Count > project.Players.Count && Players.Count > 1)
        {
            var removed = Players[^1];
            removed.NaturalPlaybackEnded -= OnPlayerNaturalPlaybackEnded;
            removed.DetachRequested -= OnPlayerDetachRequested;
            Players.RemoveAt(Players.Count - 1);
            FireAndLog(removed.DisposeAsync(), "MediaPlayerViewModel.DisposeAsync project-load-removed-player");
        }

        for (var i = 0; i < project.Players.Count && i < Players.Count; i++)
            Players[i].ApplyPlayerConfigSnapshot(project.Players[i]);

        FireAndLog(RefreshCuePreRollAsync(), "RefreshCuePreRollAsync project-load");
    }

    // ----- Phase B (§7): Project save / load command plumbing --------------------------------

    /// <summary>Path of the project file last saved or opened in this session — drives "Save" vs "Save As".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectTitle))]
    [NotifyPropertyChangedFor(nameof(HasOpenProject))]
    private string? _currentProjectPath;

    // Project-relative control scripts resolve against the project folder; keep the Control workspace's
    // script root in sync with the open project file.
    partial void OnCurrentProjectPathChanged(string? value) =>
        Control.SetProjectRoot(string.IsNullOrEmpty(value) ? null : Path.GetDirectoryName(value));

    /// <summary>Status banner text for the title bar (e.g. "Loaded. Missing outputs: …").</summary>
    [ObservableProperty]
    private string? _projectStatus;

    public ObservableCollection<string> RecentProjects { get; } = new();
    public bool HasNoRecentProjects => RecentProjects.Count == 0;

    public bool HasOpenProject => !string.IsNullOrEmpty(CurrentProjectPath);

    public string ProjectTitle =>
        string.IsNullOrEmpty(CurrentProjectPath)
            ? Strings.ProjectTitleUntitled
            : Strings.Format(nameof(Strings.ProjectTitleFormat), Path.GetFileNameWithoutExtension(CurrentProjectPath));

    /// <summary>Default location for project files (§7.3 — ~/Documents/HaPlay Projects/).</summary>
    public static string DefaultProjectsFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "HaPlay Projects");
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestSelectedOscEndpoint))]
    private async Task TestSelectedOscEndpointAsync()
    {
        if (SelectedActionEndpoint is not OscActionEndpoint)
            return;
        var row = SelectedOscEndpointRow ?? FindEndpointRow(SelectedActionEndpoint.Id);
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingOscStatus;
        await ProbeEndpointRowAsync(row, CancellationToken.None);
        EndpointTestStatus = row.Health switch
        {
            ActionEndpointHealthState.Ok => Strings.Format(nameof(Strings.OscTestOkStatusFormat), row.HealthDetail),
            ActionEndpointHealthState.Failed => Strings.Format(nameof(Strings.OscTestFailedStatusFormat), row.HealthDetail),
            _ => Strings.OscTestFinishedStatus,
        };
    }

    private bool CanTestSelectedOscEndpoint() => SelectedActionEndpoint is OscActionEndpoint;

    [RelayCommand(CanExecute = nameof(CanTestSelectedMidiEndpoint))]
    private async Task TestSelectedMidiEndpointAsync()
    {
        if (SelectedActionEndpoint is not MidiActionEndpoint)
            return;
        var row = SelectedMidiEndpointRow ?? FindEndpointRow(SelectedActionEndpoint.Id);
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingMidiStatus;
        await ProbeEndpointRowAsync(row, CancellationToken.None);
        EndpointTestStatus = row.Health switch
        {
            ActionEndpointHealthState.Ok => Strings.Format(nameof(Strings.MidiTestOkStatusFormat), row.HealthDetail),
            ActionEndpointHealthState.Failed => Strings.Format(nameof(Strings.MidiTestFailedStatusFormat), row.HealthDetail),
            _ => Strings.MidiTestFinishedStatus,
        };
    }

    private bool CanTestSelectedMidiEndpoint() => IsMidiAvailable && SelectedActionEndpoint is MidiActionEndpoint;

    private sealed class ProjectMidiOutputRowBuilder
    {
        public int? DeviceId { get; set; }

        public string? DeviceName { get; set; }

        public Guid? ControlDeviceId { get; set; }

        public Guid? CueEndpointId { get; set; }

        public ProjectMidiOutputRowViewModel ToRow() =>
            new(ControlDeviceId, CueEndpointId, DeviceId, DeviceName);
    }

    private static Window? TryGetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    private const int RecentProjectsCap = 8;

    private void PushRecentProject(string path)
    {
        // Move-to-front: if it's already in the list, lift it; otherwise prepend. Cap depth so a
        // long-running operator with 200 shows doesn't get an unmanageable menu.
        for (var i = RecentProjects.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentProjects[i], path, StringComparison.OrdinalIgnoreCase))
                RecentProjects.RemoveAt(i);
        }

        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > RecentProjectsCap)
            RecentProjects.RemoveAt(RecentProjects.Count - 1);

        try { SaveRecentProjects(); } catch { /* best effort */ }
    }

    /// <summary>Stored alongside the user's profile so it survives reinstalls of the app.</summary>
    private static string RecentProjectsFilePath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "HaPlay", "recent-projects.json");
        }
    }

    public void LoadRecentProjects()
    {
        try
        {
            var path = RecentProjectsFilePath;
            if (!File.Exists(path))
                return;
            using var stream = File.OpenRead(path);
            var list = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.ListString);
            if (list is null) return;
            RecentProjects.Clear();
            foreach (var p in list.Take(RecentProjectsCap))
                RecentProjects.Add(p);
        }
        catch
        {
            /* corrupted recent-projects file: ignore, will be overwritten on next save */
        }
    }

    private void SaveRecentProjects()
    {
        var path = RecentProjectsFilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, RecentProjects.ToList(), AppSettingsJsonContext.Default.ListString);
    }

    /// <summary>Bound from the recent-projects menu items. The parameter is <see cref="object"/>, not
    /// <see cref="string"/>: the recent-projects submenu binds this command via an item Style that also
    /// matches the submenu's header MenuItem (whose DataContext is this view-model). A string-typed
    /// RelayCommand casts the CommandParameter in CanExecute, so that header would throw
    /// InvalidCastException when the menu renders — making the whole File menu fail to open. Accepting
    /// <c>object?</c> and guarding on the actual path avoids the cast and is harmless for the header.</summary>
    [RelayCommand]
    private Task OpenRecentAsync(object? path) =>
        path is string p && !string.IsNullOrWhiteSpace(p)
            ? OpenProjectFromPathAsync(p)
            : Task.CompletedTask;
}
