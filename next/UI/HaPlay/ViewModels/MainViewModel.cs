using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    // 8a.4 convergence: when HAPLAY_USE_SHOWSESSION=1, the cue transport re-backs onto this headless session
    // instead of the engine above (off by default → the engine path is untouched). See TryWireShowSessionCueTransport.
    private ShowSession? _cueShowSession;
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
        CuePlayer.CueStandbyInvalidated += (_, cueId) => _cuePlaybackEngine.MarkPreparedCueStale(cueId);
        CuePlayer.RefreshPreviewAudioDevices();
        TryWireShowSessionCueTransport(); // 8a.4: optionally re-back cue transport onto ShowSession (gated, off by default)
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

    /// <summary>8a.4 convergence (gated by <c>HAPLAY_USE_SHOWSESSION=1</c>): re-back the cue workspace's
    /// transport onto the headless <see cref="ShowSession"/> instead of <c>CuePlaybackEngine</c>. Off by
    /// default → the engine wiring above stands untouched. Audio realizes on the default device via the
    /// session's backend; video output realization (NDI/SDL/local via OutputManagement) is a later slice.
    /// Best-effort: any failure logs and leaves the engine active. The show reloads on cue-list change.</summary>
    private void TryWireShowSessionCueTransport()
    {
        if (Environment.GetEnvironmentVariable("HAPLAY_USE_SHOWSESSION") != "1")
            return;

        try
        {
            var backend = MediaRuntime.Registry.AudioBackends.FirstOrDefault();
            _cueShowSession = new ShowSession(
                MediaRuntime.Registry,
                backend,
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex));

            CuePlayer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CuePlayer.SelectedCueList))
                    ReloadCueShowSession();
            };
            ReloadCueShowSession();

            // Override the transport callbacks to drive ShowSession. The VM resolves WHICH cues fire and hands
            // them to the executors, so we fire by id (FireCueAsync) — independent of ShowSession's GO anchor.
            CuePlayer.StopPlaybackCallback = () => _cueShowSession!.StopAsync();
            CuePlayer.SetPlaybackPausedCallback = paused => _cueShowSession!.SetPausedAsync(paused);
            CuePlayer.SeekCueCallback = (_, pos) => _cueShowSession!.SeekAsync(pos);
            CuePlayer.CancelCueCallback = id => _cueShowSession!.StopCueAsync(id.ToString());
            CuePlayer.MediaCueExecutor = async (cue, _) =>
                await _cueShowSession!.FireCueAsync(cue.Id.ToString()).ConfigureAwait(false) == CueExecutionStatus.Fired
                    ? null
                    : $"cue '{cue.Label}' not ready";
            CuePlayer.MediaCueGroupExecutor = async (cues, _) =>
            {
                foreach (var cue in cues)
                    await _cueShowSession!.FireCueAsync(cue.Id.ToString()).ConfigureAwait(false);
                return null;
            };
            Trace.LogInformation("HaPlay: cue transport re-backed onto ShowSession (HAPLAY_USE_SHOWSESSION=1).");
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession cue re-back failed; staying on the engine.");
            _cueShowSession = null;
        }
    }

    /// <summary>Loads the selected cue list into the re-back <see cref="ShowSession"/> (no-op when disabled).</summary>
    private void ReloadCueShowSession()
    {
        if (_cueShowSession is null || CuePlayer.SelectedCueList is not { } list)
            return;
        try
        {
            _cueShowSession.LoadDocument(HaPlayShowMapper.ToShowDocument(list.ToModel()));
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay: ShowSession LoadDocument from cue list failed");
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
                await _cuePlaybackEngine.RefreshPreparedCuesAsync(engineTargets, ct).ConfigureAwait(false);
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
