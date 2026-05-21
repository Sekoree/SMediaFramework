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
    private bool _midiInitialized;

    public MainViewModel()
    {
        OutputManagement = new OutputManagementViewModel();
        CuePlayer = new CuePlayerViewModel();
        Players = new ObservableCollection<MediaPlayerViewModel>();
        // First player can't be removed — there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];
        CuePlayer.MediaCueExecutor = ExecuteCueMediaAsync;
        CuePlayer.ActionCueExecutor = ExecuteCueActionAsync;
        SeedDefaultActionEndpointsIfEmpty();
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        ActionEndpoints.CollectionChanged += OnActionEndpointsCollectionChanged;
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        RefreshMidiDeviceCatalog();

        // Phase B (§3.6) — give the Edit dialog a way to ask "is any player playing through this line?".
        // Iterating the Players collection on each probe is fine: outputs are edited rarely, never
        // during a hot loop, and this is the single source of truth that doesn't require a new event.
        OutputManagement.PlaybackUsageProbe =
            line => Players.Any(p => p.IsActivelyPlayingThroughLine(line));

        LoadRecentProjects();
        _appSettings = AppSettings.Load();
        _sidebarCollapsed = _appSettings.SidebarCollapsed;
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

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void SelectWorkspace(WorkspaceItem workspace) => SelectedWorkspace = workspace;

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

    /// <summary>OSC-only slice of <see cref="ActionEndpoints"/> for the OSC sidebar workspace.</summary>
    public ObservableCollection<OscActionEndpoint> OscEndpoints { get; } = new();

    /// <summary>MIDI-only slice of <see cref="ActionEndpoints"/> for the MIDI sidebar workspace.</summary>
    public ObservableCollection<MidiActionEndpoint> MidiEndpoints { get; } = new();

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
    }

    private void OnActionEndpointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RebuildEndpointWorkspaceLists();
        CuePlayer.SetActionEndpoints(ActionEndpoints);
        RemoveActionEndpointCommand.NotifyCanExecuteChanged();
    }

    private void RebuildEndpointWorkspaceLists()
    {
        OscEndpoints.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<OscActionEndpoint>())
            OscEndpoints.Add(endpoint);

        MidiEndpoints.Clear();
        foreach (var endpoint in ActionEndpoints.OfType<MidiActionEndpoint>())
            MidiEndpoints.Add(endpoint);
    }

    [RelayCommand]
    private void AddOscEndpoint()
    {
        var n = ActionEndpoints.OfType<OscActionEndpoint>().Count() + 1;
        var endpoint = new OscActionEndpoint
        {
            Name = $"OSC {n}",
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
            Name = $"MIDI {n}",
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
            MidiInputOptions.Add(new MidiInputOption(dev.Id, dev.Name ?? $"Device {dev.Id}"));

        var outputs = PMUtil.GetOutputDevices();
        MidiOutputOptions.Clear();
        foreach (var dev in outputs)
            MidiOutputOptions.Add(new MidiOutputOption(dev.Id, dev.Name ?? $"Device {dev.Id}"));

        MidiDeviceStatus = $"MIDI devices: {inputs.Count} input, {outputs.Count} output.";
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
        var name = $"Player {_nextPlayerNumber++}";
        return new MediaPlayerViewModel(OutputManagement, name, removable ? RemovePlayer : null);
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
            Name = "OSC Localhost",
            Host = "127.0.0.1",
            Port = 9000,
        });
    }

    private async Task<string?> ExecuteCueMediaAsync(MediaCueNode cue, CancellationToken ct)
    {
        _ = ct;
        var player = SelectedPlayer;
        if (player is null)
            return "no selected player";
        if (cue.Source is null)
            return "no media source";

        await player.PlayPlaylistItemAsync(cue.Source);
        return cue.Source.DisplayName;
    }

    private async Task<string?> ExecuteCueActionAsync(ActionCueNode cue, CancellationToken ct)
    {
        try
        {
            return cue.ActionKind switch
            {
                CueActionKind.OscOut => await ExecuteCueOscAsync(cue, ct),
                CueActionKind.MidiOut => await Task.Run(() => ExecuteCueMidi(cue, ct), ct),
                _ => $"action kind '{cue.ActionKind}' not wired",
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
                return $"OSC endpoint missing: {endpointId}";
        }

        var spec = ParseOscSpec(cue.AddressOrMessage, endpoint);
        using var client = await OSCClient.CreateAsync(spec.Host, spec.Port, cancellationToken: ct);
        await client.SendMessageAsync(spec.Address, spec.Arguments, ct);
        return $"OSC {spec.Host}:{spec.Port}{spec.Address}";
    }

    private string ExecuteCueMidi(ActionCueNode cue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var endpoint = cue.EndpointId is { } endpointId
            ? ActionEndpoints.OfType<MidiActionEndpoint>().FirstOrDefault(e => e.Id == endpointId)
            : ActionEndpoints.OfType<MidiActionEndpoint>().FirstOrDefault();
        if (cue.EndpointId is not null && endpoint is null)
            return $"MIDI endpoint missing: {cue.EndpointId}";

        var initErr = EnsureMidiInitialized();
        if (initErr is not null)
            return initErr;

        var devices = PMUtil.GetOutputDevices();
        if (devices.Count == 0)
            return "no MIDI output devices";

        var device = ResolveMidiDevice(endpoint, devices);
        if (device is null)
            return endpoint is null
                ? "no suitable MIDI output device"
                : $"MIDI endpoint device not found: {endpoint.DeviceName ?? endpoint.DeviceId?.ToString() ?? "(unset)"}";

        var spec = ParseMidiSpec(cue.AddressOrMessage, endpoint, device.Value.Id);
        using var outDevice = new MIDIOutputDevice(spec.DeviceId);
        var openErr = outDevice.Open();
        if (openErr != PmError.NoError)
            return $"MIDI open failed: {PMUtil.GetErrorText(openErr) ?? openErr.ToString()}";
        var writeErr = outDevice.Write(spec.Message);
        if (writeErr != PmError.NoError)
            return $"MIDI write failed: {PMUtil.GetErrorText(writeErr) ?? writeErr.ToString()}";
        return $"MIDI {device.Value.Name ?? $"#{device.Value.Id}"} {spec.Description}";
    }

    private string? EnsureMidiInitialized()
    {
        lock (_midiInitSync)
        {
            if (_midiInitialized)
                return null;
            var err = PMUtil.Initialize();
            if (err != PmError.NoError)
                return $"MIDI init failed: {PMUtil.GetErrorText(err) ?? err.ToString()}";
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
            throw new InvalidOperationException("MIDI action requires a message (e.g. 'noteon 60 100').");

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
            throw new InvalidOperationException("MIDI action command missing.");

        var cmd = tokens[idx++].ToLowerInvariant();
        IMIDIMessage msg = cmd switch
        {
            "noteon" => new NoteOn(channel, ParseByte(tokens, idx++, "note"), ParseByte(tokens, idx++, "velocity")),
            "noteoff" => new NoteOff(channel, ParseByte(tokens, idx++, "note"),
                idx < tokens.Count ? ParseByte(tokens, idx++, "velocity") : (byte)0),
            "cc" => new ControlChange(channel, ParseByte(tokens, idx++, "controller"), ParseByte(tokens, idx++, "value")),
            "pc" or "program" => new ProgramChange(channel, ParseByte(tokens, idx++, "program")),
            _ => throw new InvalidOperationException($"Unsupported MIDI command '{cmd}'. Use noteon/noteoff/cc/pc."),
        };

        var deviceId = endpoint?.DeviceId ?? fallbackDeviceId;
        return new MidiSpec(deviceId, msg, $"{cmd} ch{channel + 1}");
    }

    private static byte ParseByte(IReadOnlyList<string> tokens, int index, string name)
    {
        if (index < 0 || index >= tokens.Count)
            throw new InvalidOperationException($"MIDI argument '{name}' is missing.");
        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"MIDI argument '{name}' is invalid: '{tokens[index]}'.");
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
        ActionEndpoints.Clear();
        foreach (var endpoint in project.ActionEndpoints)
            ActionEndpoints.Add(endpoint);
        SeedDefaultActionEndpointsIfEmpty();
        RebuildEndpointWorkspaceLists();
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
        CuePlayer.ApplyCueLists(project.CueLists);

        // Reconcile players: extend or shrink to match the project's player count, then apply each one.
        while (Players.Count < project.Players.Count)
            Players.Add(new MediaPlayerViewModel(OutputManagement, $"Player {_nextPlayerNumber++}", RemovePlayer));
        while (Players.Count > project.Players.Count && Players.Count > 1)
            Players.RemoveAt(Players.Count - 1);

        for (var i = 0; i < project.Players.Count && i < Players.Count; i++)
            Players[i].ApplyPlayerConfigSnapshot(project.Players[i]);
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
            ? "HaPlay — Untitled"
            : $"HaPlay — {Path.GetFileNameWithoutExtension(CurrentProjectPath)}";

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
            Title = "Open HaPlay project",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay project") { Patterns = ["*." + ProjectIO.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
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
            ProjectStatus = $"Open failed: {ex.Message}";
            return;
        }
        catch (Exception ex)
        {
            ProjectStatus = $"Open failed: {ex.Message}";
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
        var outputStartErrors = await OutputManagement.StartRuntimesForLoadedDefinitionsAsync();
        CurrentProjectPath = path;
        PushRecentProject(path);

        var statusParts = new List<string> { $"Loaded '{Path.GetFileName(path)}'." };
        if (missing.Count > 0)
            statusParts.Add($"{missing.Count} player route(s) reference missing outputs: {string.Join(", ", missing)}.");
        if (outputStartErrors.Count > 0)
            statusParts.Add($"{outputStartErrors.Count} output runtime(s) could not start: {string.Join("; ", outputStartErrors)}.");
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
            Title = "Save HaPlay project",
            DefaultExtension = ProjectIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(CurrentProjectPath)
                ? "project." + ProjectIO.FileExtension
                : Path.GetFileName(CurrentProjectPath),
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay project") { Patterns = ["*." + ProjectIO.FileExtension] },
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
            ProjectStatus = $"Saved '{Path.GetFileName(path)}'.";
        }
        catch (Exception ex)
        {
            ProjectStatus = $"Save failed: {ex.Message}";
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
