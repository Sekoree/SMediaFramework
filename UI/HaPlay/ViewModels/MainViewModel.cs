using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Reflection;
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
using OSCLib;
using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private int _nextPlayerNumber = 1;
    private readonly object _midiInitSync = new();
    private readonly Playback.CuePlaybackEngine _cuePlaybackEngine;
    private bool _midiInitialized;
    private CancellationTokenSource? _endpointHealthCts;
    private DispatcherTimer? _endpointHealthTimer;
    private int _endpointHealthRefreshInFlight;

    public MainViewModel()
    {
        OutputManagement = new OutputManagementViewModel();
        CuePlayer = new CuePlayerViewModel();
        CuePlayer.SetAvailableOutputs(OutputManagement.Outputs);
        Players = new ObservableCollection<MediaPlayerViewModel>();
        // First player can't be removed — there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];
        _cuePlaybackEngine = new Playback.CuePlaybackEngine(OutputManagement, CuePlayer);
        _cuePlaybackEngine.NaturalEnd += async (_, _) =>
            await CuePlayer.OnMediaCueNaturallyEndedAsync().ConfigureAwait(false);
        _cuePlaybackEngine.CueStarted += (_, id) => CuePlayer.OnCueStarted(id);
        _cuePlaybackEngine.CueEnded += (_, id) => CuePlayer.OnCueEnded(id);
        _cuePlaybackEngine.CueProgress += (_, p) => CuePlayer.OnCueProgress(p);
        CuePlayer.CancelCueCallback = _cuePlaybackEngine.StopCueAsync;
        CuePlayer.MediaCueExecutor = _cuePlaybackEngine.ExecuteAsync;
        CuePlayer.StopPlaybackCallback = _cuePlaybackEngine.StopAsync;
        CuePlayer.SetPlaybackPausedCallback = _cuePlaybackEngine.SetPausedAsync;
        CuePlayer.ActionCueExecutor = ExecuteCueActionAsync;
        CuePlayer.PreRollRefreshSuggested += (_, _) => _ = RefreshCuePreRollAsync();
        foreach (var player in Players)
            player.NaturalPlaybackEnded += OnPlayerNaturalPlaybackEnded;
        SeedDefaultActionEndpointsIfEmpty();
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        ActionEndpoints.CollectionChanged += OnActionEndpointsCollectionChanged;
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        RefreshMidiDeviceCatalog();
        _ = RefreshAllEndpointHealthAsync();
        // Keep endpoint LEDs current even when devices/network state changes after project load.
        _endpointHealthTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            _ = RefreshAllEndpointHealthAsync();
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
        _playersLayout = _appSettings.PlayersLayout;
        AppearanceController.ApplyTheme(_theme);
        AppearanceController.ApplyDensity(_density);
        if (!Playback.PlaybackVideoPipeline.CliRequestedUyvyPassthrough)
            Playback.PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = _appSettings.PreferLiveUyvyPassthrough;
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == _appSettings.LastSelectedWorkspace)
                            ?? WorkspaceItem.Players;
    }

    // ----- Phase B (§12.1): App-shell sidebar -------------------------------------------------

    private readonly AppSettings _appSettings;

    public IReadOnlyList<WorkspaceItem> Workspaces { get; } =
    [
        WorkspaceItem.Players,
        WorkspaceItem.Cues,
        WorkspaceItem.Outputs,
        WorkspaceItem.OscConnections,
        WorkspaceItem.MidiDevices,
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
    [NotifyPropertyChangedFor(nameof(IsOutputsWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsOscConnectionsWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsMidiDevicesWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsProjectWorkspaceSelected))]
    private WorkspaceItem _selectedWorkspace = WorkspaceItem.Players;

    public bool IsPlayersWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Players;
    public bool IsCuesWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Cues;
    public bool IsOutputsWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Outputs;
    public bool IsOscConnectionsWorkspaceSelected => SelectedWorkspace == WorkspaceItem.OscConnections;
    public bool IsMidiDevicesWorkspaceSelected => SelectedWorkspace == WorkspaceItem.MidiDevices;
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

    public OutputManagementViewModel OutputManagement { get; }
    public CuePlayerViewModel CuePlayer { get; }
    public ObservableCollection<MediaPlayerViewModel> Players { get; }
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    // ----- Phase E (§8.6): Theme & Density -------------------------------------------------------

    public IReadOnlyList<AppThemeMode> ThemeChoices { get; } = Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<AppDensityMode> DensityChoices { get; } = Enum.GetValues<AppDensityMode>();
    public IReadOnlyList<PlayersLayoutMode> PlayersLayoutChoices { get; } = Enum.GetValues<PlayersLayoutMode>();

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

    /// <summary>Phase E (§8.3) — Players-workspace layout mode (Tabs / Stacked / Split). Setting this
    /// flips which visual tree renders inside the Players DockPanel via the boolean derived properties
    /// below, and persists to app-settings.json.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayersTabsLayout))]
    [NotifyPropertyChangedFor(nameof(IsPlayersStackedLayout))]
    [NotifyPropertyChangedFor(nameof(IsPlayersSplitLayout))]
    private PlayersLayoutMode _playersLayout;

    public bool IsPlayersTabsLayout => PlayersLayout == PlayersLayoutMode.Tabs;
    public bool IsPlayersStackedLayout => PlayersLayout == PlayersLayoutMode.Stacked;
    public bool IsPlayersSplitLayout => PlayersLayout == PlayersLayoutMode.Split;

    /// <summary>Phase E (§8.3) — bound by the Tabs-layout ContentControl. Returns the selected player
    /// only when Tabs mode is active so the hidden Tabs branch can't materialize a duplicate
    /// <c>MediaPlayerView</c> for the same VM that the Stacked/Split ItemsControl is already showing.
    /// (<see cref="Avalonia.Controls.Control.IsVisible"/>=false hides rendering but keeps the visual
    /// tree materialized, so without this null-when-not-tabs gate every selected player would have
    /// two attached views in Stacked/Split mode.)</summary>
    public MediaPlayerViewModel? TabsLayoutContent =>
        IsPlayersTabsLayout ? SelectedPlayer : null;

    /// <summary>Same idea for Stacked layout — returns the players collection only when Stacked is active.</summary>
    public ObservableCollection<MediaPlayerViewModel>? StackedLayoutContent =>
        IsPlayersStackedLayout ? Players : null;

    /// <summary>Same idea for Split layout.</summary>
    public ObservableCollection<MediaPlayerViewModel>? SplitLayoutContent =>
        IsPlayersSplitLayout ? Players : null;

    partial void OnPlayersLayoutChanged(PlayersLayoutMode value)
    {
        _appSettings.PlayersLayout = value;
        _appSettings.Save();
        OnPropertyChanged(nameof(TabsLayoutContent));
        OnPropertyChanged(nameof(StackedLayoutContent));
        OnPropertyChanged(nameof(SplitLayoutContent));
    }

    partial void OnSelectedPlayerChanged(MediaPlayerViewModel? value)
    {
        _ = value;
        // Tabs layout pulls from the live SelectedPlayer, so a tab switch must rebroadcast.
        if (IsPlayersTabsLayout)
            OnPropertyChanged(nameof(TabsLayoutContent));
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

    [ObservableProperty]
    private string? _midiDeviceStatus;

    [ObservableProperty]
    private MidiOutputOption? _selectedMidiOutputOption;

    [ObservableProperty]
    private string? _endpointTestStatus;

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
        _ = RefreshAllEndpointHealthAsync();
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
    }

    private ActionEndpointRowViewModel? FindEndpointRow(Guid endpointId) =>
        OscEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId)
        ?? MidiEndpointRows.FirstOrDefault(r => r.Endpoint.Id == endpointId);

    public async Task RefreshAllEndpointHealthAsync()
    {
        if (Interlocked.CompareExchange(ref _endpointHealthRefreshInFlight, 1, 0) != 0)
            return;

        CancellationTokenSource? newCts = null;
        _endpointHealthCts?.Cancel();
        _endpointHealthCts?.Dispose();
        _endpointHealthCts = newCts = new CancellationTokenSource();
        var ct = newCts.Token;

        try
        {
            foreach (var row in OscEndpointRows.Concat(MidiEndpointRows))
            {
                if (ct.IsCancellationRequested)
                    return;
                await ProbeEndpointRowAsync(row, ct).ConfigureAwait(false);
            }
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
    }

    [RelayCommand]
    private void AddOscEndpoint()
    {
        var n = ActionEndpoints.OfType<OscActionEndpoint>().Count() + 1;
        var endpoint = new OscActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.OscEndpointNameFormat), n),
            Host = "127.0.0.1",
            Port = 9000,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    [RelayCommand]
    private void AddMidiEndpoint()
    {
        var n = ActionEndpoints.OfType<MidiActionEndpoint>().Count() + 1;
        var endpoint = new MidiActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.MidiEndpointNameFormat), n),
            Channel = 0,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveActionEndpoint))]
    private void RemoveActionEndpoint()
    {
        if (SelectedActionEndpoint is null)
            return;
        ActionEndpoints.Remove(SelectedActionEndpoint);
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
    }

    private bool CanRemoveActionEndpoint() => SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanSaveActionEndpointEdits))]
    private void SaveActionEndpointEdits()
    {
        if (SelectedActionEndpoint is null)
            return;
        var index = ActionEndpoints.IndexOf(SelectedActionEndpoint);
        if (index < 0)
            return;

        var name = string.IsNullOrWhiteSpace(EndpointEditName)
            ? SelectedActionEndpoint.Name
            : EndpointEditName.Trim();

        var replacement = SelectedActionEndpoint switch
        {
            OscActionEndpoint osc => osc with
            {
                Name = name,
                Host = string.IsNullOrWhiteSpace(OscEditHost) ? osc.Host : OscEditHost.Trim(),
                Port = OscEditPort is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort ? OscEditPort : osc.Port,
            },
            MidiActionEndpoint midi => midi with
            {
                Name = name,
                DeviceId = MidiEditDeviceId,
                DeviceName = string.IsNullOrWhiteSpace(MidiEditDeviceName) ? null : MidiEditDeviceName.Trim(),
                Channel = Math.Clamp(MidiEditChannel, 0, 15),
            },
            _ => SelectedActionEndpoint,
        };

        ActionEndpoints[index] = replacement;
        SelectedActionEndpoint = replacement;
        FindEndpointRow(replacement.Id)?.ReplaceEndpoint(replacement);
        var row = FindEndpointRow(replacement.Id);
        if (row is not null)
            _ = ProbeEndpointRowAsync(row, CancellationToken.None);
    }

    private bool CanSaveActionEndpointEdits() => SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshMidiOutputs))]
    private void RefreshMidiOutputs()
    {
        RefreshMidiDeviceCatalog();
        if (SelectedActionEndpoint is MidiActionEndpoint midi)
            SelectedMidiOutputOption = MidiOutputOptions.FirstOrDefault(o => o.Id == midi.DeviceId);
    }

    private bool CanRefreshMidiOutputs() => SelectedActionEndpoint is MidiActionEndpoint;

    [RelayCommand]
    private void RefreshMidiDeviceCatalog()
    {
        var initError = EnsureMidiInitialized();
        if (initError is not null)
        {
            MidiDeviceStatus = initError;
            return;
        }

        var inputs = PMUtil.GetInputDevices();
        MidiInputOptions.Clear();
        foreach (var dev in inputs)
            MidiInputOptions.Add(new MidiInputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        var outputs = PMUtil.GetOutputDevices();
        MidiOutputOptions.Clear();
        foreach (var dev in outputs)
            MidiOutputOptions.Add(new MidiOutputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        MidiDeviceStatus = Strings.Format(nameof(Strings.MidiDeviceCatalogStatusFormat), inputs.Count, outputs.Count);
    }

    [RelayCommand]
    private void UseSelectedMidiOutput()
    {
        if (SelectedMidiOutputOption is null)
            return;
        MidiEditDeviceId = SelectedMidiOutputOption.Id;
        MidiEditDeviceName = SelectedMidiOutputOption.Name;
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
            CuePlayer.StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowFailedFormat), ex.Message);
        }
    }

    private void RemovePlayer(MediaPlayerViewModel player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0) return;
        Players.RemoveAt(idx);
        if (SelectedPlayer == player)
            SelectedPlayer = Players.Count > 0 ? Players[Math.Min(idx, Players.Count - 1)] : null;
    }

    private void SeedDefaultActionEndpointsIfEmpty()
    {
        if (ActionEndpoints.Count > 0)
            return;
        ActionEndpoints.Add(new OscActionEndpoint
        {
            Name = Strings.OscLocalhostEndpointName,
            Host = "127.0.0.1",
            Port = 9000,
        });
    }

    private async Task RefreshCuePreRollAsync()
    {
        var player = SelectedPlayer;
        var list = CuePlayer.SelectedCueList;
        if (player is null || list is null)
            return;

        var n = list.PreRollCount;
        var fileTargets = CuePlayer.GetPreRollTargets(n);
        var ndiTargets = CuePlayer.GetNdiPreConnectTargets(n);
        var paTargets = CuePlayer.GetPortAudioPreConnectTargets(n);
        try
        {
            await player.RefreshCuePreRollAsync(fileTargets).ConfigureAwait(false);
            await player.RefreshNdiPreConnectAsync(ndiTargets).ConfigureAwait(false);
            await player.RefreshPortAudioPreConnectAsync(paTargets).ConfigureAwait(false);
        }
        catch
        {
            /* best effort — pre-roll must not break transport */
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
        var tokens = raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tokens.Count == 0)
            throw new InvalidOperationException(Strings.MidiActionRequiresMessage);

        byte channel = (byte)Math.Clamp(endpoint?.Channel ?? 0, 0, 15);
        var idx = 0;
        if (tokens[0].StartsWith("ch", StringComparison.OrdinalIgnoreCase))
        {
            var rawCh = tokens[0].AsSpan(tokens[0].Contains('=') ? tokens[0].IndexOf('=') + 1 : 2);
            if (int.TryParse(rawCh, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                channel = (byte)Math.Clamp(parsed - 1, 0, 15);
            idx++;
        }

        if (idx >= tokens.Count)
            throw new InvalidOperationException(Strings.MidiActionCommandMissing);

        var cmd = tokens[idx++].ToLowerInvariant();
        IMIDIMessage msg = cmd switch
        {
            "noteon" => new NoteOn(channel, ParseByte(tokens, idx++, "note"), ParseByte(tokens, idx++, "velocity")),
            "noteoff" => new NoteOff(channel, ParseByte(tokens, idx++, "note"),
                idx < tokens.Count ? ParseByte(tokens, idx++, "velocity") : (byte)0),
            "cc" => new ControlChange(channel, ParseByte(tokens, idx++, "controller"), ParseByte(tokens, idx++, "value")),
            "pc" or "program" => new ProgramChange(channel, ParseByte(tokens, idx++, "program")),
            _ => throw new InvalidOperationException(Strings.Format(nameof(Strings.UnsupportedMidiCommandFormat), cmd)),
        };

        var deviceId = endpoint?.DeviceId ?? fallbackDeviceId;
        return new MidiSpec(deviceId, msg, Strings.Format(nameof(Strings.MidiSpecDescriptionFormat), cmd, channel + 1));
    }

    private static byte ParseByte(IReadOnlyList<string> tokens, int index, string name)
    {
        if (index < 0 || index >= tokens.Count)
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentMissingFormat), name));
        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(Strings.Format(nameof(Strings.MidiArgumentInvalidFormat), name, tokens[index]));
        return (byte)Math.Clamp(value, 0, 127);
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
    public HaPlayProject BuildProjectSnapshot() => new()
    {
        SchemaVersion = HaPlayProject.CurrentSchemaVersion,
        HaPlayVersion = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(),
        Outputs = OutputManagement.Outputs.Select(o => o.Definition).ToList(),
        VirtualAudioChannels = OutputManagement.BuildVirtualAudioChannelAssignmentsSnapshot().ToList(),
        SharedHeadphonesBuses = OutputManagement.BuildSharedHeadphonesBusesSnapshot().ToList(),
        Players = Players.Select(p => p.BuildPlayerConfigSnapshot()).ToList(),
        ActionEndpoints = ActionEndpoints.ToList(),
        CueLists = CuePlayer.BuildCueListsSnapshot(),
    };

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
        // Reconcile outputs: rebuild the list from the project definitions. Phase B will need a richer
        // "rebind missing devices" flow (§7.3, §7.4); for now we just project the definitions.
        OutputManagement.ReplaceDefinitionsForLoad(project.Outputs);
        OutputManagement.ApplyVirtualAudioChannelAssignments(project.VirtualAudioChannels);
        OutputManagement.ApplySharedHeadphonesBuses(project.SharedHeadphonesBuses);
        ActionEndpoints.Clear();
        foreach (var endpoint in project.ActionEndpoints)
            ActionEndpoints.Add(endpoint);
        SeedDefaultActionEndpointsIfEmpty();
        RebuildEndpointWorkspaceLists();
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        CuePlayer.ApplyCueLists(project.CueLists);

        // Reconcile players: extend or shrink to match the project's player count, then apply each one.
        while (Players.Count < project.Players.Count)
            Players.Add(new MediaPlayerViewModel(OutputManagement, Strings.Format(nameof(Strings.PlayerNameFormat), _nextPlayerNumber++), RemovePlayer));
        while (Players.Count > project.Players.Count && Players.Count > 1)
            Players.RemoveAt(Players.Count - 1);

        for (var i = 0; i < project.Players.Count && i < Players.Count; i++)
            Players[i].ApplyPlayerConfigSnapshot(project.Players[i]);

        _ = RefreshCuePreRollAsync();
    }

    // ----- Phase B (§7): Project save / load command plumbing --------------------------------

    /// <summary>Path of the project file last saved or opened in this session — drives "Save" vs "Save As".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectTitle))]
    [NotifyPropertyChangedFor(nameof(HasOpenProject))]
    private string? _currentProjectPath;

    /// <summary>Status banner text for the title bar (e.g. "Loaded. Missing outputs: …").</summary>
    [ObservableProperty]
    private string? _projectStatus;

    public ObservableCollection<string> RecentProjects { get; } = new();

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

    [RelayCommand]
    private void NewProject()
    {
        // Reset to a single empty player + no outputs. Don't prompt for unsaved changes yet — Phase B
        // ships the basic flow; "are you sure?" can land in B.5 with the dialog convention pass.
        ApplyProjectSnapshot(new HaPlayProject());
        CurrentProjectPath = null;
        ProjectStatus = null;
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenProjectDialogTitle,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayProjectFileTypeLabel) { Patterns = ["*." + ProjectIO.FileExtension] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        await OpenProjectFromPathAsync(path);
    }

    public async Task OpenProjectFromPathAsync(string path)
    {
        HaPlayProject project;
        try
        {
            project = await ProjectIO.LoadAsync(path);
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectOpenFailedFormat), ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectOpenFailedFormat), ex.Message);
            return;
        }

        // Capture the existing project's output display names BEFORE we replace them, so we can detect
        // routes that reference outputs the new project doesn't have.
        var requestedRoutes = project.Players
            .SelectMany(p => p.SelectedOutputDisplayNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableNames = project.Outputs
            .Select(o => o.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requestedRoutes.Where(r => !availableNames.Contains(r)).ToList();

        ApplyProjectSnapshot(project);
        _ = RefreshCuePreRollAsync();
        var outputStartErrors = await OutputManagement.StartRuntimesForLoadedDefinitionsAsync();
        CurrentProjectPath = path;
        PushRecentProject(path);

        if (missing.Count > 0)
        {
            var replacementMap = await PromptRebindMissingOutputsAsync(missing);
            if (replacementMap.Count > 0)
            {
                foreach (var player in Players)
                    player.RemapSelectedOutputs(replacementMap);
                missing = missing.Where(m => !replacementMap.ContainsKey(m)).ToList();
            }
        }

        CuePlayer.RefreshBrokenEndpointFlags();
        await PromptRebindMissingActionEndpointsAsync();
        _ = RefreshAllEndpointHealthAsync();

        var statusParts = new List<string> { Strings.Format(nameof(Strings.ProjectLoadedStatusFormat), Path.GetFileName(path)) };
        if (missing.Count > 0)
            statusParts.Add(Strings.Format(nameof(Strings.ProjectMissingRoutesStatusFormat), missing.Count, string.Join(", ", missing)));
        if (outputStartErrors.Count > 0)
            statusParts.Add(Strings.Format(nameof(Strings.ProjectOutputRuntimesStartFailedFormat), outputStartErrors.Count, string.Join("; ", outputStartErrors)));
        ProjectStatus = string.Join(" ", statusParts);
    }

    [RelayCommand]
    private Task SaveProjectAsync() =>
        string.IsNullOrEmpty(CurrentProjectPath) ? SaveProjectAsAsync() : SaveProjectToPathAsync(CurrentProjectPath!);

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveProjectDialogTitle,
            DefaultExtension = ProjectIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(CurrentProjectPath)
                ? Strings.Format(nameof(Strings.ProjectDefaultFileNameFormat), ProjectIO.FileExtension)
                : Path.GetFileName(CurrentProjectPath),
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayProjectFileTypeLabel) { Patterns = ["*." + ProjectIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await SaveProjectToPathAsync(path);
    }

    private async Task SaveProjectToPathAsync(string path)
    {
        try
        {
            var snapshot = BuildProjectSnapshot();
            await ProjectIO.SaveAsync(snapshot, path);
            CurrentProjectPath = path;
            PushRecentProject(path);
            ProjectStatus = Strings.Format(nameof(Strings.ProjectSavedStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat), ex.Message);
        }
    }

    private async Task<IStorageFolder?> TryGetStartLocationAsync(Window owner)
    {
        try
        {
            var folder = DefaultProjectsFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch
        {
            return null;
        }
    }

    private async Task PromptRebindMissingActionEndpointsAsync()
    {
        var groups = CuePlayer.GetBrokenEndpointGroups();
        if (groups.Count == 0)
            return;

        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new Dialogs.RebindMissingActionEndpointsDialogViewModel(groups, ActionEndpoints.ToList());
        if (vm.Rows.Count == 0)
            return;

        var dialog = new Views.Dialogs.RebindMissingActionEndpointsDialog { DataContext = vm };
        var result = await dialog.ShowDialog<IReadOnlyDictionary<Guid, Guid>?>(owner);
        if (result is { Count: > 0 })
            CuePlayer.RemapActionEndpoints(result);
    }

    private async Task<IReadOnlyDictionary<string, string>> PromptRebindMissingOutputsAsync(
        IReadOnlyList<string> missingDisplayNames)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null || missingDisplayNames.Count == 0)
            return new Dictionary<string, string>();

        var available = OutputManagement.Outputs
            .Select(o => o.Definition.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (available.Count == 0)
            return new Dictionary<string, string>();

        var vm = new Dialogs.RebindMissingOutputsDialogViewModel(missingDisplayNames, available);
        var dialog = new Views.Dialogs.RebindMissingOutputsDialog { DataContext = vm };
        var result = await dialog.ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        return result ?? new Dictionary<string, string>();
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

    private bool CanTestSelectedMidiEndpoint() => SelectedActionEndpoint is MidiActionEndpoint;

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
            var list = JsonSerializer.Deserialize<List<string>>(stream);
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
        JsonSerializer.Serialize(stream, RecentProjects.ToList());
    }

    /// <summary>Bound from the recent-projects menu items.</summary>
    [RelayCommand]
    private Task OpenRecentAsync(string path) => OpenProjectFromPathAsync(path);
}
