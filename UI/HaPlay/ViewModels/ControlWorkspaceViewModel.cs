using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.ControlGraph;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

/// <summary>
/// Script-centric control workspace. Owns the live <see cref="ControlSystemRuntimeSession"/> and its
/// <see cref="ControlMonitorBuffer"/>, and surfaces a live monitor view. Arming/disarming is the only
/// thing that touches hardware/sockets, and it is fully guarded so a failure never crashes the app.
/// </summary>
public partial class ControlWorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaxRenderedEntries = 1000;
    private const string AllFilter = "All";
    private const string DefaultX32ProfileId = "behringer.x32.osc";

    private readonly DispatcherTimer _refreshTimer;
    private ControlSystemConfig _config = new();
    private string? _projectRoot;
    private ControlMonitorBuffer? _monitorBuffer;
    private ControlSystemRuntimeSession? _session;
    private UdpControlOscSender? _oscSender;
    private IControlMidiSender? _midiSender;
    private int _lastRenderedCount = -1;
    private bool _filterDirty;
    private bool _busy;
    private DateTimeOffset _learnSinceUtc;

    // Fallback MIDI device resolution — injectable so unit tests can supply a fake catalog/prompt
    // without touching PortMidi or showing a real dialog.
    internal Func<ControlMidiPortCatalog?> MidiCatalogProvider { get; set; } = EnumerateMidiPorts;

    internal Func<IReadOnlyList<ControlMidiResolutionRequest>, Task<IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo>?>> MidiResolutionPrompt { get; set; } = DefaultPromptAsync;

    public ControlWorkspaceViewModel()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshMonitor();
        _refreshTimer.Start();
    }

    public ObservableCollection<ControlMonitorEntryViewModel> MonitorEntries { get; } = new();

    public ObservableCollection<ControlScriptRowViewModel> ScriptRows { get; } = new();

    public ObservableCollection<ControlScriptDiagnosticRowViewModel> ScriptDiagnostics { get; } = new();

    public ObservableCollection<ControlStructureRowViewModel> StructureRows { get; } = new();

    public ObservableCollection<ControlX32CommandRowViewModel> X32CommandRows { get; } = new();

    public ObservableCollection<string> ProfileWarnings { get; } = new();

    public IReadOnlyList<string> MonitorDirectionOptions { get; } =
    [
        AllFilter,
        nameof(ControlMonitorDirection.Input),
        nameof(ControlMonitorDirection.Output),
        nameof(ControlMonitorDirection.Internal),
        nameof(ControlMonitorDirection.Dropped),
        nameof(ControlMonitorDirection.Error),
    ];

    public IReadOnlyList<string> MonitorProtocolOptions { get; } =
    [
        AllFilter,
        nameof(ControlMonitorProtocol.Midi),
        nameof(ControlMonitorProtocol.Osc),
        nameof(ControlMonitorProtocol.Script),
        nameof(ControlMonitorProtocol.Runtime),
        nameof(ControlMonitorProtocol.Cache),
    ];

    public IReadOnlyList<ControlScriptScope> ScriptScopeOptions { get; } =
        Enum.GetValues<ControlScriptScope>();

    public IReadOnlyList<ControlScriptFailureMode> ScriptFailureModeOptions { get; } =
        Enum.GetValues<ControlScriptFailureMode>();

    public bool IsArmed => _session is not null;

    public string ArmButtonText => IsArmed ? "Disarm" : "Arm";

    [ObservableProperty]
    private string _statusMessage = "Disarmed.";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _errorsOnly;

    [ObservableProperty]
    private bool _isLearning;

    [ObservableProperty]
    private ControlLearnCandidateViewModel? _learnCandidate;

    public string LearnButtonText => IsLearning ? "Listening… (cancel)" : "Learn MIDI";

    public bool HasLearnCandidate => LearnCandidate is not null;

    partial void OnIsLearningChanged(bool value) => OnPropertyChanged(nameof(LearnButtonText));

    partial void OnLearnCandidateChanged(ControlLearnCandidateViewModel? value)
    {
        OnPropertyChanged(nameof(HasLearnCandidate));
        ConfirmLearnCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string _selectedMonitorDirection = AllFilter;

    [ObservableProperty]
    private string _selectedMonitorProtocol = AllFilter;

    [ObservableProperty]
    private string _deviceFilterText = string.Empty;

    [ObservableProperty]
    private ControlScriptRowViewModel? _selectedScriptRow;

    [ObservableProperty]
    private string _selectedScriptText = string.Empty;

    [ObservableProperty]
    private string _scriptEditorStatus = string.Empty;

    [ObservableProperty]
    private string _exportedFunctionsSummary = "(no exports)";

    partial void OnFilterTextChanged(string value) => _filterDirty = true;

    partial void OnErrorsOnlyChanged(bool value) => _filterDirty = true;

    partial void OnSelectedMonitorDirectionChanged(string value) => _filterDirty = true;

    partial void OnSelectedMonitorProtocolChanged(string value) => _filterDirty = true;

    partial void OnDeviceFilterTextChanged(string value) => _filterDirty = true;

    partial void OnSelectedScriptRowChanged(ControlScriptRowViewModel? value)
    {
        LoadSelectedScriptText(value);
        SaveSelectedScriptCommand.NotifyCanExecuteChanged();
        RemoveSelectedScriptCommand.NotifyCanExecuteChanged();
        ToggleLearnCommand.NotifyCanExecuteChanged();
        ConfirmLearnCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedScript));
    }

    public bool HasSelectedScript => SelectedScriptRow is not null;

    partial void OnSelectedScriptTextChanged(string value) =>
        RefreshScriptAnalysis(SelectedScriptRow);

    public int DeviceCount => _config.Devices.Count;

    public int ScriptCount => _config.Scripts.Count;

    public int ListenerCount => _config.OscListeners.Count;

    public int LayerCount => _config.Layers.Count;

    [ObservableProperty]
    private string _testOscHost = "192.168.2.76";

    [ObservableProperty]
    private string _testOscPort = "10023";

    [ObservableProperty]
    private string _testOscAddress = "/ch/01/mix/fader";

    [ObservableProperty]
    private string _testOscArgs = "0.5";

    public void LoadConfig(ControlSystemConfig config)
    {
        StopSessionFireAndForget();
        _config = config ?? new ControlSystemConfig();
        MonitorEntries.Clear();
        RebuildScriptRows();
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildX32CommandRows(cache: null);
        _lastRenderedCount = -1;
        StatusMessage = "Disarmed.";
        NotifySummary();
        NotifyArmState();
    }

    /// <summary>Sets the directory that project-relative script files resolve against (the project folder).</summary>
    public void SetProjectRoot(string? projectRoot)
    {
        _projectRoot = projectRoot;
        LoadSelectedScriptText(SelectedScriptRow);
        SaveSelectedScriptCommand.NotifyCanExecuteChanged();
    }

    public ControlSystemConfig BuildSnapshot() => _config with { IsArmed = false };

    public IReadOnlyList<ControlDeviceInstanceConfig> GetMidiInputDevices() =>
        _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi && HasMidiInputBinding(d.Binding))
            .ToList();

    public IReadOnlyList<ControlDeviceInstanceConfig> GetMidiOutputDevices() =>
        _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Midi && HasMidiOutputBinding(d.Binding))
            .ToList();

    public void AddOrUpdateMidiInputDevice(int deviceId, string deviceName) =>
        AddOrUpdateMidiDevice(deviceId, deviceName, isInput: true);

    public void AddOrUpdateMidiOutputDevice(int deviceId, string deviceName) =>
        AddOrUpdateMidiDevice(deviceId, deviceName, isInput: false);

    public bool RemoveMidiInputDevice(Guid deviceInstanceId) =>
        RemoveMidiBinding(deviceInstanceId, isInput: true);

    public bool RemoveMidiOutputDevice(Guid deviceInstanceId) =>
        RemoveMidiBinding(deviceInstanceId, isInput: false);

    private void AddOrUpdateMidiDevice(int deviceId, string deviceName, bool isInput)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = deviceId.ToString(CultureInfo.InvariantCulture);

        var trimmedName = deviceName.Trim();
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d =>
            d.Protocol == ControlDeviceProtocol.Midi
            && (string.Equals(d.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Binding.MidiInputDeviceName, trimmedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Binding.MidiOutputDeviceName, trimmedName, StringComparison.OrdinalIgnoreCase)));

        if (index < 0)
        {
            devices.Add(new ControlDeviceInstanceConfig
            {
                Name = trimmedName,
                ProfileId = "generic-midi",
                Protocol = ControlDeviceProtocol.Midi,
                IsEnabled = true,
                Binding = CreateMidiBinding(deviceId, trimmedName, isInput, existingAliases: devices.Select(d => d.Binding.Alias)),
            });
        }
        else
        {
            var device = devices[index];
            var binding = isInput
                ? device.Binding with { MidiInputDeviceId = deviceId, MidiInputDeviceName = trimmedName }
                : device.Binding with { MidiOutputDeviceId = deviceId, MidiOutputDeviceName = trimmedName };
            devices[index] = device with
            {
                Name = string.IsNullOrWhiteSpace(device.Name) ? trimmedName : device.Name,
                Protocol = ControlDeviceProtocol.Midi,
                IsEnabled = true,
                Binding = binding,
            };
        }

        _config = _config with { Devices = devices };
        if (IsArmed)
            StatusMessage = "MIDI device updated. Re-arm control to apply device changes.";
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
    }

    private bool RemoveMidiBinding(Guid deviceInstanceId, bool isInput)
    {
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d => d.Id == deviceInstanceId && d.Protocol == ControlDeviceProtocol.Midi);
        if (index < 0)
            return false;

        var device = devices[index];
        var binding = isInput
            ? device.Binding with { MidiInputDeviceId = null, MidiInputDeviceName = null }
            : device.Binding with { MidiOutputDeviceId = null, MidiOutputDeviceName = null };

        if (!HasMidiInputBinding(binding) && !HasMidiOutputBinding(binding))
            devices.RemoveAt(index);
        else
            devices[index] = device with { Binding = binding };

        _config = _config with { Devices = devices };
        if (IsArmed)
            StatusMessage = "MIDI device updated. Re-arm control to apply device changes.";
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
        return true;
    }

    private static bool HasMidiInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiInputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MidiInputDeviceName);

    private static bool HasMidiOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiOutputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MidiOutputDeviceName);

    private static ControlDeviceBindingConfig CreateMidiBinding(
        int deviceId,
        string deviceName,
        bool isInput,
        IEnumerable<string?> existingAliases)
    {
        var binding = new ControlDeviceBindingConfig
        {
            Alias = CreateUniqueAlias(deviceName, existingAliases),
        };
        return isInput
            ? binding with { MidiInputDeviceId = deviceId, MidiInputDeviceName = deviceName }
            : binding with { MidiOutputDeviceId = deviceId, MidiOutputDeviceName = deviceName };
    }

    private static string CreateUniqueAlias(string deviceName, IEnumerable<string?> existingAliases)
    {
        var baseAlias = NormalizeAlias(deviceName);
        if (string.IsNullOrWhiteSpace(baseAlias))
            baseAlias = "midi-device";

        var used = existingAliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseAlias))
            return baseAlias;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseAlias}-{i}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    private static string NormalizeAlias(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                builder.Append(char.ToLowerInvariant(c));
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }

    [RelayCommand(CanExecute = nameof(CanSaveSelectedScript))]
    private void SaveSelectedScript()
    {
        if (SelectedScriptRow is null)
            return;

        var path = ResolveScriptPath(SelectedScriptRow.Script.ScriptPath);
        if (path is null)
        {
            ScriptEditorStatus = "Script path is not available for this project.";
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SelectedScriptText);
        ScriptEditorStatus = $"Saved {SelectedScriptRow.Script.ScriptPath}.";
        RefreshScriptAnalysis(SelectedScriptRow);
    }

    private bool CanSaveSelectedScript() =>
        SelectedScriptRow is not null
        && !string.IsNullOrWhiteSpace(SelectedScriptRow.Script.ScriptPath)
        && !string.IsNullOrWhiteSpace(_projectRoot);

    [RelayCommand]
    private void AddScript() => AddScriptInternal(ControlScriptScope.Project, deviceInstanceId: null, layerId: null);

    private void AddScriptInternal(
        ControlScriptScope scope,
        Guid? deviceInstanceId,
        Guid? layerId,
        Guid? endpointInstanceId = null,
        string namePrefix = "Script",
        string scriptPath = "")
    {
        var script = new ControlScriptConfig
        {
            Name = $"{namePrefix} {(_config.Scripts.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            Scope = scope,
            DeviceInstanceId = deviceInstanceId,
            LayerId = layerId,
            EndpointInstanceId = endpointInstanceId,
            ScriptPath = scriptPath,
        };

        _config = _config with { Scripts = [.. _config.Scripts, script] };
        RebuildScriptRows();
        SelectedScriptRow = ScriptRows.FirstOrDefault(row => row.Script.Id == script.Id);
        RebuildStructureRows();
        NotifySummary();
        if (IsArmed)
            StatusMessage = "Script added. Re-arm control to apply script changes.";
    }

    private void AddHelperScript() =>
        AddScriptInternal(ControlScriptScope.Project, deviceInstanceId: null, layerId: null, namePrefix: "Helper", scriptPath: "Scripts/helper.mnd");

    private void AddDeviceScript(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is { } deviceId)
            AddScriptInternal(ControlScriptScope.Device, deviceId, layerId: null);
    }

    private void AddEndpointScript(ControlStructureRowViewModel row)
    {
        if (row.OscListenerId is { } listenerId)
            AddScriptInternal(ControlScriptScope.Endpoint, deviceInstanceId: null, layerId: null, endpointInstanceId: listenerId);
    }

    private void AddLayerScript(ControlStructureRowViewModel row)
    {
        if (row.LayerId is { } layerId)
            AddScriptInternal(ControlScriptScope.Layer, deviceInstanceId: null, layerId);
    }

    // ----- Layer activation -------------------------------------------------------------------
    // Layers are mutually exclusive: activating one deactivates the rest. The config flag drives the
    // structure view; the live session also switches so LayerEnabled/LayerDisabled triggers fire.

    private void ActivateLayer(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        _config = _config with
        {
            Layers = _config.Layers.Select(l => l with { IsEnabled = l.Id == layerId }).ToList(),
        };
        RebuildStructureRows();
        NotifySummary();

        if (_session is not null)
        {
            _ = ActivateLayerLiveAsync(layerId);
        }
        else
        {
            var layer = _config.Layers.FirstOrDefault(l => l.Id == layerId);
            StatusMessage = $"Activated layer '{layer?.Name}'. Arm to run its scripts.";
        }
    }

    private async Task ActivateLayerLiveAsync(Guid layerId)
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            await session.ScriptSession.SetActiveLayerAsync(layerId).ConfigureAwait(true);
            var layer = _config.Layers.FirstOrDefault(l => l.Id == layerId);
            StatusMessage = $"Activated layer '{layer?.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Layer activate error: {ex.Message}";
        }
    }

    // ----- Periodic OSC send add/edit/remove --------------------------------------------------

    internal Func<PeriodicSendDialogViewModel, Task<bool>> PeriodicSendPrompt { get; set; } = DefaultPeriodicSendPromptAsync;

    private async Task AddPeriodicSendAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId)
            return;

        var dialog = new PeriodicSendDialogViewModel("Add periodic OSC send", "/xremote", "/xremote", 8000, isEnabled: true);
        if (!await PeriodicSendPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        if (!TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOscSends =
            [
                .. device.PeriodicOscSends,
                new ControlPeriodicOscSendConfig
                {
                    Name = values.Name,
                    Address = values.Address,
                    IntervalMs = values.IntervalMs,
                    IsEnabled = values.IsEnabled,
                },
            ],
        }))
            return;

        StatusMessage = $"Added periodic send '{values.Name}' every {values.IntervalMs} ms." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditPeriodicSendAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId || row.PeriodicSendId is not { } sendId)
            return;

        var existing = _config.Devices.FirstOrDefault(d => d.Id == deviceId)?
            .PeriodicOscSends.FirstOrDefault(s => s.Id == sendId);
        if (existing is null)
            return;

        var dialog = new PeriodicSendDialogViewModel(
            "Edit periodic OSC send", existing.Name, existing.Address, existing.IntervalMs, existing.IsEnabled);
        if (!await PeriodicSendPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        if (!TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOscSends = device.PeriodicOscSends
                .Select(s => s.Id == sendId
                    ? s with { Name = values.Name, Address = values.Address, IntervalMs = values.IntervalMs, IsEnabled = values.IsEnabled }
                    : s)
                .ToList(),
        }))
            return;

        StatusMessage = $"Updated periodic send '{values.Name}' every {values.IntervalMs} ms." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemovePeriodicSend(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId || row.PeriodicSendId is not { } sendId)
            return;

        if (TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOscSends = device.PeriodicOscSends.Where(s => s.Id != sendId).ToList(),
        }))
        {
            StatusMessage = "Removed periodic send." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }
    }

    private bool TryUpdateDevice(Guid deviceId, Func<ControlDeviceInstanceConfig, ControlDeviceInstanceConfig> update)
    {
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d => d.Id == deviceId);
        if (index < 0)
            return false;

        devices[index] = update(devices[index]);
        _config = _config with { Devices = devices };
        RefreshAfterDeviceChange();
        return true;
    }

    private static async Task<bool> DefaultPeriodicSendPromptAsync(PeriodicSendDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new PeriodicSendDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    private ControlStructureRowCommands BuildStructureRowCommands() => new(
        AddScript,
        AddHelperScript,
        AddDeviceScript,
        AddEndpointScript,
        AddLayerScript,
        ActivateLayer,
        row => _ = AddPeriodicSendAsync(row),
        row => _ = EditPeriodicSendAsync(row),
        RemovePeriodicSend,
        row => _ = EditOscDeviceInternalAsync(FindOscDevice(row)),
        RemoveOscDevice,
        row => _ = TestOscDeviceAsync(row),
        row => _ = TestMidiDeviceAsync(row));

    // ----- OSC device add/edit/remove ---------------------------------------------------------
    // The dialog display is injectable so the add/edit logic is unit-testable without a window.

    internal Func<OscDeviceDialogViewModel, Task<bool>> OscDevicePrompt { get; set; } = DefaultOscDevicePromptAsync;

    [RelayCommand]
    private Task AddOscDeviceAsync() => EditOscDeviceInternalAsync(existing: null);

    private ControlDeviceInstanceConfig? FindOscDevice(ControlStructureRowViewModel row) =>
        row.DeviceInstanceId is { } id
            ? _config.Devices.FirstOrDefault(d => d.Id == id && d.Protocol == ControlDeviceProtocol.Osc)
            : null;

    private async Task EditOscDeviceInternalAsync(ControlDeviceInstanceConfig? existing)
    {
        var isAdd = existing is null;
        var profiles = CompositeControlDeviceProfileRepository.ForProject(_config).Profiles
            .Where(p => p.Protocol == ControlDeviceProtocol.Osc)
            .ToList();
        var defaultProfileId = profiles.Any(p => p.Id == DefaultX32ProfileId)
            ? DefaultX32ProfileId
            : profiles.FirstOrDefault()?.Id;

        var dialog = new OscDeviceDialogViewModel(
            isAdd ? "Add OSC device" : "Edit OSC device",
            name: existing?.Name ?? "X32",
            profileId: existing?.ProfileId is { Length: > 0 } pid ? pid : defaultProfileId,
            host: string.IsNullOrWhiteSpace(existing?.Binding.OscHost) ? "192.168.2.76" : existing!.Binding.OscHost!,
            port: existing?.Binding.OscPort ?? 10023,
            alias: existing?.Binding.Alias ?? (isAdd ? "x32" : null),
            localPort: existing?.Binding.OscLocalPort,
            isEnabled: existing?.IsEnabled ?? true,
            oscProfiles: profiles);

        if (!await OscDevicePrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var devices = _config.Devices.ToList();
        if (existing is null)
        {
            devices.Add(new ControlDeviceInstanceConfig
            {
                Name = values.Name,
                ProfileId = values.ProfileId,
                Protocol = ControlDeviceProtocol.Osc,
                IsEnabled = values.IsEnabled,
                Binding = new ControlDeviceBindingConfig
                {
                    Alias = values.Alias,
                    OscHost = values.Host,
                    OscPort = values.Port,
                    OscLocalPort = values.LocalPort,
                },
            });
            StatusMessage = $"Added OSC device '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }
        else
        {
            var index = devices.FindIndex(d => d.Id == existing.Id);
            if (index < 0)
                return;

            devices[index] = existing with
            {
                Name = values.Name,
                ProfileId = values.ProfileId,
                IsEnabled = values.IsEnabled,
                Binding = existing.Binding with
                {
                    Alias = values.Alias,
                    OscHost = values.Host,
                    OscPort = values.Port,
                    OscLocalPort = values.LocalPort,
                },
            };
            StatusMessage = $"Updated OSC device '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }

        _config = _config with { Devices = devices };
        RefreshAfterDeviceChange();
    }

    private void RemoveOscDevice(ControlStructureRowViewModel row)
    {
        var device = FindOscDevice(row);
        if (device is null)
            return;

        _config = _config with { Devices = _config.Devices.Where(d => d.Id != device.Id).ToList() };
        RefreshAfterDeviceChange();
        StatusMessage = $"Removed OSC device '{device.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RefreshAfterDeviceChange()
    {
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
    }

    private static async Task<bool> DefaultOscDevicePromptAsync(OscDeviceDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new OscDeviceDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedScript))]
    private void RemoveSelectedScript()
    {
        var target = SelectedScriptRow;
        if (target is null)
            return;

        _config = _config with { Scripts = _config.Scripts.Where(s => s.Id != target.Script.Id).ToList() };
        RebuildScriptRows();
        RebuildStructureRows();
        NotifySummary();
        if (IsArmed)
            StatusMessage = "Script removed. Re-arm control to apply script changes.";
    }

    private void RebuildScriptRows()
    {
        var selectedId = SelectedScriptRow?.Script.Id;
        ScriptRows.Clear();
        foreach (var script in _config.Scripts)
            ScriptRows.Add(new ControlScriptRowViewModel(script, OnScriptRowChanged));
        SelectedScriptRow = selectedId is null
            ? ScriptRows.FirstOrDefault()
            : ScriptRows.FirstOrDefault(row => row.Script.Id == selectedId) ?? ScriptRows.FirstOrDefault();
    }

    private void RebuildStructureRows()
    {
        StructureRows.Clear();
        foreach (var row in BuildStructureRows(_config, BuildStructureRowCommands()))
            StructureRows.Add(row);
    }

    private void RebuildProfileWarnings()
    {
        ProfileWarnings.Clear();
        foreach (var warning in BuildProfileWarnings(_config, CompositeControlDeviceProfileRepository.ForProject(_config)))
            ProfileWarnings.Add(warning);
    }

    private void RebuildX32CommandRows(ControlValueCache? cache)
    {
        X32CommandRows.Clear();
        foreach (var row in BuildX32CommandRows(_config, CompositeControlDeviceProfileRepository.ForProject(_config), cache))
            X32CommandRows.Add(row);
    }

    internal static IReadOnlyList<ControlX32CommandRowViewModel> BuildX32CommandRows(
        ControlSystemConfig config,
        IControlDeviceProfileRepository repository,
        ControlValueCache? cache)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);

        var rows = new List<ControlX32CommandRowViewModel>();
        foreach (var device in config.Devices
                     .Where(d => d.Protocol == ControlDeviceProtocol.Osc)
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var profile = repository.FindById(device.ProfileId);
            if (profile is null || profile.Protocol != ControlDeviceProtocol.Osc || profile.Commands.Count == 0)
                continue;

            foreach (var command in profile.Commands.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var cacheText = TryGetCommandCacheText(device, command, cache) ?? "(uncached)";
                rows.Add(new ControlX32CommandRowViewModel(
                    DeviceName: string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                    CommandName: string.IsNullOrWhiteSpace(command.DisplayName) ? command.Id : command.DisplayName,
                    Address: command.Address,
                    ValueKind: command.ValueKind.ToString(),
                    Access: command.Access.ToString(),
                    CacheValue: cacheText));
            }
        }

        return rows;
    }

    internal static IReadOnlyList<string> BuildProfileWarnings(
        ControlSystemConfig config,
        IControlDeviceProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);

        var warnings = new List<string>();
        foreach (var device in config.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed device)" : device.Name;
            if (string.IsNullOrWhiteSpace(device.ProfileId))
            {
                if (device.ProfileMode == ControlDeviceProfileMode.Required)
                    warnings.Add($"{name}: required profile is not set.");
                continue;
            }

            var profile = repository.FindById(device.ProfileId);
            if (profile is null)
            {
                warnings.Add($"{name}: profile '{device.ProfileId}' is not installed; raw {device.Protocol} scripting is still available.");
                continue;
            }

            if (profile.Protocol != device.Protocol)
            {
                warnings.Add($"{name}: profile '{profile.DisplayName}' is {profile.Protocol}, but device is {device.Protocol}.");
            }
        }

        return warnings;
    }

    internal static IReadOnlyList<ControlStructureRowViewModel> BuildStructureRows(
        ControlSystemConfig config,
        ControlStructureRowCommands? commands = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rows = new List<ControlStructureRowViewModel>();

        AddGroup(rows, "MIDI devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.Midi), commands);
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Midi).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "MIDI",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed MIDI)" : device.Name,
                FormatMidiBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1,
                deviceInstanceId: device.Id,
                protocol: ControlDeviceProtocol.Midi,
                commands: commands));
        }

        AddGroup(rows, "OSC listeners", config.OscListeners.Count, commands);
        foreach (var listener in config.OscListeners.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Listen",
                string.IsNullOrWhiteSpace(listener.Name) ? "(unnamed listener)" : listener.Name,
                $"port {listener.LocalPort.ToString(CultureInfo.InvariantCulture)} - {listener.SocketMode}",
                FormatEnabled(listener.IsEnabled),
                Level: 1,
                oscListenerId: listener.Id,
                commands: commands));
        }

        AddGroup(rows, "OSC devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.Osc), commands);
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Osc).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "OSC",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                FormatOscBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1,
                deviceInstanceId: device.Id,
                protocol: ControlDeviceProtocol.Osc,
                commands: commands));
        }

        AddGroup(rows, "Layers", config.Layers.Count, commands);
        foreach (var layer in config.Layers.OrderBy(l => l.Priority).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var layerScriptCount = config.Scripts.Count(s => s.Scope == ControlScriptScope.Layer && s.LayerId == layer.Id);
            rows.Add(new ControlStructureRowViewModel(
                "Layer",
                string.IsNullOrWhiteSpace(layer.Name) ? "(unnamed layer)" : layer.Name,
                $"priority {layer.Priority.ToString(CultureInfo.InvariantCulture)} - {layerScriptCount.ToString(CultureInfo.InvariantCulture)} script(s)",
                layer.IsEnabled ? "active" : "inactive",
                Level: 1,
                layerId: layer.Id,
                commands: commands));
        }

        AddGroup(rows, "Scripts", config.Scripts.Count, commands);
        foreach (var script in config.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Script",
                string.IsNullOrWhiteSpace(script.Name) ? "(unnamed script)" : script.Name,
                $"{script.Scope} - {FormatScriptPath(script.ScriptPath)} - {script.Triggers.Count.ToString(CultureInfo.InvariantCulture)} trigger(s)",
                FormatEnabled(script.IsEnabled),
                Level: 1,
                commands: commands));
        }

        var periodic = config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Osc)
            .SelectMany(d => d.PeriodicOscSends.Select(s => (Device: d, Send: s)))
            .OrderBy(x => x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Send.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddGroup(rows, "Periodic sends", periodic.Count, commands);
        foreach (var item in periodic)
        {
            rows.Add(new ControlStructureRowViewModel(
                "Periodic",
                string.IsNullOrWhiteSpace(item.Send.Name) ? item.Send.Address : item.Send.Name,
                $"{item.Device.Name}: {item.Send.Address} every {item.Send.IntervalMs.ToString(CultureInfo.InvariantCulture)} ms",
                FormatEnabled(item.Send.IsEnabled && item.Device.IsEnabled),
                Level: 1,
                deviceInstanceId: item.Device.Id,
                periodicSendId: item.Send.Id,
                protocol: ControlDeviceProtocol.Osc,
                commands: commands));
        }

        return rows;
    }

    private static string? TryGetCommandCacheText(
        ControlDeviceInstanceConfig device,
        ControlCommandProfile command,
        ControlValueCache? cache)
    {
        if (cache is null || string.IsNullOrWhiteSpace(command.Address))
            return null;

        foreach (var key in GetDeviceCacheKeys(device))
        {
            if (!cache.TryGet(new ControlValueCacheKey(key, command.Address), out var entry) || entry.IsStale)
                continue;

            return FormatCachedValue(entry);
        }

        return null;
    }

    private static IEnumerable<string> GetDeviceCacheKeys(ControlDeviceInstanceConfig device)
    {
        yield return device.Id.ToString();
        if (!string.IsNullOrWhiteSpace(device.Name))
            yield return device.Name;
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            yield return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            yield return device.ProfileId;
    }

    private static string FormatCachedValue(ControlValueCacheEntry entry)
    {
        var value = entry.Value.Kind switch
        {
            ControlCachedValueKind.Number => entry.Value.NumberValue.ToString("0.###", CultureInfo.InvariantCulture),
            ControlCachedValueKind.String => entry.Value.StringValue ?? string.Empty,
            ControlCachedValueKind.Boolean => entry.Value.BooleanValue ? "true" : "false",
            _ => string.Empty,
        };
        return $"{value} ({entry.Source}, {entry.Timestamp.ToLocalTime():HH:mm:ss})";
    }

    private static void AddGroup(
        List<ControlStructureRowViewModel> rows,
        string name,
        int count,
        ControlStructureRowCommands? commands) =>
        rows.Add(new ControlStructureRowViewModel(
            "Group",
            name,
            $"{count.ToString(CultureInfo.InvariantCulture)} configured",
            string.Empty,
            Level: 0,
            IsGroup: true,
            commands: commands));

    private static string FormatMidiBinding(ControlDeviceBindingConfig binding)
    {
        var parts = new List<string>();
        if (binding.MidiInputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MidiInputDeviceName))
            parts.Add($"in: {FormatDeviceBinding(binding.MidiInputDeviceId, binding.MidiInputDeviceName)}");
        if (binding.MidiOutputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MidiOutputDeviceName))
            parts.Add($"out: {FormatDeviceBinding(binding.MidiOutputDeviceId, binding.MidiOutputDeviceName)}");
        return parts.Count == 0 ? "(unbound)" : string.Join(" / ", parts);
    }

    private static string FormatOscBinding(ControlDeviceBindingConfig binding)
    {
        var endpoint = !string.IsNullOrWhiteSpace(binding.OscHost) && binding.OscPort is { } port
            ? $"{binding.OscHost}:{port.ToString(CultureInfo.InvariantCulture)}"
            : "(unbound)";
        return binding.OscListenerId is { } listenerId
            ? $"{endpoint} - listener {listenerId}"
            : endpoint;
    }

    private static string FormatDeviceBinding(int? deviceId, string? deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "(unnamed)" : deviceName.Trim();
        return deviceId is { } id
            ? $"{name} #{id.ToString(CultureInfo.InvariantCulture)}"
            : name;
    }

    private static string FormatScriptPath(string scriptPath) =>
        string.IsNullOrWhiteSpace(scriptPath) ? "(no file)" : scriptPath;

    private static string FormatEnabled(bool isEnabled) =>
        isEnabled ? "enabled" : "disabled";

    private void OnScriptRowChanged(ControlScriptRowViewModel row, ControlScriptConfig script)
    {
        var scripts = _config.Scripts.ToList();
        var index = scripts.FindIndex(s => s.Id == script.Id);
        if (index < 0)
            return;

        scripts[index] = script;
        _config = _config with { Scripts = scripts };
        RebuildStructureRows();

        if (ReferenceEquals(row, SelectedScriptRow))
        {
            SaveSelectedScriptCommand.NotifyCanExecuteChanged();
            if (string.IsNullOrWhiteSpace(script.ScriptPath))
                ScriptEditorStatus = "Script has no file path.";
            else if (!string.IsNullOrWhiteSpace(_projectRoot))
                ScriptEditorStatus = script.ScriptPath;
        }

        if (IsArmed)
            StatusMessage = "Script settings changed. Re-arm control to apply script changes.";
    }

    private void LoadSelectedScriptText(ControlScriptRowViewModel? row)
    {
        if (row is null)
        {
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = "No script selected.";
            ExportedFunctionsSummary = "(no exports)";
            ScriptDiagnostics.Clear();
            return;
        }

        var path = ResolveScriptPath(row.Script.ScriptPath);
        if (path is null)
        {
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = string.IsNullOrWhiteSpace(row.Script.ScriptPath)
                ? "Script has no file path."
                : "Open or save the project before editing project-relative scripts.";
            ExportedFunctionsSummary = "(no exports)";
            ScriptDiagnostics.Clear();
            if (string.IsNullOrWhiteSpace(row.Script.ScriptPath))
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel("Compile", "Script path is required.", isError: true));
            return;
        }

        if (!File.Exists(path))
        {
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = $"New file: {row.Script.ScriptPath}.";
            RefreshScriptAnalysis(row);
            return;
        }

        SelectedScriptText = File.ReadAllText(path);
        ScriptEditorStatus = row.Script.ScriptPath;
        RefreshScriptAnalysis(row);
    }

    private void RefreshScriptAnalysis(ControlScriptRowViewModel? row)
    {
        ScriptDiagnostics.Clear();
        if (row is null || string.IsNullOrWhiteSpace(row.Script.ScriptPath))
        {
            ExportedFunctionsSummary = "(no exports)";
            return;
        }

        try
        {
            var host = new ControlScriptFileHost(new OverlayControlScriptSourceProvider(
                CreateSourceProvider(),
                row.Script.ScriptPath,
                SelectedScriptText));
            var module = host.LoadModule(row.Script.ScriptPath);
            var exports = module.ExportedFunctionNames;
            ExportedFunctionsSummary = exports.Count == 0
                ? "(no exports)"
                : string.Join(", ", exports);
            ValidateTriggerExports(row.Script, exports);
        }
        catch (Exception ex)
        {
            ExportedFunctionsSummary = $"scan failed: {ex.Message}";
            ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel("Compile", ex.Message, isError: true));
        }
    }

    private void ValidateTriggerExports(ControlScriptConfig script, IReadOnlyList<string> exports)
    {
        var exportedFunctions = exports.ToHashSet(StringComparer.Ordinal);
        foreach (var trigger in script.Triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.FunctionName))
            {
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel(
                    "Compile",
                    $"{trigger.Kind} trigger has no function name.",
                    isError: true));
                continue;
            }

            if (!exportedFunctions.Contains(trigger.FunctionName))
            {
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel(
                    "Compile",
                    $"{trigger.Kind} trigger references missing export '{trigger.FunctionName}'.",
                    isError: true));
            }
        }
    }

    private string? ResolveScriptPath(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot) || string.IsNullOrWhiteSpace(scriptPath))
            return null;

        var root = Path.GetFullPath(_projectRoot);
        var path = Path.GetFullPath(Path.Combine(root, scriptPath));
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : path;
    }

    [RelayCommand]
    private async Task ToggleArmAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (IsArmed)
            {
                await DisarmInternalAsync().ConfigureAwait(true);
                StatusMessage = "Disarmed.";
            }
            else
            {
                await ArmInternalAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _busy = false;
            NotifyArmState();
        }
    }

    // ----- Fallback MIDI device resolution ----------------------------------------------------
    // When a configured MIDI device cannot be confidently matched to a current port (ambiguous or
    // missing), let the user pick the live port and persist that choice into the device binding.

    [RelayCommand]
    private async Task ResolveMidiDevicesAsync()
    {
        if (await ResolveMidiDevicesCoreAsync(announceWhenResolvedOrEmpty: true).ConfigureAwait(true))
            StatusMessage = "MIDI device bindings resolved." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    /// <summary>
    /// Enumerates current MIDI ports, prompts the user to resolve any ambiguous/missing bindings, and writes
    /// the chosen ports back into the config. Returns true when at least one binding was updated.
    /// </summary>
    private async Task<bool> ResolveMidiDevicesCoreAsync(bool announceWhenResolvedOrEmpty)
    {
        var catalog = MidiCatalogProvider();
        if (catalog is null)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device catalog is unavailable.";
            return false;
        }

        var requests = ControlMidiDeviceResolver.BuildRequests(_config, catalog.Inputs, catalog.Outputs);
        if (requests.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "All enabled MIDI devices resolve to a current port.";
            return false;
        }

        var selections = await MidiResolutionPrompt(requests).ConfigureAwait(true);
        if (selections is null || selections.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device resolution cancelled.";
            return false;
        }

        _config = ControlMidiDeviceResolver.ApplySelections(_config, selections);
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
        return true;
    }

    private static ControlMidiPortCatalog? EnumerateMidiPorts()
    {
        try
        {
            using var lease = ControlMidiLibraryLease.Acquire();
            var provider = RealControlMidiDeviceProvider.Instance;
            provider.EnsureInitialized();
            return new ControlMidiPortCatalog(provider.GetInputDevices(), provider.GetOutputDevices());
        }
        catch
        {
            // PortMidi unavailable (headless/CI) — skip the fallback flow rather than fault.
            return null;
        }
    }

    private static async Task<IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo>?> DefaultPromptAsync(
        IReadOnlyList<ControlMidiResolutionRequest> requests)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var dialog = new RebindMissingControlMidiDevicesDialog
        {
            DataContext = new RebindMissingControlMidiDevicesDialogViewModel(requests),
        };
        return await dialog.ShowDialog<IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo>?>(owner)
            .ConfigureAwait(true);
    }

    private static Window? TryGetOwnerWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private async Task ArmInternalAsync()
    {
        // Give the user a chance to bind ambiguous/missing MIDI devices to live ports before opening
        // sessions. No-op in tests/headless (no owner window, or no enabled MIDI bindings to resolve).
        await ResolveMidiDevicesCoreAsync(announceWhenResolvedOrEmpty: false).ConfigureAwait(true);

        ControlSystemRuntimeSession? pendingSession = null;
        UdpControlOscSender? pendingOsc = null;
        try
        {
            var armedConfig = _config with { IsArmed = true };
            var monitor = new ControlMonitorBuffer(Math.Max(1, _config.Monitor.MaxVisibleMessages));
            var osc = new UdpControlOscSender(armedConfig);
            var midi = new ControlSystemMidiDeviceSessionManager(armedConfig, monitor);
            var session = new ControlSystemRuntimeSession(
                armedConfig,
                CreateSourceProvider(),
                osc,
                midi,
                monitor: monitor,
                midiSessions: midi);
            pendingSession = session;
            pendingOsc = osc;
            await session.StartAsync().ConfigureAwait(true);

            _monitorBuffer = monitor;
            _oscSender = osc;
            _midiSender = midi;
            _session = session;
            pendingSession = null;
            pendingOsc = null;
            _lastRenderedCount = -1;
            StatusMessage = $"Armed — {ListenerCount} listener(s), {DeviceCount} device(s), {ScriptCount} script(s).";
        }
        catch (Exception ex)
        {
            if (pendingSession is not null)
            {
                try
                {
                    await pendingSession.DisposeAsync().ConfigureAwait(true);
                }
                catch
                {
                    // best effort cleanup after failed arm
                }
            }

            pendingOsc?.Dispose();
            await DisarmInternalAsync().ConfigureAwait(true);
            StatusMessage = $"Failed to arm: {ex.Message}";
        }
    }

    private async Task DisarmInternalAsync()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;

        if (session is not null)
        {
            try
            {
                await session.StopAsync().ConfigureAwait(true);
                await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Disarming must never throw; the session is being torn down regardless.
            }
        }

        osc?.Dispose();
    }

    private void StopSessionFireAndForget()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;
        if (session is null && osc is null)
            return;

        _ = Task.Run(async () =>
        {
            if (session is not null)
            {
                try
                {
                    await session.StopAsync().ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best effort teardown on project switch
                }
            }

            osc?.Dispose();
        });
    }

    private IControlScriptSourceProvider CreateSourceProvider() =>
        !string.IsNullOrWhiteSpace(_projectRoot) && Directory.Exists(_projectRoot)
            ? new FileSystemControlScriptSourceProvider(_projectRoot)
            : new InMemoryControlScriptSourceProvider(new Dictionary<string, string>());

    [RelayCommand]
    private void ClearMonitor()
    {
        _monitorBuffer?.Clear();
        MonitorEntries.Clear();
        _lastRenderedCount = -1;
    }

    [RelayCommand]
    private async Task SendTestOscAsync()
    {
        var osc = _oscSender;
        var monitor = _monitorBuffer;
        if (osc is null || monitor is null)
        {
            StatusMessage = "Arm the control system before sending test OSC.";
            return;
        }

        if (!int.TryParse(TestOscPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            StatusMessage = "Invalid OSC port.";
            return;
        }

        var args = ParseOscArgs(TestOscArgs);
        try
        {
            await osc.SendAsync(TestOscHost, port, TestOscAddress, args).ConfigureAwait(true);
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Osc,
                Result = ControlMonitorResult.Sent,
                RemoteHost = TestOscHost,
                RemotePort = port,
                Endpoint = $"{TestOscHost}:{port}",
                Address = TestOscAddress,
                OscArguments = args.Select(ControlMonitorOscArgumentRecord.FromOscArgument).ToList(),
                Message = "test send",
            });
        }
        catch (Exception ex)
        {
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Osc,
                Result = ControlMonitorResult.Failed,
                RemoteHost = TestOscHost,
                RemotePort = port,
                Address = TestOscAddress,
                Message = "test send",
                ErrorMessage = ex.Message,
            });
        }
    }

    [RelayCommand]
    private async Task RunManualScriptsAsync()
    {
        var session = _session;
        if (session is null)
        {
            StatusMessage = "Arm the control system first.";
            return;
        }

        try
        {
            await session.ScriptSession.DispatchManualAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manual run error: {ex.Message}";
        }
    }

    // ----- Context-menu test sends ------------------------------------------------------------

    /// <summary>Prefills the test-send fields from the OSC device endpoint and sends the current test address.</summary>
    private async Task TestOscDeviceAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId)
            return;

        var device = _config.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
            return;

        if (!string.IsNullOrWhiteSpace(device.Binding.OscHost))
            TestOscHost = device.Binding.OscHost!;
        if (device.Binding.OscPort is { } port)
            TestOscPort = port.ToString(CultureInfo.InvariantCulture);

        await SendTestOscAsync().ConfigureAwait(true);
    }

    /// <summary>Sends a single recognizable test CC to the selected MIDI device's output.</summary>
    private async Task TestMidiDeviceAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId)
            return;

        var sender = _midiSender;
        if (sender is null)
        {
            StatusMessage = "Arm the control system before sending test MIDI.";
            return;
        }

        const int channel = 1;
        const int controller = 0;
        const int value = 127;
        try
        {
            await sender.SendControlChangeAsync(deviceId, channel, controller, value, highResolution14Bit: false)
                .ConfigureAwait(true);
            _monitorBuffer?.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Midi,
                Result = ControlMonitorResult.Sent,
                DeviceInstanceId = deviceId,
                MidiChannel = channel,
                MidiController = controller,
                MidiValue = value,
                Message = "test send",
            });
            StatusMessage = $"Sent test MIDI cc{controller}={value}.";
        }
        catch (Exception ex)
        {
            _monitorBuffer?.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Midi,
                Result = ControlMonitorResult.Failed,
                DeviceInstanceId = deviceId,
                Message = "test send",
                ErrorMessage = ex.Message,
            });
            StatusMessage = $"Test MIDI failed: {ex.Message}";
        }
    }

    // ----- Learn mode -------------------------------------------------------------------------
    // Listen to live MIDI input in the monitor, capture the first control the user moves, and turn
    // it into a pre-filled trigger (plus an optional handler stub) on the selected script. Capturing
    // is host-driven from the monitor poll; the generated trigger is only added once the user confirms.

    [RelayCommand(CanExecute = nameof(CanToggleLearn))]
    private void ToggleLearn()
    {
        if (IsLearning)
        {
            CancelLearn();
            return;
        }

        LearnCandidate = null;
        _learnSinceUtc = DateTimeOffset.UtcNow;
        IsLearning = true;
        StatusMessage = "Learn: move a MIDI control on the device…";
    }

    private bool CanToggleLearn() => IsArmed && HasSelectedScript;

    [RelayCommand]
    private void CancelLearn()
    {
        var wasActive = IsLearning || HasLearnCandidate;
        IsLearning = false;
        LearnCandidate = null;
        if (wasActive)
            StatusMessage = "Learn cancelled.";
    }

    [RelayCommand(CanExecute = nameof(CanConfirmLearn))]
    private void ConfirmLearn()
    {
        var candidate = LearnCandidate;
        var row = SelectedScriptRow;
        if (candidate is null || row is null)
            return;

        var functionName = string.IsNullOrWhiteSpace(candidate.FunctionName)
            ? SuggestLearnFunctionName(candidate.Record)
            : candidate.FunctionName.Trim();

        row.AddLearnedTrigger(BuildLearnedTrigger(candidate.Record, functionName));

        if (candidate.InsertStub && !HasExport(SelectedScriptText, functionName))
            SelectedScriptText += BuildLearnedStub(candidate.Record, functionName);

        LearnCandidate = null;
        StatusMessage = $"Added '{functionName}' trigger. Review and save the script.";
    }

    private bool CanConfirmLearn() => HasLearnCandidate && HasSelectedScript;

    /// <summary>Promotes a captured monitor record into an editable learn candidate. Internal for tests.</summary>
    internal void ApplyLearnCapture(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        IsLearning = false;
        LearnCandidate = new ControlLearnCandidateViewModel(record, SuggestLearnFunctionName(record));
        StatusMessage = $"Learned {LearnCandidate.Description}. Confirm to add a trigger.";
    }

    private void ResetLearn()
    {
        IsLearning = false;
        LearnCandidate = null;
    }

    /// <summary>Finds the first decoded MIDI input (CC or note) captured at or after <paramref name="sinceUtc"/>.</summary>
    internal static ControlMonitorRecord? FindLearnCapture(IEnumerable<ControlMonitorRecord> records, DateTimeOffset sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.FirstOrDefault(r =>
            r.TimestampUtc >= sinceUtc
            && r.Direction == ControlMonitorDirection.Input
            && r.Protocol == ControlMonitorProtocol.Midi
            && (r.MidiController is not null || r.MidiNote is not null));
    }

    internal static string SuggestLearnFunctionName(ControlMonitorRecord record) =>
        record.MidiController is { } controller
            ? $"onCc{controller.ToString(CultureInfo.InvariantCulture)}"
            : $"onNote{(record.MidiNote ?? 0).ToString(CultureInfo.InvariantCulture)}";

    internal static ControlScriptTriggerConfig BuildLearnedTrigger(ControlMonitorRecord record, string functionName)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.MidiController is { } controller
            ? new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MidiControlChange,
                FunctionName = functionName,
                MidiChannel = record.MidiChannel,
                MidiController = controller,
            }
            : new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MidiNote,
                FunctionName = functionName,
                MidiChannel = record.MidiChannel,
                MidiNote = record.MidiNote,
            };
    }

    internal static string BuildLearnedStub(ControlMonitorRecord record, string functionName)
    {
        var description = record.MidiController is { } controller
            ? $"MIDI CC {controller.ToString(CultureInfo.InvariantCulture)} on channel {(record.MidiChannel ?? 0).ToString(CultureInfo.InvariantCulture)}"
            : $"MIDI note {(record.MidiNote ?? 0).ToString(CultureInfo.InvariantCulture)} on channel {(record.MidiChannel ?? 0).ToString(CultureInfo.InvariantCulture)}";
        return $"{Environment.NewLine}export fun {functionName}(event, context) {{{Environment.NewLine}"
            + $"    // TODO: handle {description}{Environment.NewLine}"
            + $"    // event.value holds the incoming value{Environment.NewLine}"
            + $"}}{Environment.NewLine}";
    }

    internal static bool HasExport(string? scriptText, string functionName) =>
        !string.IsNullOrEmpty(scriptText)
        && !string.IsNullOrWhiteSpace(functionName)
        && Regex.IsMatch(scriptText, $@"\bexport\s+fun\s+{Regex.Escape(functionName)}\s*\(");

    private void RefreshMonitor()
    {
        var buffer = _monitorBuffer;
        if (buffer is null || IsPaused)
            return;

        RebuildX32CommandRows(_session?.ScriptSession.OscCache);

        var records = buffer.Records;

        if (IsLearning)
        {
            var capture = FindLearnCapture(records, _learnSinceUtc);
            if (capture is not null)
                ApplyLearnCapture(capture);
        }

        if (records.Count == _lastRenderedCount && !_filterDirty)
            return;

        _lastRenderedCount = records.Count;
        _filterDirty = false;

        var query = ApplyMonitorFilters(
            records,
            new ControlMonitorFilterSettings(
                ErrorsOnly,
                FilterText,
                SelectedMonitorDirection,
                SelectedMonitorProtocol,
                DeviceFilterText));

        var filtered = query.TakeLast(MaxRenderedEntries).Select(r => new ControlMonitorEntryViewModel(r)).ToList();

        MonitorEntries.Clear();
        foreach (var entry in filtered)
            MonitorEntries.Add(entry);
    }

    internal static IEnumerable<ControlMonitorRecord> ApplyMonitorFilters(
        IEnumerable<ControlMonitorRecord> records,
        ControlMonitorFilterSettings filters)
    {
        ArgumentNullException.ThrowIfNull(records);

        var query = records;
        if (filters.ErrorsOnly)
            query = query.Where(r => r.Direction == ControlMonitorDirection.Error || r.Result == ControlMonitorResult.Failed);

        if (Enum.TryParse<ControlMonitorDirection>(filters.Direction, ignoreCase: true, out var direction))
            query = query.Where(r => r.Direction == direction);

        if (Enum.TryParse<ControlMonitorProtocol>(filters.Protocol, ignoreCase: true, out var protocol))
            query = query.Where(r => r.Protocol == protocol);

        if (!string.IsNullOrWhiteSpace(filters.DeviceText))
            query = query.Where(r => MatchesDevice(r, filters.DeviceText));

        if (!string.IsNullOrWhiteSpace(filters.Text))
            query = query.Where(r => MatchesText(r, filters.Text));

        return query;
    }

    private static bool MatchesText(ControlMonitorRecord record, string text) =>
        Contains(record.Address, text)
        || Contains(record.Message, text)
        || Contains(record.ErrorMessage, text)
        || Contains(record.DeviceKey, text)
        || Contains(record.Endpoint, text);

    private static bool MatchesDevice(ControlMonitorRecord record, string text) =>
        Contains(record.DeviceKey, text)
        || Contains(record.ProfileId, text)
        || Contains(record.Endpoint, text)
        || (record.DeviceInstanceId is { } id && id.ToString().Contains(text, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string text) =>
        value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<OSCArgument> ParseOscArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var list = new List<OSCArgument>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (bool.TryParse(token, out var boolean))
                list.Add(boolean ? OSCArgument.True() : OSCArgument.False());
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                list.Add(OSCArgument.Int32(integer));
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                list.Add(OSCArgument.Float32((float)real));
            else
                list.Add(OSCArgument.String(token));
        }

        return list;
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(ScriptCount));
        OnPropertyChanged(nameof(ListenerCount));
        OnPropertyChanged(nameof(LayerCount));
    }

    private void NotifyArmState()
    {
        if (!IsArmed)
            ResetLearn();
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(ArmButtonText));
        ToggleLearnCommand.NotifyCanExecuteChanged();
        ConfirmLearnCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        await DisarmInternalAsync().ConfigureAwait(false);
    }
}

public sealed class ControlMonitorEntryViewModel
{
    public ControlMonitorEntryViewModel(ControlMonitorRecord record)
    {
        Time = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Direction = record.Direction.ToString();
        Protocol = record.Protocol.ToString();
        Result = record.Result.ToString();
        IsError = record.Direction == ControlMonitorDirection.Error || record.Result == ControlMonitorResult.Failed;
        Summary = BuildSummary(record);
    }

    public string Time { get; }

    public string Direction { get; }

    public string Protocol { get; }

    public string Result { get; }

    public string Summary { get; }

    public bool IsError { get; }

    private static string BuildSummary(ControlMonitorRecord record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Address))
            parts.Add(record.Address!);
        if (record.OscArguments.Count > 0)
            parts.Add("[" + string.Join(", ", record.OscArguments.Select(FormatOscArgument)) + "]");
        if (record.MidiController is { } controller)
            parts.Add($"cc{controller}");
        if (record.MidiNote is { } note)
            parts.Add($"note{note}");
        if (record.MidiValue is { } value)
            parts.Add($"={value}");
        if (!string.IsNullOrWhiteSpace(record.DeviceKey))
            parts.Add($"dev:{record.DeviceKey}");
        else if (!string.IsNullOrWhiteSpace(record.Endpoint))
            parts.Add(record.Endpoint!);
        if (!string.IsNullOrWhiteSpace(record.Message))
            parts.Add(record.Message!);
        if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
            parts.Add($"⚠ {record.ErrorMessage}");

        return parts.Count > 0 ? string.Join("  ", parts) : "(no detail)";
    }

    private static string FormatOscArgument(ControlMonitorOscArgumentRecord argument)
    {
        if (argument.FloatValue is { } f)
            return f.ToString("0.###", CultureInfo.InvariantCulture);
        if (argument.IntegerValue is { } i)
            return i.ToString(CultureInfo.InvariantCulture);
        if (argument.BoolValue is { } b)
            return b ? "true" : "false";
        if (argument.StringValue is { } s)
            return $"\"{s}\"";
        return argument.Kind;
    }
}

internal sealed record ControlMonitorFilterSettings(
    bool ErrorsOnly,
    string Text,
    string Direction,
    string Protocol,
    string DeviceText);

/// <summary>Context-menu callbacks shared by every structure row, supplied by the workspace VM.</summary>
internal sealed record ControlStructureRowCommands(
    Action AddProjectScript,
    Action AddHelperFile,
    Action<ControlStructureRowViewModel> AddDeviceScript,
    Action<ControlStructureRowViewModel> AddEndpointScript,
    Action<ControlStructureRowViewModel> AddLayerScript,
    Action<ControlStructureRowViewModel> ActivateLayer,
    Action<ControlStructureRowViewModel> AddPeriodicSend,
    Action<ControlStructureRowViewModel> EditPeriodicSend,
    Action<ControlStructureRowViewModel> RemovePeriodicSend,
    Action<ControlStructureRowViewModel> EditOscDevice,
    Action<ControlStructureRowViewModel> RemoveOscDevice,
    Action<ControlStructureRowViewModel> TestOsc,
    Action<ControlStructureRowViewModel> TestMidi);

public sealed class ControlStructureRowViewModel
{
    internal ControlStructureRowViewModel(
        string kind,
        string name,
        string detail,
        string state,
        int Level,
        bool IsGroup = false,
        Guid? deviceInstanceId = null,
        Guid? layerId = null,
        Guid? oscListenerId = null,
        Guid? periodicSendId = null,
        ControlDeviceProtocol? protocol = null,
        ControlStructureRowCommands? commands = null)
    {
        Kind = kind;
        Name = name;
        Detail = detail;
        State = state;
        this.Level = Level;
        this.IsGroup = IsGroup;
        DeviceInstanceId = deviceInstanceId;
        LayerId = layerId;
        OscListenerId = oscListenerId;
        PeriodicSendId = periodicSendId;
        Protocol = protocol;

        if (commands is not null)
        {
            AddProjectScriptCommand = new RelayCommand(commands.AddProjectScript);
            AddHelperFileCommand = new RelayCommand(commands.AddHelperFile);
            AddDeviceScriptCommand = new RelayCommand(() => commands.AddDeviceScript(this), () => CanAddDeviceScript);
            AddEndpointScriptCommand = new RelayCommand(() => commands.AddEndpointScript(this), () => CanAddEndpointScript);
            AddLayerScriptCommand = new RelayCommand(() => commands.AddLayerScript(this), () => CanAddLayerScript);
            ActivateLayerCommand = new RelayCommand(() => commands.ActivateLayer(this), () => CanActivateLayer);
            AddPeriodicSendCommand = new RelayCommand(() => commands.AddPeriodicSend(this), () => CanAddPeriodicSend);
            EditPeriodicSendCommand = new RelayCommand(() => commands.EditPeriodicSend(this), () => CanEditPeriodicSend);
            RemovePeriodicSendCommand = new RelayCommand(() => commands.RemovePeriodicSend(this), () => CanEditPeriodicSend);
            EditOscDeviceCommand = new RelayCommand(() => commands.EditOscDevice(this), () => CanEditOscDevice);
            RemoveOscDeviceCommand = new RelayCommand(() => commands.RemoveOscDevice(this), () => CanEditOscDevice);
            TestOscCommand = new RelayCommand(() => commands.TestOsc(this), () => CanTestOsc);
            TestMidiCommand = new RelayCommand(() => commands.TestMidi(this), () => CanTestMidi);
        }
    }

    public string Kind { get; }

    public string Name { get; }

    public string Detail { get; }

    public string State { get; }

    public int Level { get; }

    public bool IsGroup { get; }

    public Guid? DeviceInstanceId { get; }

    public Guid? LayerId { get; }

    public Guid? OscListenerId { get; }

    public Guid? PeriodicSendId { get; }

    public ControlDeviceProtocol? Protocol { get; }

    public double IndentWidth => Level * 16;

    public bool CanAddDeviceScript => DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanEditPeriodicSend => PeriodicSendId is not null && DeviceInstanceId is not null;

    public bool CanAddEndpointScript => OscListenerId is not null;

    public bool CanAddLayerScript => LayerId is not null;

    public bool CanActivateLayer => LayerId is not null;

    public bool CanAddPeriodicSend => Protocol == ControlDeviceProtocol.Osc && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanEditOscDevice => Protocol == ControlDeviceProtocol.Osc && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanTestOsc => Protocol == ControlDeviceProtocol.Osc && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanTestMidi => Protocol == ControlDeviceProtocol.Midi && DeviceInstanceId is not null;

    public ICommand? AddProjectScriptCommand { get; }

    public ICommand? AddHelperFileCommand { get; }

    public ICommand? AddDeviceScriptCommand { get; }

    public ICommand? AddEndpointScriptCommand { get; }

    public ICommand? AddLayerScriptCommand { get; }

    public ICommand? ActivateLayerCommand { get; }

    public ICommand? AddPeriodicSendCommand { get; }

    public ICommand? EditPeriodicSendCommand { get; }

    public ICommand? RemovePeriodicSendCommand { get; }

    public ICommand? EditOscDeviceCommand { get; }

    public ICommand? RemoveOscDeviceCommand { get; }

    public ICommand? TestOscCommand { get; }

    public ICommand? TestMidiCommand { get; }
}

public sealed record ControlX32CommandRowViewModel(
    string DeviceName,
    string CommandName,
    string Address,
    string ValueKind,
    string Access,
    string CacheValue);

public sealed partial class ControlScriptRowViewModel : ViewModelBase
{
    private readonly Action<ControlScriptRowViewModel, ControlScriptConfig>? _onChanged;
    private ControlScriptConfig _script;

    public ControlScriptRowViewModel(ControlScriptConfig script, Action<ControlScriptRowViewModel, ControlScriptConfig>? onChanged = null)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _onChanged = onChanged;
        RebuildTriggerRows();
    }

    public ControlScriptConfig Script => _script;

    /// <summary>Editable trigger rows for this script. Edits flow back into <see cref="Script"/> via the row callback.</summary>
    public ObservableCollection<ControlScriptTriggerRowViewModel> Triggers { get; } = new();

    public string Name
    {
        get => _script.Name;
        set
        {
            var next = value ?? string.Empty;
            if (next == _script.Name)
                return;

            UpdateScript(_script with { Name = next }, nameof(Name), nameof(DisplayName));
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_script.Name) ? "(unnamed script)" : _script.Name;

    public bool IsEnabled
    {
        get => _script.IsEnabled;
        set
        {
            if (value == _script.IsEnabled)
                return;

            UpdateScript(_script with { IsEnabled = value }, nameof(IsEnabled));
        }
    }

    public string ScriptPath
    {
        get => _script.ScriptPath;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (next == _script.ScriptPath)
                return;

            UpdateScript(_script with { ScriptPath = next }, nameof(ScriptPath), nameof(DisplayScriptPath));
        }
    }

    public string DisplayScriptPath => string.IsNullOrWhiteSpace(_script.ScriptPath) ? "(no file)" : _script.ScriptPath;

    public ControlScriptScope Scope
    {
        get => _script.Scope;
        set
        {
            if (value == _script.Scope)
                return;

            UpdateScript(_script with { Scope = value }, nameof(Scope), nameof(ScopeText));
        }
    }

    public string ScopeText => _script.Scope.ToString();

    public ControlScriptFailureMode FailureMode
    {
        get => _script.FailurePolicy.Mode;
        set
        {
            if (value == _script.FailurePolicy.Mode)
                return;

            UpdateScript(
                _script with { FailurePolicy = _script.FailurePolicy with { Mode = value } },
                nameof(FailureMode),
                nameof(FailureSummary));
        }
    }

    public int MaxConsecutiveFailures
    {
        get => _script.FailurePolicy.MaxConsecutiveFailures;
        set
        {
            var next = Math.Max(1, value);
            if (next == _script.FailurePolicy.MaxConsecutiveFailures)
                return;

            UpdateScript(
                _script with { FailurePolicy = _script.FailurePolicy with { MaxConsecutiveFailures = next } },
                nameof(MaxConsecutiveFailures),
                nameof(FailureSummary));
        }
    }

    public string FailureSummary =>
        FailureMode == ControlScriptFailureMode.KeepRunning
            ? "Keep running"
            : $"{FailureMode} after {MaxConsecutiveFailures} failure(s)";

    public string TriggerSummary =>
        _script.Triggers.Count == 0
            ? "(no triggers)"
            : string.Join(", ", _script.Triggers.Select(FormatTrigger));

    [RelayCommand]
    private void AddTrigger() =>
        AddLearnedTrigger(new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual });

    /// <summary>Appends a pre-built trigger (e.g. from learn mode) and its editable row.</summary>
    public void AddLearnedTrigger(ControlScriptTriggerConfig trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        UpdateScript(_script with { Triggers = [.. _script.Triggers, trigger] }, nameof(TriggerSummary));
        Triggers.Add(new ControlScriptTriggerRowViewModel(trigger, OnTriggerRowChanged, RemoveTriggerRow));
    }

    private void RemoveTriggerRow(ControlScriptTriggerRowViewModel row)
    {
        UpdateScript(
            _script with { Triggers = _script.Triggers.Where(t => t.Id != row.Trigger.Id).ToList() },
            nameof(TriggerSummary));
        Triggers.Remove(row);
    }

    private void OnTriggerRowChanged(ControlScriptTriggerRowViewModel row, ControlScriptTriggerConfig trigger)
    {
        var triggers = _script.Triggers.ToList();
        var index = triggers.FindIndex(t => t.Id == trigger.Id);
        if (index < 0)
            return;

        triggers[index] = trigger;
        UpdateScript(_script with { Triggers = triggers }, nameof(TriggerSummary));
    }

    private void RebuildTriggerRows()
    {
        Triggers.Clear();
        foreach (var trigger in _script.Triggers)
            Triggers.Add(new ControlScriptTriggerRowViewModel(trigger, OnTriggerRowChanged, RemoveTriggerRow));
    }

    private void UpdateScript(ControlScriptConfig script, params string[] changedProperties)
    {
        _script = script;
        foreach (var property in changedProperties)
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(Script));
        _onChanged?.Invoke(this, _script);
    }

    private static string FormatTrigger(ControlScriptTriggerConfig trigger)
    {
        var label = trigger.Kind.ToString();
        if (!string.IsNullOrWhiteSpace(trigger.FunctionName))
            label += $":{trigger.FunctionName}";
        if (!string.IsNullOrWhiteSpace(trigger.OscAddressPattern))
            label += $" {trigger.OscAddressPattern}";
        if (trigger.MidiController is { } controller)
            label += $" cc{controller}";
        if (trigger.MidiNote is { } note)
            label += $" note{note}";
        return label;
    }
}

/// <summary>
/// Editable view of a single <see cref="ControlScriptTriggerConfig"/>. Optional MIDI/OSC/interval match
/// fields are surfaced as text so an empty field means "match any". Edits produce an updated immutable
/// config and notify the owning <see cref="ControlScriptRowViewModel"/> through the change callback.
/// </summary>
public sealed partial class ControlScriptTriggerRowViewModel : ViewModelBase
{
    private readonly Action<ControlScriptTriggerRowViewModel, ControlScriptTriggerConfig>? _onChanged;
    private readonly Action<ControlScriptTriggerRowViewModel>? _onRemove;
    private ControlScriptTriggerConfig _trigger;

    public ControlScriptTriggerRowViewModel(
        ControlScriptTriggerConfig trigger,
        Action<ControlScriptTriggerRowViewModel, ControlScriptTriggerConfig>? onChanged = null,
        Action<ControlScriptTriggerRowViewModel>? onRemove = null)
    {
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _onChanged = onChanged;
        _onRemove = onRemove;
    }

    public ControlScriptTriggerConfig Trigger => _trigger;

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);

    public IReadOnlyList<ControlScriptTriggerKind> KindOptions { get; } = Enum.GetValues<ControlScriptTriggerKind>();

    public ControlScriptTriggerKind Kind
    {
        get => _trigger.Kind;
        set
        {
            if (value == _trigger.Kind)
                return;

            Update(
                _trigger with { Kind = value },
                nameof(Kind),
                nameof(ShowOscAddress),
                nameof(ShowMidiChannel),
                nameof(ShowMidiController),
                nameof(ShowMidiNote),
                nameof(ShowInterval));
        }
    }

    public string FunctionName
    {
        get => _trigger.FunctionName;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (next == _trigger.FunctionName)
                return;

            Update(_trigger with { FunctionName = next }, nameof(FunctionName));
        }
    }

    public string OscAddressPattern
    {
        get => _trigger.OscAddressPattern ?? string.Empty;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (next == _trigger.OscAddressPattern)
                return;

            Update(_trigger with { OscAddressPattern = next }, nameof(OscAddressPattern));
        }
    }

    public string MidiChannelText
    {
        get => FormatOptionalInt(_trigger.MidiChannel);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiChannel)
                return;

            Update(_trigger with { MidiChannel = next }, nameof(MidiChannelText));
        }
    }

    public string MidiControllerText
    {
        get => FormatOptionalInt(_trigger.MidiController);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiController)
                return;

            Update(_trigger with { MidiController = next }, nameof(MidiControllerText));
        }
    }

    public string MidiNoteText
    {
        get => FormatOptionalInt(_trigger.MidiNote);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiNote)
                return;

            Update(_trigger with { MidiNote = next }, nameof(MidiNoteText));
        }
    }

    public string IntervalMsText
    {
        get => FormatOptionalInt(_trigger.IntervalMs);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.IntervalMs)
                return;

            Update(_trigger with { IntervalMs = next }, nameof(IntervalMsText));
        }
    }

    public bool ShowOscAddress => Kind is ControlScriptTriggerKind.OscMessage or ControlScriptTriggerKind.OscCacheChanged;

    public bool ShowMidiChannel => Kind is ControlScriptTriggerKind.MidiMessage
        or ControlScriptTriggerKind.MidiControlChange
        or ControlScriptTriggerKind.MidiNote;

    public bool ShowMidiController => Kind is ControlScriptTriggerKind.MidiMessage
        or ControlScriptTriggerKind.MidiControlChange;

    public bool ShowMidiNote => Kind is ControlScriptTriggerKind.MidiMessage or ControlScriptTriggerKind.MidiNote;

    public bool ShowInterval => Kind is ControlScriptTriggerKind.Periodic;

    private void Update(ControlScriptTriggerConfig trigger, params string[] changedProperties)
    {
        _trigger = trigger;
        foreach (var property in changedProperties)
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(Trigger));
        _onChanged?.Invoke(this, _trigger);
    }

    private static string FormatOptionalInt(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static int? ParseOptionalInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// A MIDI control captured by learn mode, awaiting confirmation. Holds the source monitor record plus
/// the user-editable handler function name and whether to append a handler stub to the script text.
/// </summary>
public sealed partial class ControlLearnCandidateViewModel : ViewModelBase
{
    public ControlLearnCandidateViewModel(ControlMonitorRecord record, string suggestedFunctionName)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        _functionName = suggestedFunctionName;
        Description = BuildDescription(record);
    }

    public ControlMonitorRecord Record { get; }

    public string Description { get; }

    [ObservableProperty]
    private string _functionName;

    [ObservableProperty]
    private bool _insertStub = true;

    private static string BuildDescription(ControlMonitorRecord record)
    {
        var channel = record.MidiChannel is { } ch ? $" ch {ch.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var value = record.MidiValue is { } v ? $" (value {v.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        if (record.MidiController is { } controller)
            return $"CC {controller.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        if (record.MidiNote is { } note)
            return $"Note {note.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        return "MIDI control";
    }
}

public sealed class ControlScriptDiagnosticRowViewModel
{
    public ControlScriptDiagnosticRowViewModel(string stage, string message, bool isError)
    {
        Stage = stage;
        Message = message;
        IsError = isError;
    }

    public string Stage { get; }

    public string Message { get; }

    public bool IsError { get; }
}

internal sealed class OverlayControlScriptSourceProvider : IControlScriptSourceProvider
{
    private readonly IControlScriptSourceProvider _inner;
    private readonly string _overlayPath;
    private readonly string _overlaySource;

    public OverlayControlScriptSourceProvider(IControlScriptSourceProvider inner, string overlayPath, string overlaySource)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _overlayPath = ControlScriptPath.Normalize(overlayPath);
        _overlaySource = overlaySource ?? string.Empty;
    }

    public bool TryReadScript(string scriptPath, out string source)
    {
        if (string.Equals(ControlScriptPath.Normalize(scriptPath), _overlayPath, StringComparison.OrdinalIgnoreCase))
        {
            source = _overlaySource;
            return true;
        }

        return _inner.TryReadScript(scriptPath, out source);
    }
}
