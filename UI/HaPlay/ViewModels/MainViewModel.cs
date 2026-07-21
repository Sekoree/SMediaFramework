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
using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.Resources;
using HaPlay.Services;
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
    // The cue workspace's ShowSession runtime, its output leases, reloads, and progress polls all live on the
    // coordinator (extracted from this VM - review Part-5 #3); this VM only constructs and forwards to it.
    private readonly CueShowSessionCoordinator _cueShow;
    private int _shutdownCleanupStarted;
    private bool _midiInitialized;
    private readonly EndpointHealthMonitor _endpointHealth;

    /// <summary>Crash-recovery autosave (§ session restore): captures the full project to the cache on a cadence
    /// and, on a clean shutdown, deletes its session folder so nothing is offered for restore.</summary>
    private readonly SessionRecoveryService _recovery;
    private readonly ProjectPersistenceCoordinator _projectPersistence = new();
    private bool _discardUnsavedOnShutdown;
    private bool _autoSaveSuspendedForRecovery;

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
        Control = new ControlWorkspaceViewModel();
        CuePlayer.SetAvailableOutputs(OutputManagement.Outputs);
        Players = new ObservableCollection<MediaPlayerViewModel>();
        Players.CollectionChanged += (_, _) => OnPlayersChangedForPlayerTabs();
        RecentProjects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRecentProjects));
        // First player can't be removed - there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];
        Soundboard = new SoundboardWorkspaceViewModel(OutputManagement);
        Soundboard.ProbeDurationCallback = CueMediaProbe.TryProbeDurationMsAsync;
        CuePlayer.ActionCueExecutor = ExecuteCueActionAsync;
        CuePlayer.PreRollRefreshSuggested += (_, _) =>
            FireAndLog(RefreshCuePreRollAsync(), "RefreshCuePreRollAsync suggested");
        CuePlayer.RefreshPreviewAudioDevices();
        // The cue workspace + soundboard run on the headless ShowSession - the only playback runtime since the
        // legacy CuePlaybackEngine/SoundboardEngine were deleted (NXT-06/NXT-13 cutover completion). The session,
        // its output leases, reloads, and polls are owned by the coordinator.
        _cueShow = new CueShowSessionCoordinator(CuePlayer, Soundboard, OutputManagement);
        _cueShow.WireShowSessionCueTransport();
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        ActionEndpoints.CollectionChanged += OnActionEndpointsCollectionChanged;
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        RefreshMIDIDeviceCatalog();
        // APP-02: the endpoint-health polling lifecycle (5 s timer, single-flight guard, per-run CTS, timing)
        // lives in a dedicated service; the view model keeps only the probe loop (ProbeAllEndpointsAsync).
        // APP-01 behaviour - poll only while endpoints exist - is preserved by the pending-count gate + SyncEnabled.
        _endpointHealth = new EndpointHealthMonitor(
            TimeSpan.FromSeconds(5),
            () => OSCEndpointRows.Count + MIDIEndpointRows.Count,
            ProbeAllEndpointsAsync,
            Trace);
        FireAndLog(_endpointHealth.RefreshAsync(), "EndpointHealthMonitor startup");
        _endpointHealth.SyncEnabled();

        // Phase B (§3.6) - give the Edit dialog a way to ask "is any player playing through this line?".
        // Iterating the Players collection on each probe is fine: outputs are edited rarely, never
        // during a hot loop, and this is the single source of truth that doesn't require a new event.
        OutputManagement.PlaybackUsageProbe =
            line => Players.Any(p => p.IsActivelyPlayingThroughLine(line));
        OutputManagement.ActivePlayersProbe = () => Players;

        // I/O Debug page: same probe family as the health poll, plus the cue session via its coordinator.
        PipelineStats = new PipelineStatsViewModel
        {
            ActivePlayersProbe = () => Players,
            CueSessionProbe = () => _cueShow.PipelineStatsSession,
        };
        PipelineStats.Start();

        LoadRecentProjects();
        _appSettings = AppSettings.Load();
        CuePlayer.Hotkeys = _appSettings.CueHotkeys.Copy();
        _sidebarCollapsed = _appSettings.SidebarCollapsed;
        // Appearance (§8.6) - load saved values and apply them immediately. The OnXChanged hooks would
        // re-save on first set; seed via backing fields so we don't fire that pointlessly.
        _baseTheme = _appSettings.BaseTheme;
        _theme = _appSettings.Theme;
        _density = _appSettings.Density;
        // Snapshot what we compose this launch; a later change to any of these is "pending" until the next
        // restart (see AppearanceChangePending). All appearance settings are apply-at-startup only.
        _startupBaseTheme = _baseTheme;
        _startupTheme = _theme;
        _startupDensity = _density;
        // Base theme first (it decides whether the variant/density even apply), then variant + density.
        // ApplyTheme pins Light for Classic; ApplyDensity is a no-op unless Fluent is active.
        AppearanceController.ApplyBaseTheme(_baseTheme);
        AppearanceController.ApplyTheme(_theme);
        AppearanceController.ApplyDensity(_density);
        if (!Playback.PlaybackVideoPipeline.CliRequestedUyvyPassthrough)
            Playback.PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = _appSettings.PreferLiveUyvyPassthrough;
        var lastWorkspaceId = WorkspaceItem.MigrateLegacyId(_appSettings.LastSelectedWorkspace);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == lastWorkspaceId)
                            ?? WorkspaceItem.Players;
        ToastCenter.Sink = OnToastPosted;
        ToastCenter.ActionSink = OnActionToastPosted;

        // Remote API (per-machine setting) - seed via backing fields so the OnXChanged hooks don't
        // re-save during construction, then bring the listener up if it was left enabled.
        _restApiEnabled = _appSettings.RestApiEnabled;
        _restApiPort = _appSettings.RestApiPort is >= 1 and <= 65535 ? _appSettings.RestApiPort : 8990;
        _restApiAllowLan = _appSettings.RestApiAllowLan;
        // Optional token (API-01): no token by default - the remote API targets closed-LAN automation
        // (e.g. Bitfocus Companion). A token is only required when the operator sets one.
        _restApiAccessToken = _appSettings.RestApiAccessToken ?? string.Empty;
        RestartRestApi();

        // Session restore: start capturing to the cache. The startup scan for a crashed session runs later, once
        // the window is up (MainWindow.OnOpened → CheckForRecoverableSessionAsync), so it can show a dialog.
        _recovery = new SessionRecoveryService(
            buildSnapshot: BuildProjectSnapshot,
            currentProjectPath: () => CurrentProjectPath,
            autoSaveEnabled: () => AutoSaveEnabled && !_autoSaveSuspendedForRecovery,
            recoveryScripts: Control.BuildRecoveryScriptFiles,
            haPlayVersion: HaPlayVersionString,
            untitledTitle: Strings.RecoverSessionUntitledLabel,
            persistProject: PersistProjectForRecoveryAsync,
            isProjectPersisted: _projectPersistence.IsPersisted);
        _recovery.StatusChanged += OnRecoveryStatusChanged;
        if (Environment.GetEnvironmentVariable("HAPLAY_DISABLE_RECOVERY_TIMER") is not ("1" or "true"))
            _recovery.Start();

        // Baseline for the unsaved-changes check: a freshly-launched, untouched project is "clean".
        MarkProjectClean();

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

    /// <summary>Best-effort shutdown teardown (called by the app lifetime): tears down the cue workspace's
    /// ShowSession + its output leases via the coordinator. Players are reclaimed by process-exit, as before.</summary>
    public void ShutdownCleanup()
    {
        if (Interlocked.Exchange(ref _shutdownCleanupStarted, 1) != 0)
            return;
        // Finalize recovery first, while the view-model graph is still intact: it flushes a final auto-save (when
        // enabled) and removes this session's recovery folder so a clean exit is not mistaken for a crash.
        _recovery.FinalizeCleanShutdown(
            discardChanges: _discardUnsavedOnShutdown,
            retainRecovery: !_discardUnsavedOnShutdown && HasUnsavedChanges);
        _endpointHealth.Dispose();
        _remoteApi.Dispose();
        _cueShow.ShutdownCleanup();
        // Finalize armed file recordings LAST-ish but before the media host teardown: flushes encoders
        // and writes container trailers so an exit mid-record never leaves a truncated file.
        OutputManagement.FinishAllRecordingsForShutdown();
    }

    // ----- Remote API (HTTP) ---------------------------------------------------------------------

    // APP-02: the listener + dispatcher lifecycle lives in a dedicated host; the view model keeps only the
    // bound settings + presentation below.
    private readonly Remote.RemoteApiHost _remoteApi = new();

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
    [NotifyPropertyChangedFor(nameof(RestApiAccessTokenDisplay))]
    [NotifyPropertyChangedFor(nameof(HasRestApiAccessToken))]
    private string _restApiAccessToken = string.Empty;

    /// <summary>When false (default) the token is shown masked; the Reveal toggle flips it (API-01), so the
    /// secret is not shoulder-surfed or captured in a screenshot of the Project workspace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestApiAccessTokenDisplay))]
    private bool _revealRestApiToken;

    /// <summary>Fixed-width mask (length-independent, so it does not leak the token length).</summary>
    private const string RestApiTokenMask = "••••••••••••••••";

    /// <summary>Shown in the Project workspace card (and the base for Copy-API-URL menus).</summary>
    public string RestApiBaseUrlDisplay =>
        _remoteApi.IsRunning && _remoteApi.BaseUrl is { } url
            ? url
            : Strings.RemoteApiDisabledStatus;

    /// <summary>Degradation note (e.g. Windows loopback fallback) or bind error; null when clean.</summary>
    public string? RestApiStatusNote => _remoteApi.StatusNote;

    /// <summary>True when the API is reachable from the network with NO token - the deliberate
    /// zero-friction Companion mode. It stays supported, but the state must be unmistakable in the
    /// UI (review P2-7): this drives the prominent trusted-network warning in the Project card.</summary>
    public bool RestApiOpenLanActive =>
        RestApiEnabled && RestApiAllowLan && string.IsNullOrEmpty(RestApiAccessToken);

    public string RestApiSecurityStatus =>
        (RestApiAllowLan ? Strings.RemoteApiSecurityLan : Strings.RemoteApiSecurityLoopback)
        + " "
        + (string.IsNullOrEmpty(RestApiAccessToken)
            ? Strings.RemoteApiTokenOptional
            : Strings.RemoteApiTokenRequired);

    /// <summary>Token field text: a "(none)" placeholder when auth is off, the raw token when revealed, else a
    /// fixed-width mask so it is not shoulder-surfed or captured in a screenshot (API-01).</summary>
    public string RestApiAccessTokenDisplay =>
        string.IsNullOrEmpty(RestApiAccessToken) ? Strings.RemoteApiTokenNone
        : RevealRestApiToken ? RestApiAccessToken
        : RestApiTokenMask;

    /// <summary>True when a token is set (drives the Clear button's enabled state).</summary>
    public bool HasRestApiAccessToken => !string.IsNullOrEmpty(RestApiAccessToken);

    /// <summary>Endpoint cheat-sheet rendered as a list by the Project workspace card. Paths are
    /// protocol, not prose - only the descriptions localize.</summary>
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
        SaveOwnedAppSettings();
        RestartRestApi();
    }

    partial void OnRestApiPortChanged(int value)
    {
        _appSettings.RestApiPort = value;
        SaveOwnedAppSettings();
        RestartRestApi();
    }

    partial void OnRestApiAllowLanChanged(bool value)
    {
        _appSettings.RestApiAllowLan = value;
        SaveOwnedAppSettings();
        RestartRestApi();
    }

    /// <summary>Persists ONLY this view-model's owned settings fields via the merge-safe write path
    /// (review H5): serializing the long-lived `_appSettings` snapshot clobbered fields other writers
    /// (visualizer dialog, playlist toggles, dialog sizes) had saved since startup.</summary>
    private void SaveOwnedAppSettings() => AppSettings.Update(s =>
    {
        s.RestApiEnabled = _appSettings.RestApiEnabled;
        s.RestApiPort = _appSettings.RestApiPort;
        s.RestApiAllowLan = _appSettings.RestApiAllowLan;
        s.RestApiAccessToken = _appSettings.RestApiAccessToken;
        s.SidebarCollapsed = _appSettings.SidebarCollapsed;
        s.LastSelectedWorkspace = _appSettings.LastSelectedWorkspace;
        s.MainWindow = _appSettings.MainWindow;
        s.BaseTheme = _appSettings.BaseTheme;
        s.Theme = _appSettings.Theme;
        s.Density = _appSettings.Density;
        s.CueHotkeys = _appSettings.CueHotkeys.Copy();
    });

    private void RestartRestApi()
    {
        _remoteApi.Restart(
            RestApiEnabled, RestApiPort, RestApiAccessToken, RestApiAllowLan,
            () => new Remote.RemoteApiDispatcher(CuePlayer, () => Players, Soundboard, Control)
            {
                LanBindingEnabled = RestApiAllowLan,
                TokenConfigured = !string.IsNullOrEmpty(RestApiAccessToken),
            });

        // Copy-API-URL menus keep working while the listener is off - the copied URL targets the
        // configured port and becomes live the moment the API is enabled. The token is never embedded in
        // copied URLs (API-01); a token-protected server expects the X-HaPlay-Api-Key header instead.
        Remote.RemoteApi.BaseUrl = _remoteApi.AdvertisedBaseUrl(RestApiPort, RestApiAllowLan);
        OnPropertyChanged(nameof(RestApiBaseUrlDisplay));
        OnPropertyChanged(nameof(RestApiStatusNote));
        OnPropertyChanged(nameof(RestApiSecurityStatus));
        OnPropertyChanged(nameof(RestApiOpenLanActive));
    }

    partial void OnRestApiAccessTokenChanged(string value)
    {
        _appSettings.RestApiAccessToken = string.IsNullOrEmpty(value) ? null : value;
        SaveOwnedAppSettings();
        RestartRestApi();
    }

    /// <summary>Opt in to auth by generating a random token (the remote API is token-less by default).</summary>
    [RelayCommand]
    private void RegenerateRestApiToken() =>
        RestApiAccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Remove the token so the remote API accepts unauthenticated requests again.</summary>
    [RelayCommand]
    private void ClearRestApiToken()
    {
        RevealRestApiToken = false; // a subsequently generated token starts masked again
        RestApiAccessToken = string.Empty;
    }

    // ----- UI rewrite P1 (plan §1): toast overlay queue -----------------------------------------

    private const int MaxVisibleToasts = 3;
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(6);
    private DispatcherTimer? _toastSweepTimer;

    /// <summary>Visible toasts, newest last. Rendered by the MainView overlay - never part of layout.</summary>
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

    /// <summary>Action-carrying toast (e.g. "Removed 3 cues - Undo"). No dedup-refresh: each
    /// action is a distinct one-shot, and the longer deadline gives the operator time to react.</summary>
    private void OnActionToastPosted(string message, string actionLabel, Action action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnActionToastPosted(message, actionLabel, action));
            return;
        }

        while (Toasts.Count >= MaxVisibleToasts)
            Toasts.RemoveAt(0);
        Toasts.Add(new ToastViewModel(ToastSeverity.Info, message, t => Toasts.Remove(t), actionLabel, action)
        {
            DeadlineTicks = Environment.TickCount64 + 2 * (long)ToastLifetime.TotalMilliseconds,
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

    // Construct the three largest hidden views on first visit, not during the first-frame path. Once loaded,
    // retain their content so editor state and pop-out hosts survive subsequent workspace switches (PERF-01).
    private bool _cueWorkspaceLoaded;
    private bool _soundboardWorkspaceLoaded;
    private bool _controlWorkspaceLoaded;
    public CuePlayerViewModel? LoadedCueWorkspace => _cueWorkspaceLoaded ? CuePlayer : null;
    public SoundboardWorkspaceViewModel? LoadedSoundboardWorkspace => _soundboardWorkspaceLoaded ? Soundboard : null;
    public ControlWorkspaceViewModel? LoadedControlWorkspace => _controlWorkspaceLoaded ? Control : null;

    /// <summary>True when the operator has authored control scripts but never saved the project - they live
    /// only in the scratch cache and would be lost on close. Drives the on-close "save your work?" prompt.</summary>
    public bool HasUnsavedScratchScripts => LoadedControlWorkspace?.HasUnsavedScratchScripts == true;

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _appSettings.SidebarCollapsed = value;
        SaveOwnedAppSettings();
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItem value)
    {
        if (value == WorkspaceItem.Cues && !_cueWorkspaceLoaded)
        {
            _cueWorkspaceLoaded = true;
            OnPropertyChanged(nameof(LoadedCueWorkspace));
        }
        else if (value == WorkspaceItem.Soundboard && !_soundboardWorkspaceLoaded)
        {
            _soundboardWorkspaceLoaded = true;
            OnPropertyChanged(nameof(LoadedSoundboardWorkspace));
        }
        else if (value == WorkspaceItem.Control && !_controlWorkspaceLoaded)
        {
            _controlWorkspaceLoaded = true;
            OnPropertyChanged(nameof(LoadedControlWorkspace));
        }
        _appSettings.LastSelectedWorkspace = value.Id;
        SaveOwnedAppSettings();
        if (value == WorkspaceItem.Project)
            RefreshMediaCacheSizes(); // sizes are current by the time the operator sees the section
    }

    // ----- Phase E (§8.7): Main window state persistence -----------------------------------------

    /// <summary>Phase E (§8.7) - last saved main-window placement, or <see langword="null"/> on first
    /// launch. The window code-behind calls this on Opened to restore size/position; values are
    /// validated against the current screen layout before being applied.</summary>
    public WindowStateSnapshot? GetSavedWindowState() => _appSettings.MainWindow;

    /// <summary>Phase E (§8.7) - persist the current main-window placement. Called from the window
    /// code-behind on Closing (or on debounced size/move/state changes). Writes through to
    /// <c>app-settings.json</c> immediately so a hard kill still preserves the last-known good state.</summary>
    public void SaveWindowState(WindowStateSnapshot snapshot)
    {
        _appSettings.MainWindow = snapshot;
        SaveOwnedAppSettings();
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

    /// <summary>UX-03: open the searchable keyboard-shortcut help overlay (Help menu / F1).</summary>
    [RelayCommand]
    private async Task ShowKeyboardShortcutsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;
        await new Views.Dialogs.KeyboardShortcutsDialog(CuePlayer.Hotkeys, hotkeys =>
        {
            CuePlayer.Hotkeys = hotkeys.Copy();
            _appSettings.CueHotkeys = hotkeys.Copy();
            SaveOwnedAppSettings();
        }).ShowDialog(owner);
    }

    /// <summary>Ctrl+1..N keyboard handler. Index is 1-based to match the modifier key. (§12.5)</summary>
    public void SelectWorkspaceByIndex(int oneBasedIndex)
    {
        var idx = oneBasedIndex - 1;
        if (idx >= 0 && idx < Workspaces.Count)
            SelectedWorkspace = Workspaces[idx];
    }

    public OutputManagementViewModel OutputManagement { get; }

    /// <summary>The I/O workspace's Debug page (1 Hz pipeline timings across decks + cues).</summary>
    public PipelineStatsViewModel PipelineStats { get; }
    public CuePlayerViewModel CuePlayer { get; }
    public SoundboardWorkspaceViewModel Soundboard { get; }
    public ControlWorkspaceViewModel Control { get; }
    public ObservableCollection<MediaPlayerViewModel> Players { get; }
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    // ----- Phase E (§8.6): Theme & Density -------------------------------------------------------

    /// <summary>Gates the Project workspace appearance controls. Now that Simple/Fluent ship real dark +
    /// density resources (the Classic-only limitation UI-01 called out), the section is shown. All three
    /// settings (style / variant / density) are composed at startup; changing any surfaces a restart prompt
    /// (<see cref="ShowAppearanceRestartPrompt"/>). The variant + density controls disable themselves for base
    /// themes that don't support them (see <see cref="IsVariantSelectable"/> / <see cref="IsDensitySelectable"/>).</summary>
    public bool ShowAppearanceSettings => true;

    public IReadOnlyList<AppBaseTheme> BaseThemeChoices { get; } = Enum.GetValues<AppBaseTheme>();
    public IReadOnlyList<AppThemeMode> ThemeChoices { get; } = Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<AppDensityMode> DensityChoices { get; } = Enum.GetValues<AppDensityMode>();

    /// <summary>Base control theme (§8.6). ALL appearance settings apply at startup only: a live control-theme
    /// swap isn't reliable in Avalonia (realized controls keep their old templates, and re-styling mid-event
    /// can corrupt the tree), and the variant/density are grouped with it so the whole panel follows one
    /// consistent "change → restart to apply" model. Setting persists the choice; a change surfaces the
    /// restart prompt (see <see cref="ShowAppearanceRestartPrompt"/>).</summary>
    [ObservableProperty]
    private AppBaseTheme _baseTheme;

    /// <summary>Phase E (§8.6) - chrome light/dark variant. Persisted; composed at the next startup.</summary>
    [ObservableProperty]
    private AppThemeMode _theme;

    /// <summary>Phase E (§8.6) - Fluent density. Persisted; composed at the next startup.</summary>
    [ObservableProperty]
    private AppDensityMode _density;

    // The appearance actually painting the running window, captured at launch. A saved value that differs
    // from its snapshot is pending a restart.
    private AppBaseTheme _startupBaseTheme;
    private AppThemeMode _startupTheme;
    private AppDensityMode _startupDensity;
    private bool _appearancePromptDismissed;

    /// <summary>True when any appearance setting (style / variant / density) differs from what's running, so a
    /// restart is needed to compose it.</summary>
    public bool AppearanceChangePending =>
        BaseTheme != _startupBaseTheme || Theme != _startupTheme || Density != _startupDensity;

    /// <summary>Drives the "restart now / later" prompt: shown while a change is pending and the user hasn't
    /// chosen "Later" for it yet.</summary>
    public bool ShowAppearanceRestartPrompt => AppearanceChangePending && !_appearancePromptDismissed;

    /// <summary>The light/dark variant only applies to variant-aware base themes (Simple/Fluent); Classic
    /// is light-only, so its selector disables.</summary>
    public bool IsVariantSelectable => BaseTheme != AppBaseTheme.Classic;

    /// <summary>Density is a Fluent-only axis; the selector disables for Classic/Simple.</summary>
    public bool IsDensitySelectable => BaseTheme == AppBaseTheme.Fluent;

    partial void OnBaseThemeChanged(AppBaseTheme value)
    {
        _appSettings.BaseTheme = value;
        SaveOwnedAppSettings();
        // The variant/density selectors gate on the newly-selected base theme so the user can pre-pick them
        // for the pending style.
        OnPropertyChanged(nameof(IsVariantSelectable));
        OnPropertyChanged(nameof(IsDensitySelectable));
        MarkAppearanceChanged();
    }

    partial void OnThemeChanged(AppThemeMode value)
    {
        _appSettings.Theme = value;
        SaveOwnedAppSettings();
        MarkAppearanceChanged();
    }

    partial void OnDensityChanged(AppDensityMode value)
    {
        _appSettings.Density = value;
        SaveOwnedAppSettings();
        MarkAppearanceChanged();
    }

    // Re-surface the restart prompt on every appearance change (even if a prior change was dismissed) and
    // refresh the pending state.
    private void MarkAppearanceChanged()
    {
        _appearancePromptDismissed = false;
        OnPropertyChanged(nameof(AppearanceChangePending));
        OnPropertyChanged(nameof(ShowAppearanceRestartPrompt));
    }

    /// <summary>"Later" - keep the running appearance; the saved choice applies on the next launch.</summary>
    [RelayCommand]
    private void DismissAppearanceRestart()
    {
        _appearancePromptDismissed = true;
        OnPropertyChanged(nameof(ShowAppearanceRestartPrompt));
    }

    /// <summary>"Restart now" - relaunch so the saved appearance is composed fresh at startup.</summary>
    [RelayCommand]
    private void RestartApp() => AppRestart.Restart();

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
    public ObservableCollection<ActionEndpointRowViewModel> OSCEndpointRows { get; } = new();

    /// <summary>MIDI endpoints with persistent health LEDs for the MIDI sidebar workspace.</summary>
    public ObservableCollection<ActionEndpointRowViewModel> MIDIEndpointRows { get; } = new();

    [ObservableProperty]
    private ActionEndpointRowViewModel? _selectedOSCEndpointRow;

    [ObservableProperty]
    private ActionEndpointRowViewModel? _selectedMIDIEndpointRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedActionEndpoint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedOSCEndpoint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMIDIEndpoint))]
    private ActionEndpoint? _selectedActionEndpoint;

    public bool HasSelectedActionEndpoint => SelectedActionEndpoint is not null;
    public bool IsSelectedOSCEndpoint => SelectedActionEndpoint is OSCActionEndpoint;
    public bool IsSelectedMIDIEndpoint => SelectedActionEndpoint is MIDIActionEndpoint;

    [ObservableProperty]
    private string _endpointEditName = string.Empty;

    [ObservableProperty]
    private string _oSCEditHost = "127.0.0.1";

    [ObservableProperty]
    private int _oSCEditPort = 9000;

    [ObservableProperty]
    private string _mIDIEditDeviceName = string.Empty;

    [ObservableProperty]
    private int? _mIDIEditDeviceId;

    [ObservableProperty]
    private int _mIDIEditChannel;

    public ObservableCollection<MIDIOutputOption> MIDIOutputOptions { get; } = new();
    public ObservableCollection<MIDIInputOption> MIDIInputOptions { get; } = new();
    public ObservableCollection<ProjectMIDIInputRowViewModel> ProjectMIDIInputRows { get; } = new();
    public ObservableCollection<ProjectMIDIOutputRowViewModel> ProjectMIDIOutputRows { get; } = new();
    public bool HasNoProjectMIDIInputs => ProjectMIDIInputRows.Count == 0;
    public bool HasNoProjectMIDIOutputs => ProjectMIDIOutputRows.Count == 0;
    public bool IsMIDIAvailable => RuntimeModules.IsMIDIAvailable;
    public string MIDIUnavailableStatus => RuntimeModules.MIDIUnavailableReason ?? "MIDI runtime unavailable.";

    [ObservableProperty]
    private string? _mIDIDeviceStatus;

    [ObservableProperty]
    private MIDIOutputOption? _selectedMIDIOutputOption;

    [ObservableProperty]
    private MIDIInputOption? _selectedMIDIInputOption;

    [ObservableProperty]
    private ProjectMIDIInputRowViewModel? _selectedProjectMIDIInputRow;

    [ObservableProperty]
    private ProjectMIDIOutputRowViewModel? _selectedProjectMIDIOutputRow;

    [ObservableProperty]
    private string? _endpointTestStatus;

    partial void OnSelectedMIDIInputOptionChanged(MIDIInputOption? value)
    {
        _ = value;
        AddSelectedMIDIInputToControlCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMIDIOutputOptionChanged(MIDIOutputOption? value)
    {
        _ = value;
        UseSelectedMIDIOutputCommand.NotifyCanExecuteChanged();
        AddSelectedMIDIOutputToControlCommand.NotifyCanExecuteChanged();
        AddSelectedMIDIOutputEndpointCommand.NotifyCanExecuteChanged();
        AddSelectedMIDIOutputToProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProjectMIDIInputRowChanged(ProjectMIDIInputRowViewModel? value)
    {
        _ = value;
        RemoveSelectedProjectMIDIInputCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProjectMIDIOutputRowChanged(ProjectMIDIOutputRowViewModel? value)
    {
        _ = value;
        RemoveSelectedProjectMIDIOutputCommand.NotifyCanExecuteChanged();
        TestSelectedProjectMIDIOutputCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        if (value is null)
        {
            EndpointEditName = string.Empty;
            OSCEditHost = "127.0.0.1";
            OSCEditPort = 9000;
            MIDIEditDeviceName = string.Empty;
            MIDIEditDeviceId = null;
            MIDIEditChannel = 0;
        }
        else
        {
            EndpointEditName = value.Name;
            switch (value)
            {
                case OSCActionEndpoint osc:
                    OSCEditHost = osc.Host;
                    OSCEditPort = osc.Port;
                    break;
                case MIDIActionEndpoint midi:
                    MIDIEditDeviceName = midi.DeviceName ?? string.Empty;
                    MIDIEditDeviceId = midi.DeviceId;
                    MIDIEditChannel = midi.Channel;
                    SelectedMIDIOutputOption = MIDIOutputOptions.FirstOrDefault(o => o.Id == midi.DeviceId);
                    break;
            }
        }
        RemoveActionEndpointCommand.NotifyCanExecuteChanged();
        SaveActionEndpointEditsCommand.NotifyCanExecuteChanged();
        RefreshMIDIOutputsCommand.NotifyCanExecuteChanged();
        TestSelectedOSCEndpointCommand.NotifyCanExecuteChanged();
        TestSelectedMIDIEndpointCommand.NotifyCanExecuteChanged();
        EndpointTestStatus = null;
        SyncEndpointRowSelectionFromEndpoint();
    }

    partial void OnSelectedOSCEndpointRowChanged(ActionEndpointRowViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedActionEndpoint, value.Endpoint))
            SelectedActionEndpoint = value.Endpoint;
    }

    partial void OnSelectedMIDIEndpointRowChanged(ActionEndpointRowViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedActionEndpoint, value.Endpoint))
            SelectedActionEndpoint = value.Endpoint;
    }

    private void SyncEndpointRowSelectionFromEndpoint()
    {
        var id = SelectedActionEndpoint?.Id;
        SelectedOSCEndpointRow = id is null
            ? null
            : OSCEndpointRows.FirstOrDefault(r => r.Endpoint.Id == id);
        SelectedMIDIEndpointRow = id is null
            ? null
            : MIDIEndpointRows.FirstOrDefault(r => r.Endpoint.Id == id);
    }

    private void OnActionEndpointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        RemoveActionEndpointCommand.NotifyCanExecuteChanged();
        SyncEndpointHealthTimer(); // APP-01: start when endpoints appear, stop when the last one is removed
        FireAndLog(RefreshAllEndpointHealthAsync(), "RefreshAllEndpointHealthAsync endpoints-changed");
    }

    /// <summary>APP-01/APP-02: enable endpoint-health polling only while endpoints exist (delegated to the monitor).</summary>
    private void SyncEndpointHealthTimer() => _endpointHealth.SyncEnabled();

    private void RebuildEndpointWorkspaceLists()
    {
        var oscHealth = OSCEndpointRows.ToDictionary(r => r.Endpoint.Id, r => (r.Health, r.HealthDetail));
        OSCEndpointRows.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<OSCActionEndpoint>())
        {
            var row = new ActionEndpointRowViewModel(endpoint);
            if (oscHealth.TryGetValue(endpoint.Id, out var h))
            {
                row.Health = h.Health;
                row.HealthDetail = h.HealthDetail;
            }

            OSCEndpointRows.Add(row);
        }

        var midiHealth = MIDIEndpointRows.ToDictionary(r => r.Endpoint.Id, r => (r.Health, r.HealthDetail));
        MIDIEndpointRows.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<MIDIActionEndpoint>())
        {
            var row = new ActionEndpointRowViewModel(endpoint);
            if (midiHealth.TryGetValue(endpoint.Id, out var h))
            {
                row.Health = h.Health;
                row.HealthDetail = h.HealthDetail;
            }

            MIDIEndpointRows.Add(row);
        }

        SyncEndpointRowSelectionFromEndpoint();
        RebuildProjectMIDIDeviceRows();
    }

    private ActionEndpointRowViewModel? FindEndpointRow(Guid endpointId) =>
        OSCEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId)
        ?? MIDIEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId);

    private void RebuildProjectMIDIDeviceRows()
    {
        var selectedInputKey = SelectedProjectMIDIInputRow?.MatchKey;
        var selectedOutputKey = SelectedProjectMIDIOutputRow?.MatchKey;

        ProjectMIDIInputRows.Clear();
        foreach (var row in BuildProjectMIDIInputRows(Control.BuildSnapshot()))
            ProjectMIDIInputRows.Add(row);

        ProjectMIDIOutputRows.Clear();
        foreach (var row in BuildProjectMIDIOutputRows(Control.BuildSnapshot(), ActionEndpoints))
            ProjectMIDIOutputRows.Add(row);
        OnPropertyChanged(nameof(HasNoProjectMIDIInputs));
        OnPropertyChanged(nameof(HasNoProjectMIDIOutputs));

        SelectedProjectMIDIInputRow = selectedInputKey is null
            ? ProjectMIDIInputRows.FirstOrDefault()
            : ProjectMIDIInputRows.FirstOrDefault(r => r.MatchKey == selectedInputKey) ?? ProjectMIDIInputRows.FirstOrDefault();
        SelectedProjectMIDIOutputRow = selectedOutputKey is null
            ? ProjectMIDIOutputRows.FirstOrDefault()
            : ProjectMIDIOutputRows.FirstOrDefault(r => r.MatchKey == selectedOutputKey) ?? ProjectMIDIOutputRows.FirstOrDefault();

        RemoveSelectedProjectMIDIInputCommand.NotifyCanExecuteChanged();
        RemoveSelectedProjectMIDIOutputCommand.NotifyCanExecuteChanged();
        TestSelectedProjectMIDIOutputCommand.NotifyCanExecuteChanged();
    }

    internal static IReadOnlyList<ProjectMIDIInputRowViewModel> BuildProjectMIDIInputRows(ControlSystemConfig config) =>
        config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI)
            .Where(d => d.Binding.MIDIInputDeviceId is not null || !string.IsNullOrWhiteSpace(d.Binding.MIDIInputDeviceName))
            .Select(d => new ProjectMIDIInputRowViewModel(
                d.Id,
                d.Binding.MIDIInputDeviceId,
                d.Binding.MIDIInputDeviceName ?? d.Name))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<ProjectMIDIOutputRowViewModel> BuildProjectMIDIOutputRows(
        ControlSystemConfig config,
        IEnumerable<ActionEndpoint> endpoints)
    {
        var rows = new List<ProjectMIDIOutputRowBuilder>();

        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.MIDI))
        {
            var deviceId = device.Binding.MIDIOutputDeviceId;
            var deviceName = device.Binding.MIDIOutputDeviceName;
            if (deviceId is null && string.IsNullOrWhiteSpace(deviceName))
                continue;
            deviceName ??= device.Name;

            var row = FindOrAddProjectMIDIOutputRow(rows, deviceId, deviceName);
            row.ControlDeviceId = device.Id;
            row.DeviceId ??= deviceId;
            row.DeviceName = PreferMIDIDeviceName(row.DeviceName, deviceName);
        }

        foreach (var endpoint in endpoints.OfType<MIDIActionEndpoint>())
        {
            var deviceName = endpoint.DeviceName ?? endpoint.Name;
            if (endpoint.DeviceId is null && string.IsNullOrWhiteSpace(deviceName))
                continue;

            var row = FindOrAddProjectMIDIOutputRow(rows, endpoint.DeviceId, deviceName);
            row.CueEndpointId = endpoint.Id;
            row.DeviceId ??= endpoint.DeviceId;
            row.DeviceName = PreferMIDIDeviceName(row.DeviceName, deviceName);
        }

        return rows
            .Select(r => r.ToRow())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProjectMIDIOutputRowBuilder FindOrAddProjectMIDIOutputRow(
        List<ProjectMIDIOutputRowBuilder> rows,
        int? deviceId,
        string? deviceName)
    {
        var existing = rows.FirstOrDefault(r => MatchesMIDIBinding(r.DeviceId, r.DeviceName, deviceId, deviceName));
        if (existing is not null)
            return existing;

        var row = new ProjectMIDIOutputRowBuilder
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
        };
        rows.Add(row);
        return row;
    }

    private static bool MatchesMIDIBinding(int? firstId, string? firstName, int? secondId, string? secondName)
    {
        if (firstId is not null && secondId is not null && firstId == secondId)
            return true;

        return !string.IsNullOrWhiteSpace(firstName)
               && !string.IsNullOrWhiteSpace(secondName)
               && string.Equals(firstName.Trim(), secondName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? PreferMIDIDeviceName(string? existing, string? candidate) =>
        string.IsNullOrWhiteSpace(existing)
            ? candidate
            : existing;

    /// <summary>Runs an endpoint-health sweep now. The single-flight guard, per-run cancellation, timing, and the
    /// "nothing to probe → skip" gate all live in <see cref="EndpointHealthMonitor"/> (APP-02); this stays public
    /// for the startup and endpoint-set-changed callers.</summary>
    public Task RefreshAllEndpointHealthAsync() => _endpointHealth.RefreshAsync();

    /// <summary>The domain probe loop the monitor drives: probe every OSC + MIDI endpoint row under the run's
    /// cancellation token and return how many were probed.</summary>
    private async Task<int> ProbeAllEndpointsAsync(CancellationToken ct)
    {
        var probed = 0;
        foreach (var row in OSCEndpointRows.Concat(MIDIEndpointRows))
        {
            ct.ThrowIfCancellationRequested();
            await ProbeEndpointRowAsync(row, ct).ConfigureAwait(false);
            probed++;
        }
        return probed;
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
            OSCActionEndpoint osc => await ActionEndpointProbe.TryProbeOSCAsync(osc, ct).ConfigureAwait(false),
            MIDIActionEndpoint midi => ActionEndpointProbe.TryProbeMIDI(midi, EnsureMIDIInitialized),
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
        player.DetachRequested += OnPlayerDetachRequested;
        return player;
    }

    private async Task RemovePlayer(MediaPlayerViewModel player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0) return;
        if (_detachedPlayerWindows.TryGetValue(player, out var detachedWindow))
            detachedWindow.Close();
        player.DetachRequested -= OnPlayerDetachRequested;
        await player.DisposeAsync();
        Players.RemoveAt(idx);
        if (SelectedPlayer == player)
            SelectedPlayer = Players.Count > 0 ? Players[Math.Min(idx, Players.Count - 1)] : null;
    }

    /// <summary>Tracks the floating window per detached player - gates double-detach (second click
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
        // Cancel the predecessor here (never Dispose) - each invocation disposes its own CTS in its
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

                var list = CuePlayer.SelectedCueList;
                if (list is null)
                {
                    timing?.SetOutcome("no-cue-list");
                    return;
                }

                var warmTargets = CuePlayer.GetPreparedMediaCueTargets();
                Trace.LogDebug("RefreshCuePreRollAsync: warmTargets={WarmTargets}", warmTargets.Count);

                // Prepare through the SAME ShowSession graph that fires (flush-then-warm, coordinator-owned).
                await _cueShow.WarmUpcomingForPreRollAsync(Math.Max(1, warmTargets.Count)).ConfigureAwait(false);
                timing?.SetOutcome($"warm={warmTargets.Count}");
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
                /* best effort - pre-roll must not break transport */
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
                CueActionKind.OSCOut => await ExecuteCueOSCAsync(cue, ct),
                CueActionKind.MIDIOut => await Task.Run(() => ExecuteCueMIDI(cue, ct), ct),
                _ => Strings.Format(nameof(Strings.ActionKindNotWiredFormat), cue.ActionKind),
            };
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> ExecuteCueOSCAsync(ActionCueNode cue, CancellationToken ct)
    {
        OSCActionEndpoint? endpoint = null;
        if (cue.EndpointId is { } endpointId)
        {
            endpoint = ActionEndpoints
                .OfType<OSCActionEndpoint>()
                .FirstOrDefault(e => e.Id == endpointId);
            if (endpoint is null)
                return Strings.Format(nameof(Strings.OSCEndpointMissingFormat), endpointId);
        }

        var spec = ParseOSCSpec(cue.AddressOrMessage, endpoint);
        using var client = await OSCClient.CreateAsync(spec.Host, spec.Port, cancellationToken: ct);
        await client.SendMessageAsync(spec.Address, spec.Arguments, ct);
        return Strings.Format(nameof(Strings.OSCSendResultFormat), spec.Host, spec.Port, spec.Address);
    }

    private string ExecuteCueMIDI(ActionCueNode cue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var endpoint = cue.EndpointId is { } endpointId
            ? ActionEndpoints.OfType<MIDIActionEndpoint>().FirstOrDefault(e => e.Id == endpointId)
            : ActionEndpoints.OfType<MIDIActionEndpoint>().FirstOrDefault();
        if (cue.EndpointId is not null && endpoint is null)
            return Strings.Format(nameof(Strings.MIDIEndpointMissingFormat), cue.EndpointId);

        var initErr = EnsureMIDIInitialized();
        if (initErr is not null)
            return initErr;

        var devices = PMUtil.GetOutputDevices();
        if (devices.Count == 0)
            return Strings.MIDINoOutputDevices;

        var device = ResolveMIDIDevice(endpoint, devices);
        if (device is null)
            return endpoint is null
                ? Strings.MIDINoSuitableOutputDevice
                : Strings.Format(nameof(Strings.MIDIEndpointDeviceNotFoundFormat), endpoint.DeviceName ?? endpoint.DeviceId?.ToString() ?? Strings.UnsetLabel);

        var spec = ParseMIDISpec(cue.AddressOrMessage, endpoint, device.Value.Id);
        using var outDevice = new MIDIOutputDevice(spec.DeviceId);
        var openErr = outDevice.Open();
        if (openErr != PmError.NoError)
            return Strings.Format(nameof(Strings.MIDIOpenFailedFormat), PMUtil.GetErrorText(openErr) ?? openErr.ToString());
        var writeErr = outDevice.Write(spec.Message);
        if (writeErr != PmError.NoError)
            return Strings.Format(nameof(Strings.MIDIWriteFailedFormat), PMUtil.GetErrorText(writeErr) ?? writeErr.ToString());
        return Strings.Format(nameof(Strings.MIDISendResultFormat), device.Value.Name ?? Strings.Format(nameof(Strings.DeviceHashIdFormat), device.Value.Id), spec.Description);
    }

    private string? EnsureMIDIInitialized()
    {
        lock (_midiInitSync)
        {
            if (_midiInitialized)
                return null;
            var err = PMUtil.Initialize();
            if (err != PmError.NoError)
                return Strings.Format(nameof(Strings.MIDIInitFailedFormat), PMUtil.GetErrorText(err) ?? err.ToString());
            _midiInitialized = true;
            return null;
        }
    }

    private static PmDeviceEntry? ResolveMIDIDevice(MIDIActionEndpoint? endpoint, IReadOnlyList<PmDeviceEntry> outputs)
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

    private static OSCSpec ParseOSCSpec(string raw, OSCActionEndpoint? endpoint)
    {
        var host = string.IsNullOrWhiteSpace(endpoint?.Host) ? "127.0.0.1" : endpoint.Host;
        var port = endpoint is { Port: > 0 } ? endpoint.Port : 9000;
        var tokens = raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count == 0)
            return new OSCSpec(host, port, "/cue/go", []);

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
            args.Add(ParseOSCArgumentToken(tokens[i]));

        return new OSCSpec(host, port, address, args);
    }

    private static MIDISpec ParseMIDISpec(string raw, MIDIActionEndpoint? endpoint, int fallbackDeviceId)
    {
        var deviceId = endpoint?.DeviceId ?? fallbackDeviceId;
        var parsed = CueMIDIActionMessage.CreateMessage(raw, endpoint?.Channel ?? 0);
        return new MIDISpec(deviceId, parsed.Message, parsed.Description);
    }

    private static OSCArgument ParseOSCArgumentToken(string token)
    {
        if (bool.TryParse(token, out var b))
            return b ? OSCArgument.True() : OSCArgument.False();
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32))
            return OSCArgument.Int32(i32);
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var f32))
            return OSCArgument.Float32(f32);
        return OSCArgument.String(token);
    }

    private sealed record OSCSpec(string Host, int Port, string Address, IReadOnlyList<OSCArgument> Arguments);
    private sealed record MIDISpec(int DeviceId, IMIDIMessage Message, string Description);
    public sealed record MIDIInputOption(int Id, string Name);
    public sealed record MIDIOutputOption(int Id, string Name);

    /// <summary>
    /// Phase A - build a <see cref="HaPlayProject"/> snapshot from the current VM state. Pure projection,
    /// no I/O. Phase B will wire this through a File → Save menu; for now tests and programmatic callers
    /// can round-trip via <see cref="ProjectIO"/>.
    /// </summary>
    /// <summary>Best-effort app-version stamp shared by project snapshots and recovery session metadata.</summary>
    private static string? HaPlayVersionString =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString();

    public HaPlayProject BuildProjectSnapshot() => BuildProjectSnapshot(sections: null);

    /// <summary>
    /// Scoped snapshot (save/load rework 2026-06-10): when <paramref name="sections"/> is non-null,
    /// only the listed <see cref="ProjectSections"/> leaves are filled in and the file records them
    /// in <see cref="HaPlayProject.SavedSections"/> - loading such a file applies only those parts.
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
            HaPlayVersion = HaPlayVersionString,
            SavedSections = sections is null ? null : ProjectSections.Normalize(sections),
            // Per-project session-restore setting travels with the document (not gated by a section).
            AutoSaveEnabled = AutoSaveEnabled,
            Outputs = outputs,
            Players = Has(ProjectSections.Players) ? Players.Select(p => p.BuildPlayerConfigSnapshot()).ToList() : [],
            ActionEndpoints = ActionEndpoints
                .Where(e => e is MIDIActionEndpoint ? Has(ProjectSections.TargetsMIDI) : Has(ProjectSections.TargetsOSC))
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
    /// preview windows) - that's a Phase B concern. Tests use this to verify round-trip projection
    /// without touching real devices.
    /// </remarks>
    public void ApplyProjectSnapshot(HaPlayProject project)
    {
        // Save/load rework 2026-06-10: a partial file (SavedSections != null) applies ONLY its own
        // sections; everything else in the live show is left untouched, so partial project files
        // double as section imports. null = full project, original replace-everything semantics.
        var sections = project.SavedSections;
        // The per-project auto-save flag is document-level, not a section - only a full project load adopts it;
        // a partial section import leaves the current show's setting untouched.
        if (sections is null)
            AutoSaveEnabled = project.AutoSaveEnabled;
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

        var hasMIDITargets = Has(ProjectSections.TargetsMIDI);
        var hasOSCTargets = Has(ProjectSections.TargetsOSC);
        if (hasMIDITargets || hasOSCTargets)
        {
            var keep = ActionEndpoints
                .Where(e => e is MIDIActionEndpoint ? !hasMIDITargets : !hasOSCTargets)
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
            FireAndLog(_cueShow.StopAllVoicesAsync(), "ShowSession.StopAllVoicesAsync project-load");
            Soundboard.ApplySnapshot(project.Soundboards);
        }
        if (Has(ProjectSections.Control))
            Control.LoadConfig(project.ControlSystem);
        RebuildProjectMIDIDeviceRows();

        if (!Has(ProjectSections.Players))
            return;

        // Reconcile players: extend or shrink to match the project's player count, then apply each one.
        while (Players.Count < project.Players.Count)
            Players.Add(new MediaPlayerViewModel(OutputManagement, Strings.Format(nameof(Strings.PlayerNameFormat), _nextPlayerNumber++), RemovePlayer));
        while (Players.Count > project.Players.Count && Players.Count > 1)
        {
            var removed = Players[^1];
            removed.DetachRequested -= OnPlayerDetachRequested;
            Players.RemoveAt(Players.Count - 1);
            FireAndLog(removed.DisposeAsync(), "MediaPlayerViewModel.DisposeAsync project-load-removed-player");
        }

        for (var i = 0; i < project.Players.Count && i < Players.Count; i++)
            Players[i].ApplyPlayerConfigSnapshot(project.Players[i]);

    }

    // ----- Phase B (§7): Project save / load command plumbing --------------------------------

    /// <summary>Path of the project file last saved or opened in this session - drives "Save" vs "Save As".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectTitle))]
    [NotifyPropertyChangedFor(nameof(HasOpenProject))]
    [NotifyPropertyChangedFor(nameof(AutoSaveUnavailable))]
    private string? _currentProjectPath;

    // Project-relative control scripts resolve against the project folder; keep the Control workspace's
    // script root in sync with the open project file.
    partial void OnCurrentProjectPathChanged(string? value)
    {
        Control.SetProjectRoot(string.IsNullOrEmpty(value) ? null : Path.GetDirectoryName(value));
        NotifyDirtyStateChanged();
    }

    /// <summary>Status banner text for the title bar (e.g. "Loaded. Missing outputs: …").</summary>
    [ObservableProperty]
    private string? _projectStatus;

    /// <summary>Per-project session-restore setting (persisted in <see cref="HaPlayProject.AutoSaveEnabled"/>).
    /// When on and the project has a file, the session-recovery loop writes edits through to that file on its
    /// cadence; when off, only a crash-recovery duplicate is kept in the cache. Write-through needs a file, so
    /// the toggle is only actionable once <see cref="HasOpenProject"/> is true.</summary>
    [ObservableProperty]
    private bool _autoSaveEnabled;

    partial void OnAutoSaveEnabledChanged(bool value)
    {
        _ = value;
        NotifyDirtyStateChanged();
    }

    [ObservableProperty]
    private string _autoSaveStatusText = Strings.AutoSaveStatusRecoveryOnly;

    [ObservableProperty]
    private bool _autoSaveStatusIsError;

    public ObservableCollection<string> RecentProjects { get; } = new();
    public bool HasNoRecentProjects => RecentProjects.Count == 0;

    public bool HasOpenProject => !string.IsNullOrEmpty(CurrentProjectPath);

    /// <summary>True when the project has no file yet, so per-project auto-save (write-through) can't apply.
    /// Drives the "save the project once" hint next to the auto-save toggle.</summary>
    public bool AutoSaveUnavailable => !HasOpenProject;

    /// <summary>Content hash of the project as of the last New / Open / Save - the "clean" baseline the
    /// unsaved-changes check compares against. There's no central dirty flag, so this hash IS the flag.</summary>
    private string _savedProjectHash = string.Empty;
    private bool _isRaisingDirtyState;
    private bool _notifiedProjectDirty;

    /// <summary>Records the current project state as the clean baseline (call after New / Open / Save).</summary>
    private void MarkProjectClean()
    {
        _savedProjectHash = ProjectHash.Of(BuildProjectSnapshot());
        NotifyDirtyStateChanged(knownProjectDirty: false);
    }

    private void MarkProjectDirty()
    {
        _savedProjectHash = string.Empty;
        NotifyDirtyStateChanged();
    }

    /// <summary>True when the live project differs from the saved/persisted baseline. During a dirty-state
    /// notification all dependent bindings share the one precomputed value instead of serializing the entire
    /// project independently for IsProjectDirty, HasUnsavedChanges, and ProjectTitle.</summary>
    public bool IsProjectDirty => _isRaisingDirtyState ? _notifiedProjectDirty : EvaluateProjectDirty();

    private bool EvaluateProjectDirty()
    {
        var currentHash = ProjectHash.Of(BuildProjectSnapshot());
        return !string.Equals(_savedProjectHash, currentHash, StringComparison.Ordinal)
               && !_projectPersistence.IsPersisted(CurrentProjectPath, currentHash);
    }

    /// <summary>Whether closing now would lose work: the document is dirty (or control scripts live only in the
    /// scratch cache) AND auto-save isn't going to flush it. When auto-save is on for a project that has a file,
    /// the clean-shutdown flush persists the changes, so no prompt is needed.</summary>
    public bool HasUnsavedChanges =>
        IsProjectDirty || HasUnsavedScratchScripts || Control.IsSelectedScriptDirty;

    private bool HasUnsavedChangesFor(bool projectDirty) =>
        projectDirty || HasUnsavedScratchScripts || Control.IsSelectedScriptDirty;

    /// <summary>Recomputes the project hash once and shares it across all synchronous binding evaluations.
    /// Returns that project-only dirty value so decision points can avoid immediately hashing a second time.</summary>
    private bool NotifyDirtyStateChanged(bool? knownProjectDirty = null)
    {
        var previousRaising = _isRaisingDirtyState;
        var previousValue = _notifiedProjectDirty;
        _notifiedProjectDirty = knownProjectDirty ?? EvaluateProjectDirty();
        _isRaisingDirtyState = true;
        try
        {
            OnPropertyChanged(nameof(IsProjectDirty));
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(ProjectTitle));
            return _notifiedProjectDirty;
        }
        finally
        {
            _isRaisingDirtyState = previousRaising;
            if (previousRaising)
                _notifiedProjectDirty = previousValue;
        }
    }

    public string ProjectTitle =>
        (string.IsNullOrEmpty(CurrentProjectPath)
            ? Strings.ProjectTitleUntitled
            : Strings.Format(nameof(Strings.ProjectTitleFormat), Path.GetFileNameWithoutExtension(CurrentProjectPath)))
        + (IsProjectDirty || Control.IsSelectedScriptDirty ? " *" : string.Empty);

    /// <summary>Default location for project files (§7.3 - ~/Documents/HaPlay Projects/).</summary>
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

    [RelayCommand(CanExecute = nameof(CanTestSelectedOSCEndpoint))]
    private async Task TestSelectedOSCEndpointAsync()
    {
        if (SelectedActionEndpoint is not OSCActionEndpoint)
            return;
        var row = SelectedOSCEndpointRow ?? FindEndpointRow(SelectedActionEndpoint.Id);
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingOSCStatus;
        await ProbeEndpointRowAsync(row, CancellationToken.None);
        EndpointTestStatus = row.Health switch
        {
            ActionEndpointHealthState.Ok => Strings.Format(nameof(Strings.OSCTestOkStatusFormat), row.HealthDetail),
            ActionEndpointHealthState.Failed => Strings.Format(nameof(Strings.OSCTestFailedStatusFormat), row.HealthDetail),
            _ => Strings.OSCTestFinishedStatus,
        };
    }

    private bool CanTestSelectedOSCEndpoint() => SelectedActionEndpoint is OSCActionEndpoint;

    [RelayCommand(CanExecute = nameof(CanTestSelectedMIDIEndpoint))]
    private async Task TestSelectedMIDIEndpointAsync()
    {
        if (SelectedActionEndpoint is not MIDIActionEndpoint)
            return;
        var row = SelectedMIDIEndpointRow ?? FindEndpointRow(SelectedActionEndpoint.Id);
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingMIDIStatus;
        await ProbeEndpointRowAsync(row, CancellationToken.None);
        EndpointTestStatus = row.Health switch
        {
            ActionEndpointHealthState.Ok => Strings.Format(nameof(Strings.MIDITestOkStatusFormat), row.HealthDetail),
            ActionEndpointHealthState.Failed => Strings.Format(nameof(Strings.MIDITestFailedStatusFormat), row.HealthDetail),
            _ => Strings.MIDITestFinishedStatus,
        };
    }

    private bool CanTestSelectedMIDIEndpoint() => IsMIDIAvailable && SelectedActionEndpoint is MIDIActionEndpoint;

    private sealed class ProjectMIDIOutputRowBuilder
    {
        public int? DeviceId { get; set; }

        public string? DeviceName { get; set; }

        public Guid? ControlDeviceId { get; set; }

        public Guid? CueEndpointId { get; set; }

        public ProjectMIDIOutputRowViewModel ToRow() =>
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
        => Path.Combine(HaPlayStoragePaths.LocalAppRoot, "recent-projects.json");

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
    /// InvalidCastException when the menu renders - making the whole File menu fail to open. Accepting
    /// <c>object?</c> and guarding on the actual path avoids the cast and is harmless for the header.</summary>
    [RelayCommand]
    private Task OpenRecentAsync(object? path) =>
        path is string p && !string.IsNullOrWhiteSpace(p)
            ? OpenProjectFromPathAsync(p)
            : Task.CompletedTask;
}
