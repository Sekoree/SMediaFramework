using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Control;
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

    /// <summary>
    /// Ensures the project has been saved to disk (scripts are stored next to the project file, so there's
    /// no project root until then). Returns true once the project has a location. Wired by the main shell;
    /// when unset, script saving simply reports that the project must be saved.
    /// </summary>
    internal Func<Task<bool>>? EnsureProjectSavedAsync { get; set; }

    internal Func<Task<string?>> ProfileImportPathPrompt { get; set; } = DefaultProfileImportPathPromptAsync;

    internal Func<Task<string?>> ProfileExportDirectoryPrompt { get; set; } = DefaultProfileExportDirectoryPromptAsync;

    public ControlWorkspaceViewModel()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshMonitor();
        _refreshTimer.Start();

        // Build the structure for the default (empty) config so the workspace shows its groups — OSC
        // devices, layers, scripts, periodic sends — before any project is loaded or any MIDI/OSC device
        // exists. That lets you prepare an OSC-only or scripts/layers-only setup straight away.
        RebuildScriptRows();
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
    }

    public ObservableCollection<ControlMonitorEntryViewModel> MonitorEntries { get; } = new();

    public ObservableCollection<ControlScriptRowViewModel> ScriptRows { get; } = new();

    public ObservableCollection<ControlScriptDiagnosticRowViewModel> ScriptDiagnostics { get; } = new();

    public ObservableCollection<ControlStructureRowViewModel> StructureRows { get; } = new();

    public ObservableCollection<ControlX32CommandRowViewModel> X32CommandRows { get; } = new();

    public ObservableCollection<ControlProfileRowViewModel> ProfileRows { get; } = new();

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
    private ControlProfileRowViewModel? _selectedProfileRow;

    [ObservableProperty]
    private string _x32CommandFilterText = string.Empty;

    [ObservableProperty]
    private ControlX32CommandRowViewModel? _selectedX32CommandRow;

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

    partial void OnSelectedProfileRowChanged(ControlProfileRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedProfileRow));
        OnPropertyChanged(nameof(CanRemoveSelectedProjectProfile));
        ExportSelectedProfileCommand.NotifyCanExecuteChanged();
        RemoveSelectedProjectProfileCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelectedProfileRow => SelectedProfileRow is not null;

    public bool CanRemoveSelectedProjectProfile => SelectedProfileRow?.IsProjectOverride == true;

    partial void OnX32CommandFilterTextChanged(string value) =>
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);

    partial void OnSelectedX32CommandRowChanged(ControlX32CommandRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedX32CommandRow));
        OnPropertyChanged(nameof(CanRequestSelectedX32Command));
        UseSelectedX32CommandForTestSendCommand.NotifyCanExecuteChanged();
        RequestSelectedX32CommandCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelectedX32CommandRow => SelectedX32CommandRow is not null;

    public bool CanRequestSelectedX32Command => SelectedX32CommandRow?.CanRequest == true;

    partial void OnSelectedScriptTextChanged(string value) =>
        RefreshScriptAnalysis(SelectedScriptRow);

    public int DeviceCount => _config.Devices.Count;

    public int ScriptCount => _config.Scripts.Count;

    public int ListenerCount => _config.OscListeners.Count;

    public int LayerCount => _config.Layers.Count;

    [ObservableProperty]
    private string _testOscHost = string.Empty;

    [ObservableProperty]
    private string _testOscPort = string.Empty;

    [ObservableProperty]
    private string _testOscAddress = "/info";

    [ObservableProperty]
    private string _testOscArgs = string.Empty;

    public void LoadConfig(ControlSystemConfig config)
    {
        StopSessionFireAndForget();
        _config = config ?? new ControlSystemConfig();
        MonitorEntries.Clear();
        RebuildScriptRows();
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
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
        RebuildProfileRows();
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
        RebuildProfileRows();
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
    private async Task SaveSelectedScriptAsync()
    {
        if (SelectedScriptRow is null)
            return;

        // Capture the editor text up front: saving the project sets the project root, which reloads the
        // selected script from disk (empty for a not-yet-written file) and would otherwise clobber what
        // the user just typed before we get a chance to write it.
        var contents = SelectedScriptText;

        // Scripts live next to the project file, so an unsaved project has no place to write them.
        // Offer to save the project first instead of silently failing.
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            var saved = EnsureProjectSavedAsync is not null && await EnsureProjectSavedAsync().ConfigureAwait(true);
            if (!saved || string.IsNullOrWhiteSpace(_projectRoot))
            {
                ScriptEditorStatus = "Save the project first to store scripts.";
                return;
            }
        }

        var path = ResolveScriptPath(SelectedScriptRow.Script.ScriptPath);
        if (path is null)
        {
            ScriptEditorStatus = "Script path is not available for this project.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            SelectedScriptText = contents; // restore, in case ensuring the project root reloaded an empty file
            ScriptEditorStatus = $"Saved {SelectedScriptRow.Script.ScriptPath}.";
            RefreshScriptAnalysis(SelectedScriptRow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScriptEditorStatus = $"Save failed: {ex.Message}";
        }
    }

    private bool CanSaveSelectedScript() =>
        SelectedScriptRow is not null
        && !string.IsNullOrWhiteSpace(SelectedScriptRow.Script.ScriptPath);

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
        var name = $"{namePrefix} {(_config.Scripts.Count + 1).ToString(CultureInfo.InvariantCulture)}";
        var script = new ControlScriptConfig
        {
            Name = name,
            Scope = scope,
            DeviceInstanceId = deviceInstanceId,
            LayerId = layerId,
            EndpointInstanceId = endpointInstanceId,
            // Seed a sensible project-relative path so Save works immediately (no blank-path dead end).
            ScriptPath = string.IsNullOrWhiteSpace(scriptPath) ? GenerateDefaultScriptPath(name) : scriptPath,
        };

        _config = _config with { Scripts = [.. _config.Scripts, script] };
        RebuildScriptRows();
        SelectedScriptRow = ScriptRows.FirstOrDefault(row => row.Script.Id == script.Id);
        RebuildStructureRows();
        NotifySummary();
        if (IsArmed)
            StatusMessage = "Script added. Re-arm control to apply script changes.";
    }

    private string GenerateDefaultScriptPath(string name)
    {
        var slug = NormalizeAlias(name);
        if (string.IsNullOrWhiteSpace(slug))
            slug = "script";

        var used = _config.Scripts
            .Select(s => s.ScriptPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = $"Scripts/{slug}.mnd";
        if (!used.Contains(candidate))
            return candidate;

        for (var i = 2; ; i++)
        {
            var next = $"Scripts/{slug}-{i.ToString(CultureInfo.InvariantCulture)}.mnd";
            if (!used.Contains(next))
                return next;
        }
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
            await session.EventQueue.SetActiveLayerAsync(layerId).ConfigureAwait(true);
            var layer = _config.Layers.FirstOrDefault(l => l.Id == layerId);
            StatusMessage = $"Activated layer '{layer?.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Layer activate error: {ex.Message}";
        }
    }

    // ----- Layer add/edit/remove --------------------------------------------------------------
    // Layers are structural: the live runtime snapshots them at arm time, so add/edit/remove ask for a
    // re-arm. The dialog display is injectable so the logic is unit-testable without a real window.

    internal Func<LayerDialogViewModel, Task<bool>> LayerPrompt { get; set; } = DefaultLayerPromptAsync;

    [RelayCommand]
    private async Task AddLayerAsync()
    {
        var hasActive = _config.Layers.Any(l => l.IsEnabled);
        var nextPriority = _config.Layers.Count == 0 ? 0 : _config.Layers.Max(l => l.Priority) + 1;
        var dialog = new LayerDialogViewModel(
            "Add layer",
            name: $"Layer {(_config.Layers.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            priority: nextPriority,
            isActive: !hasActive);
        if (!await LayerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var layer = new ControlLayerConfig
        {
            Name = values.Name,
            Priority = values.Priority,
            IsEnabled = values.IsActive,
        };

        var layers = _config.Layers.ToList();
        layers.Add(layer);
        if (values.IsActive)
            layers = ApplyExclusiveActive(layers, layer.Id);

        _config = _config with { Layers = layers };
        RefreshAfterLayerChange();
        StatusMessage = $"Added layer '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditLayerAsync(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        var existing = _config.Layers.FirstOrDefault(l => l.Id == layerId);
        if (existing is null)
            return;

        var dialog = new LayerDialogViewModel("Edit layer", existing.Name, existing.Priority, existing.IsEnabled);
        if (!await LayerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var layers = _config.Layers
            .Select(l => l.Id == layerId
                ? l with { Name = values.Name, Priority = values.Priority, IsEnabled = values.IsActive }
                : l)
            .ToList();
        if (values.IsActive)
            layers = ApplyExclusiveActive(layers, layerId);

        _config = _config with { Layers = layers };
        RefreshAfterLayerChange();
        StatusMessage = $"Updated layer '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemoveLayer(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        var existing = _config.Layers.FirstOrDefault(l => l.Id == layerId);
        if (existing is null)
            return;

        var affected = _config.Scripts.Count(s => s.LayerId == layerId);
        _config = _config with { Layers = _config.Layers.Where(l => l.Id != layerId).ToList() };

        // Clear references on layer-scoped scripts so a dangling layer id doesn't silently disable them.
        // Going through the row propagates the change back into the config via the row-changed callback.
        foreach (var scriptRow in ScriptRows.ToList())
            scriptRow.OnLayerRemoved(layerId);

        RefreshAfterLayerChange();
        var suffix = affected > 0
            ? $" {affected.ToString(CultureInfo.InvariantCulture)} script(s) unbound from it."
            : string.Empty;
        StatusMessage = $"Removed layer '{existing.Name}'.{suffix}" + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    // Layers are mutually exclusive: exactly the layer with <paramref name="activeId"/> stays enabled.
    private static List<ControlLayerConfig> ApplyExclusiveActive(IEnumerable<ControlLayerConfig> layers, Guid activeId) =>
        layers.Select(l => l with { IsEnabled = l.Id == activeId }).ToList();

    private void RefreshAfterLayerChange()
    {
        RebuildStructureRows();
        ApplyLayerOptionsToScriptRows();
        NotifySummary();
    }

    private IReadOnlyList<ControlLayerOption> BuildLayerOptions() =>
        _config.Layers
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new ControlLayerOption(l.Id, string.IsNullOrWhiteSpace(l.Name) ? "(unnamed layer)" : l.Name))
            .ToArray();

    private void ApplyLayerOptionsToScriptRows()
    {
        var options = BuildLayerOptions();
        foreach (var row in ScriptRows)
            row.SetLayerOptions(options);
    }

    private static async Task<bool> DefaultLayerPromptAsync(LayerDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new LayerDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    // ----- OSC listener add/edit/remove -------------------------------------------------------
    // App-level inbound OSC ports for external control sources (device replies use the client socket and
    // need none). Structural, so add/edit/remove ask for a re-arm while armed. Display is injectable for tests.

    internal Func<OscListenerDialogViewModel, Task<bool>> OscListenerPrompt { get; set; } = DefaultOscListenerPromptAsync;

    [RelayCommand]
    private async Task AddOscListenerAsync()
    {
        var nextPort = _config.OscListeners.Count == 0 ? 10020 : _config.OscListeners.Max(l => l.LocalPort) + 1;
        var dialog = new OscListenerDialogViewModel(
            "Add OSC listener",
            name: $"OSC Listener {(_config.OscListeners.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            localPort: nextPort,
            isEnabled: true);
        if (!await OscListenerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var listeners = _config.OscListeners.ToList();
        listeners.Add(new ControlOscListenerConfig
        {
            Name = values.Name,
            LocalPort = values.LocalPort,
            IsEnabled = values.IsEnabled,
        });
        _config = _config with { OscListeners = listeners };
        RefreshAfterListenerChange();
        StatusMessage = $"Added OSC listener '{values.Name}' on port {values.LocalPort.ToString(CultureInfo.InvariantCulture)}."
                        + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditOscListenerAsync(ControlStructureRowViewModel row)
    {
        if (row.OscListenerId is not { } listenerId)
            return;

        var existing = _config.OscListeners.FirstOrDefault(l => l.Id == listenerId);
        if (existing is null)
            return;

        var dialog = new OscListenerDialogViewModel("Edit OSC listener", existing.Name, existing.LocalPort, existing.IsEnabled);
        if (!await OscListenerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        _config = _config with
        {
            OscListeners = _config.OscListeners
                .Select(l => l.Id == listenerId
                    ? l with { Name = values.Name, LocalPort = values.LocalPort, IsEnabled = values.IsEnabled }
                    : l)
                .ToList(),
        };
        RefreshAfterListenerChange();
        StatusMessage = $"Updated OSC listener '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemoveOscListener(ControlStructureRowViewModel row)
    {
        if (row.OscListenerId is not { } listenerId)
            return;

        var existing = _config.OscListeners.FirstOrDefault(l => l.Id == listenerId);
        if (existing is null)
            return;

        // A dangling endpoint id simply makes any endpoint-scoped script inert (no event will match it),
        // so there's nothing unsafe to clean up here — just drop the listener.
        _config = _config with { OscListeners = _config.OscListeners.Where(l => l.Id != listenerId).ToList() };
        RefreshAfterListenerChange();
        StatusMessage = $"Removed OSC listener '{existing.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RefreshAfterListenerChange()
    {
        RebuildStructureRows();
        RebuildProfileWarnings();
        NotifySummary();
    }

    private static async Task<bool> DefaultOscListenerPromptAsync(OscListenerDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new OscListenerDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
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
        () => _ = AddLayerAsync(),
        row => _ = EditLayerAsync(row),
        RemoveLayer,
        row => _ = AddPeriodicSendAsync(row),
        row => _ = EditPeriodicSendAsync(row),
        RemovePeriodicSend,
        row => _ = EditOscDeviceInternalAsync(FindOscDevice(row)),
        RemoveOscDevice,
        row => _ = TestOscDeviceAsync(row),
        row => _ = TestMidiDeviceAsync(row),
        () => _ = AddOscListenerAsync(),
        row => _ = EditOscListenerAsync(row),
        RemoveOscListener);

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
        var defaultProfile = profiles.FirstOrDefault(p => p.Id == DefaultX32ProfileId) ?? profiles.FirstOrDefault();
        var defaultProfileId = defaultProfile?.Id;

        var dialog = new OscDeviceDialogViewModel(
            isAdd ? "Add OSC device" : "Edit OSC device",
            name: existing?.Name ?? "X32",
            profileId: existing?.ProfileId is { Length: > 0 } pid ? pid : defaultProfileId,
            host: string.IsNullOrWhiteSpace(existing?.Binding.OscHost) ? "192.168.2.76" : existing!.Binding.OscHost!,
            port: existing?.Binding.OscPort ?? defaultProfile?.DefaultOscPort ?? 10023,
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
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var path = await ProfileImportPathPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var profile = ImportProfileFromFile(path);
            StatusMessage = $"Imported project profile '{FormatProfileName(profile)}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile import failed: {ex.Message}";
        }
    }

    internal ControlDeviceProfile ImportProfileFromFile(string path)
    {
        var profile = DirectoryControlDeviceProfileRepository.LoadProfileFile(path);
        UpsertProjectProfile(profile);
        return profile;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfileRow))]
    private async Task ExportSelectedProfileAsync()
    {
        var row = SelectedProfileRow;
        if (row is null)
            return;

        var directory = await ProfileExportDirectoryPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            var path = ExportProfileToDirectory(row, directory);
            StatusMessage = $"Exported profile '{row.DisplayName}' to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile export failed: {ex.Message}";
        }
    }

    internal string ExportProfileToDirectory(ControlProfileRowViewModel row, string directory)
    {
        ArgumentNullException.ThrowIfNull(row);

        var repository = CompositeControlDeviceProfileRepository.ForProject(_config);
        var profile = repository.FindById(row.Id)
            ?? throw new InvalidOperationException($"Profile '{row.Id}' is not available.");
        return DirectoryControlDeviceProfileRepository.SaveProfile(directory, profile);
    }

    [RelayCommand]
    private async Task ExportBuiltInProfilesAsync()
    {
        var directory = await ProfileExportDirectoryPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            var paths = DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles(directory);
            StatusMessage = $"Exported {paths.Count.ToString(CultureInfo.InvariantCulture)} built-in profile(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectProfile))]
    private void RemoveSelectedProjectProfile()
    {
        var row = SelectedProfileRow;
        if (row is not { IsProjectOverride: true })
            return;

        _config = _config with
        {
            DeviceProfileOverrides = _config.DeviceProfileOverrides
                .Where(profile => !string.Equals(profile.Id, row.Id, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        StatusMessage = $"Removed project profile '{row.DisplayName}'.";
    }

    private void UpsertProjectProfile(ControlDeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var overrides = _config.DeviceProfileOverrides
            .Where(existing => !string.Equals(existing.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        overrides.Add(profile);
        _config = _config with { DeviceProfileOverrides = overrides };
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        SelectedProfileRow = ProfileRows.FirstOrDefault(row =>
            string.Equals(row.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatProfileName(ControlDeviceProfile profile) =>
        string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;

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
        ApplyLayerOptionsToScriptRows();
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

    private void RebuildProfileRows()
    {
        var selectedId = SelectedProfileRow?.Id;
        ProfileRows.Clear();
        foreach (var row in BuildProfileRows(_config))
            ProfileRows.Add(row);
        SelectedProfileRow = !string.IsNullOrWhiteSpace(selectedId)
            ? ProfileRows.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            : SelectedProfileRow;
    }

    private void RebuildX32CommandRows(ControlValueCache? cache)
    {
        var selected = SelectedX32CommandRow;
        X32CommandRows.Clear();
        foreach (var row in BuildX32CommandRows(_config, CompositeControlDeviceProfileRepository.ForProject(_config), cache, X32CommandFilterText))
            X32CommandRows.Add(row);
        SelectedX32CommandRow = selected is not null
            ? X32CommandRows.FirstOrDefault(row =>
                row.DeviceInstanceId == selected.DeviceInstanceId
                && string.Equals(row.Address, selected.Address, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    internal static IReadOnlyList<ControlX32CommandRowViewModel> BuildX32CommandRows(
        ControlSystemConfig config,
        IControlDeviceProfileRepository repository,
        ControlValueCache? cache,
        string? filterText = null)
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

            foreach (var commandInfo in profile.Commands
                         .Select(command => new { Command = command, Group = GetX32CommandGroup(command) })
                         .OrderBy(info => info.Group, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(info => info.Command.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var command = commandInfo.Command;
                var cacheText = TryGetCommandCacheText(device, command, cache) ?? "(uncached)";
                var row = new ControlX32CommandRowViewModel(
                    DeviceInstanceId: device.Id,
                    DeviceName: string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                    DeviceKey: GetPreferredDeviceKey(device),
                    Host: device.Binding.OscHost?.Trim() ?? string.Empty,
                    Port: device.Binding.OscPort,
                    Group: commandInfo.Group,
                    CommandName: string.IsNullOrWhiteSpace(command.DisplayName) ? command.Id : command.DisplayName,
                    Address: command.Address,
                    ValueKind: command.ValueKind.ToString(),
                    Access: command.Access.ToString(),
                    CacheValue: cacheText,
                    CanRequest: command.Access != ControlCommandAccess.WriteOnly
                                && !string.IsNullOrWhiteSpace(device.Binding.OscHost)
                                && device.Binding.OscPort is > 0);
                if (MatchesX32CommandFilter(row, filterText))
                    rows.Add(row);
            }
        }

        return rows;
    }

    private static bool MatchesX32CommandFilter(ControlX32CommandRowViewModel row, string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
            return true;

        var terms = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Contains(row.DeviceName, term)
            || Contains(row.DeviceKey, term)
            || Contains(row.Group, term)
            || Contains(row.CommandName, term)
            || Contains(row.Address, term)
            || Contains(row.ValueKind, term)
            || Contains(row.Access, term)
            || Contains(row.CacheValue, term));
    }

    private static string GetX32CommandGroup(ControlCommandProfile command)
    {
        if (!string.IsNullOrWhiteSpace(command.Id))
        {
            var idParts = command.Id.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (idParts.Length >= 3 && (string.Equals(idParts[0], "x32", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(idParts[0], "xair", StringComparison.OrdinalIgnoreCase)))
                return FormatX32CommandGroup(idParts[1], idParts[2]);
            if (idParts.Length >= 2)
                return FormatX32CommandGroup(idParts[0], idParts[1]);
            if (idParts.Length == 1)
                return ToDisplayGroup(idParts[0]);
        }

        var address = command.Address.Trim();
        var parts = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return FormatX32CommandGroup(parts[0], number.ToString(CultureInfo.InvariantCulture));

        if (parts.Length > 0)
            return ToDisplayGroup(parts[0]);

        return "Other";
    }

    private static string FormatX32CommandGroup(string group, string? numberText)
    {
        if (int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return group.ToLowerInvariant() switch
            {
                "ch" or "channel" => $"Channel {number:00}",
                "bus" => $"Bus {number:00}",
                "mtx" or "matrix" => $"Matrix {number:00}",
                "dca" => $"DCA {number}",
                "auxin" => $"Aux In {number:00}",
                _ => ToDisplayGroup(group),
            };
        }

        return ToDisplayGroup(group);
    }

    private static string ToDisplayGroup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Other";

        return value.Trim().ToLowerInvariant() switch
        {
            "ch" or "channel" => "Channels",
            "bus" => "Buses",
            "mtx" or "matrix" => "Matrix",
            "dca" => "DCA",
            "main" or "lr" => "Main",
            "auxin" => "Aux In",
            "config" => "Config",
            "meters" or "meter" => "Meters",
            var text => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.Replace('-', ' ').Replace('_', ' ')),
        };
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

        var listenerPorts = config.OscListeners
            .Where(l => l.IsEnabled)
            .Select(l => l.LocalPort)
            .ToHashSet();
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Osc && d.Binding.OscLocalPort is > 0))
        {
            var localPort = device.Binding.OscLocalPort!.Value;
            if (!listenerPorts.Contains(localPort))
                continue;

            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name;
            warnings.Add($"{name}: client source port {localPort} matches an enabled OSC listener; use blank/automatic or another port.");
        }

        return warnings;
    }

    internal static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRows(
        IControlDeviceProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return BuildProfileRowsFromProfiles(repository.Profiles.Select(profile =>
            new ProfileSource(profile, "Installed", IsProjectOverride: false)));
    }

    internal static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRows(
        ControlSystemConfig config,
        IControlDeviceProfileRepository? appRepository = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var profiles = new Dictionary<string, ProfileSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BuiltInControlDeviceProfileRepository.Instance.Profiles)
            AddProfileSource(profiles, profile, "Built-in", isProjectOverride: false);
        if (appRepository is not null)
        {
            foreach (var profile in appRepository.Profiles)
                AddProfileSource(profiles, profile, "App", isProjectOverride: false);
        }

        foreach (var profile in config.DeviceProfileOverrides)
            AddProfileSource(profiles, profile, "Project", isProjectOverride: true);

        return BuildProfileRowsFromProfiles(profiles.Values);
    }

    private static void AddProfileSource(
        Dictionary<string, ProfileSource> profiles,
        ControlDeviceProfile profile,
        string source,
        bool isProjectOverride)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            return;

        profiles[profile.Id] = new ProfileSource(profile, source, isProjectOverride);
    }

    private static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRowsFromProfiles(
        IEnumerable<ProfileSource> profiles) =>
        profiles
            .OrderBy(p => p.Profile.Protocol)
            .ThenBy(p => p.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(source =>
            {
                var profile = source.Profile;
                var summary = string.Join(
                    ", ",
                    new[]
                    {
                        FormatProfileCount(profile.Ports.Count, "port"),
                        FormatProfileCount(profile.Controls.Count, "control"),
                        FormatProfileCount(profile.Commands.Count, "command"),
                        FormatProfileCount(profile.Tasks.Count, "task"),
                    }.OfType<string>());
                return new ControlProfileRowViewModel(
                    string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName,
                    profile.Id,
                    profile.Protocol.ToString(),
                    source.Source,
                    string.IsNullOrWhiteSpace(summary) ? "No mapped controls, commands, or tasks." : summary,
                    source.IsProjectOverride);
            })
            .ToArray();

    private sealed record ProfileSource(ControlDeviceProfile Profile, string Source, bool IsProjectOverride);

    private static string? FormatProfileCount(int count, string label) =>
        count == 0 ? null : $"{count.ToString(CultureInfo.InvariantCulture)} {label}{(count == 1 ? string.Empty : "s")}";

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

    private static string GetPreferredDeviceKey(ControlDeviceInstanceConfig device)
    {
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.Name))
            return device.Name;
        return device.Id.ToString();
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
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OscCache);
        NotifySummary();
        return true;
    }

    private static ControlMidiPortCatalog? EnumerateMidiPorts() =>
        ControlMidiPortCatalogProvider.TryEnumerate();

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

    private static async Task<string?> DefaultProfileImportPathPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import control profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Control profile JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<string?> DefaultProfileExportDirectoryPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export control profiles",
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
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

        var host = TestOscHost.Trim();
        var address = TestOscAddress.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "OSC test host is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            StatusMessage = "OSC test address is required.";
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
            await osc.SendAsync(host, port, address, args).ConfigureAwait(true);
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Osc,
                Result = ControlMonitorResult.Sent,
                RemoteHost = host,
                RemotePort = port,
                Endpoint = $"{host}:{port}",
                Address = address,
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
                RemoteHost = host,
                RemotePort = port,
                Address = address,
                Message = "test send",
                ErrorMessage = ex.Message,
            });
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedX32CommandRow))]
    private void UseSelectedX32CommandForTestSend()
    {
        var row = SelectedX32CommandRow;
        if (row is null)
            return;

        TestOscHost = row.Host;
        TestOscPort = row.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TestOscAddress = row.Address;
        TestOscArgs = string.Empty;
        StatusMessage = $"Prepared '{row.CommandName}' for test send.";
    }

    [RelayCommand(CanExecute = nameof(CanRequestSelectedX32Command))]
    private async Task RequestSelectedX32CommandAsync()
    {
        UseSelectedX32CommandForTestSend();
        await SendTestOscAsync().ConfigureAwait(true);
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
            await session.EventQueue.DispatchManualAsync().ConfigureAwait(true);
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

    /// <summary>Finds the first decoded MIDI input captured at or after <paramref name="sinceUtc"/>.</summary>
    internal static ControlMonitorRecord? FindLearnCapture(IEnumerable<ControlMonitorRecord> records, DateTimeOffset sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.FirstOrDefault(r =>
            r.TimestampUtc >= sinceUtc
            && r.Direction == ControlMonitorDirection.Input
            && r.Protocol == ControlMonitorProtocol.Midi
            && (r.MidiMessageType is not null
                || r.MidiController is not null
                || r.MidiNote is not null
                || r.MidiValue is not null));
    }

    internal static string SuggestLearnFunctionName(ControlMonitorRecord record) =>
        record.MidiController is { } controller
            ? $"onCc{controller.ToString(CultureInfo.InvariantCulture)}"
            : record.MidiNote is { } note
                ? $"onNote{note.ToString(CultureInfo.InvariantCulture)}"
                : $"on{SanitizeFunctionSuffix((record.MidiMessageType ?? ControlMidiMessageType.Unknown).ToString())}";

    internal static ControlScriptTriggerConfig BuildLearnedTrigger(ControlMonitorRecord record, string functionName)
    {
        ArgumentNullException.ThrowIfNull(record);
        var messageType = InferMidiMessageType(record);
        if (record.MidiController is { } controller)
        {
            return new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MidiControlChange,
                FunctionName = functionName,
                MidiMessageType = messageType,
                MidiChannel = record.MidiChannel,
                MidiController = controller,
            };
        }

        if (record.MidiNote is { } note)
        {
            return new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MidiNote,
                FunctionName = functionName,
                MidiMessageType = messageType is ControlMidiMessageType.NoteOn or ControlMidiMessageType.NoteOff ? messageType : null,
                MidiChannel = record.MidiChannel,
                MidiNote = note,
            };
        }

        return new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.MidiMessage,
            FunctionName = functionName,
            MidiMessageType = messageType == ControlMidiMessageType.Unknown ? null : messageType,
            MidiChannel = record.MidiChannel,
            MidiValue = ShouldLearnMidiValue(messageType) ? record.MidiValue : null,
            MidiParameter = record.MidiParameter,
        };
    }

    internal static string BuildLearnedStub(ControlMonitorRecord record, string functionName)
    {
        var description = DescribeMidiRecord(record);
        return $"{Environment.NewLine}export fun {functionName}(event, context) {{{Environment.NewLine}"
            + $"    // TODO: handle {description}{Environment.NewLine}"
            + $"    // event.value holds the incoming value{Environment.NewLine}"
            + $"}}{Environment.NewLine}";
    }

    private static ControlMidiMessageType InferMidiMessageType(ControlMonitorRecord record)
    {
        if (record.MidiMessageType is { } messageType)
            return messageType;
        if (record.MidiController is not null)
            return ControlMidiMessageType.ControlChange;
        if (record.MidiNote is not null)
            return ControlMidiMessageType.NoteOn;
        return ControlMidiMessageType.Unknown;
    }

    private static bool ShouldLearnMidiValue(ControlMidiMessageType messageType) =>
        messageType is ControlMidiMessageType.ProgramChange
            or ControlMidiMessageType.SongSelect
            or ControlMidiMessageType.MIDITimeCode;

    private static string DescribeMidiRecord(ControlMonitorRecord record)
    {
        var channel = record.MidiChannel is { } ch ? $" on channel {ch.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var value = record.MidiValue is { } v ? $" value {v.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        if (record.MidiController is { } controller)
            return $"MIDI CC {controller.ToString(CultureInfo.InvariantCulture)}{channel}";
        if (record.MidiNote is { } note)
            return $"MIDI note {note.ToString(CultureInfo.InvariantCulture)}{channel}";
        if (record.MidiParameter is { } parameter)
            return $"MIDI {(record.MidiMessageType ?? ControlMidiMessageType.Unknown)} parameter {parameter.ToString(CultureInfo.InvariantCulture)}{channel}";
        return $"MIDI {(record.MidiMessageType ?? ControlMidiMessageType.Unknown)}{channel}{value}";
    }

    private static string SanitizeFunctionSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Midi";

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.Length == 0 ? "Midi" : builder.ToString();
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
    Action AddLayer,
    Action<ControlStructureRowViewModel> EditLayer,
    Action<ControlStructureRowViewModel> RemoveLayer,
    Action<ControlStructureRowViewModel> AddPeriodicSend,
    Action<ControlStructureRowViewModel> EditPeriodicSend,
    Action<ControlStructureRowViewModel> RemovePeriodicSend,
    Action<ControlStructureRowViewModel> EditOscDevice,
    Action<ControlStructureRowViewModel> RemoveOscDevice,
    Action<ControlStructureRowViewModel> TestOsc,
    Action<ControlStructureRowViewModel> TestMidi,
    Action AddOscListener,
    Action<ControlStructureRowViewModel> EditOscListener,
    Action<ControlStructureRowViewModel> RemoveOscListener);

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
            AddLayerCommand = new RelayCommand(commands.AddLayer);
            EditLayerCommand = new RelayCommand(() => commands.EditLayer(this), () => CanEditLayer);
            RemoveLayerCommand = new RelayCommand(() => commands.RemoveLayer(this), () => CanEditLayer);
            AddOscListenerCommand = new RelayCommand(commands.AddOscListener);
            EditOscListenerCommand = new RelayCommand(() => commands.EditOscListener(this), () => CanEditOscListener);
            RemoveOscListenerCommand = new RelayCommand(() => commands.RemoveOscListener(this), () => CanEditOscListener);
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

    public bool CanEditOscListener => OscListenerId is not null;

    public bool CanAddLayerScript => LayerId is not null;

    public bool CanActivateLayer => LayerId is not null;

    public bool CanEditLayer => LayerId is not null;

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

    public ICommand? AddLayerCommand { get; }

    public ICommand? EditLayerCommand { get; }

    public ICommand? RemoveLayerCommand { get; }

    public ICommand? AddOscListenerCommand { get; }

    public ICommand? EditOscListenerCommand { get; }

    public ICommand? RemoveOscListenerCommand { get; }

    public ICommand? AddPeriodicSendCommand { get; }

    public ICommand? EditPeriodicSendCommand { get; }

    public ICommand? RemovePeriodicSendCommand { get; }

    public ICommand? EditOscDeviceCommand { get; }

    public ICommand? RemoveOscDeviceCommand { get; }

    public ICommand? TestOscCommand { get; }

    public ICommand? TestMidiCommand { get; }
}

public sealed record ControlX32CommandRowViewModel(
    Guid DeviceInstanceId,
    string DeviceName,
    string DeviceKey,
    string Host,
    int? Port,
    string Group,
    string CommandName,
    string Address,
    string ValueKind,
    string Access,
    string CacheValue,
    bool CanRequest);

public sealed record ControlProfileRowViewModel(
    string DisplayName,
    string Id,
    string Protocol,
    string Source,
    string Summary,
    bool IsProjectOverride);

/// <summary>A selectable layer in the script editor's layer picker. <see cref="ToString"/> drives display.</summary>
public sealed record ControlLayerOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}

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

            var next = _script with { Scope = value };
            // Selecting Layer scope with no layer chosen would silently run the script globally, so bind it
            // to the first available layer; the picker still lets the user change it.
            if (value == ControlScriptScope.Layer && next.LayerId is null && LayerOptions.Count > 0)
                next = next with { LayerId = LayerOptions[0].Id };

            UpdateScript(
                next,
                nameof(Scope),
                nameof(ScopeText),
                nameof(ShowLayerPicker),
                nameof(ShowLayerSelector),
                nameof(ShowLayerHint),
                nameof(SelectedLayer));
        }
    }

    public string ScopeText => _script.Scope.ToString();

    /// <summary>Layer choices for the editor's layer picker. Populated by the owning workspace.</summary>
    public ObservableCollection<ControlLayerOption> LayerOptions { get; } = new();

    /// <summary>True while this script is layer-scoped and the layer picker (or its empty hint) should show.</summary>
    public bool ShowLayerPicker => _script.Scope == ControlScriptScope.Layer;

    /// <summary>True when the layer picker should show a selectable list of layers.</summary>
    public bool ShowLayerSelector => ShowLayerPicker && LayerOptions.Count > 0;

    /// <summary>True when layer scope is selected but no layers exist yet.</summary>
    public bool ShowLayerHint => ShowLayerPicker && LayerOptions.Count == 0;

    public ControlLayerOption? SelectedLayer
    {
        get => LayerOptions.FirstOrDefault(o => o.Id == _script.LayerId);
        set
        {
            if (value?.Id == _script.LayerId)
                return;

            UpdateScript(_script with { LayerId = value?.Id }, nameof(SelectedLayer));
        }
    }

    /// <summary>Replaces the available layer choices (called when project layers change).</summary>
    public void SetLayerOptions(IReadOnlyList<ControlLayerOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        LayerOptions.Clear();
        foreach (var option in options)
            LayerOptions.Add(option);

        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(ShowLayerSelector));
        OnPropertyChanged(nameof(ShowLayerHint));
    }

    /// <summary>Clears this script's layer binding when its layer was removed, keeping config in sync.</summary>
    public void OnLayerRemoved(Guid removedLayerId)
    {
        if (_script.LayerId == removedLayerId)
            UpdateScript(_script with { LayerId = null }, nameof(SelectedLayer));
    }

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
        if (trigger.MidiMessageType is { } messageType)
            label += $" {messageType}";
        if (trigger.MidiController is { } controller)
            label += $" cc{controller}";
        if (trigger.MidiNote is { } note)
            label += $" note{note}";
        if (trigger.MidiValue is { } value)
            label += $" value{value}";
        if (trigger.MidiParameter is { } parameter)
            label += $" param{parameter}";
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
                NormalizeMidiTrigger(_trigger with { Kind = value }),
                nameof(Kind),
                nameof(MidiMessageType),
                nameof(MidiMessageTypeOptions),
                nameof(MidiChannelText),
                nameof(MidiControllerText),
                nameof(MidiNoteText),
                nameof(MidiValueText),
                nameof(MidiParameterText),
                nameof(ShowOscAddress),
                nameof(ShowMidiMessageType),
                nameof(ShowMidiChannel),
                nameof(ShowMidiController),
                nameof(ShowMidiNote),
                nameof(ShowMidiValue),
                nameof(ShowMidiParameter),
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

    public IReadOnlyList<ControlMidiMessageType?> MidiMessageTypeOptions =>
        Kind switch
        {
            ControlScriptTriggerKind.MidiNote => [null, ControlMidiMessageType.NoteOn, ControlMidiMessageType.NoteOff],
            ControlScriptTriggerKind.MidiControlChange => [null, ControlMidiMessageType.ControlChange],
            _ => AllMidiMessageTypeOptions,
        };

    public ControlMidiMessageType? MidiMessageType
    {
        get => _trigger.MidiMessageType;
        set
        {
            if (value == _trigger.MidiMessageType)
                return;

            Update(
                NormalizeMidiTrigger(_trigger with { MidiMessageType = value }),
                nameof(MidiMessageType),
                nameof(MidiChannelText),
                nameof(MidiControllerText),
                nameof(MidiNoteText),
                nameof(MidiValueText),
                nameof(MidiParameterText),
                nameof(ShowMidiChannel),
                nameof(ShowMidiController),
                nameof(ShowMidiNote),
                nameof(ShowMidiValue),
                nameof(ShowMidiParameter));
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

    public string MidiValueText
    {
        get => FormatOptionalInt(_trigger.MidiValue);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiValue)
                return;

            Update(_trigger with { MidiValue = next }, nameof(MidiValueText));
        }
    }

    public string MidiParameterText
    {
        get => FormatOptionalInt(_trigger.MidiParameter);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiParameter)
                return;

            Update(_trigger with { MidiParameter = next }, nameof(MidiParameterText));
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

    public bool ShowMidiMessageType => Kind is ControlScriptTriggerKind.MidiMessage or ControlScriptTriggerKind.MidiNote;

    public bool ShowMidiChannel => Kind is ControlScriptTriggerKind.MidiControlChange
        or ControlScriptTriggerKind.MidiNote
        || (Kind == ControlScriptTriggerKind.MidiMessage && MidiMessageTypeUsesChannel(_trigger.MidiMessageType));

    public bool ShowMidiController => Kind == ControlScriptTriggerKind.MidiControlChange
        || (Kind == ControlScriptTriggerKind.MidiMessage && MidiMessageTypeUsesController(_trigger.MidiMessageType));

    public bool ShowMidiNote => Kind == ControlScriptTriggerKind.MidiNote
        || (Kind == ControlScriptTriggerKind.MidiMessage && MidiMessageTypeUsesNote(_trigger.MidiMessageType));

    public bool ShowMidiValue => Kind is ControlScriptTriggerKind.MidiControlChange
        or ControlScriptTriggerKind.MidiNote
        || (Kind == ControlScriptTriggerKind.MidiMessage && MidiMessageTypeUsesValue(_trigger.MidiMessageType));

    public bool ShowMidiParameter => Kind == ControlScriptTriggerKind.MidiMessage
        && MidiMessageTypeUsesParameter(_trigger.MidiMessageType);

    public bool ShowInterval => Kind is ControlScriptTriggerKind.Periodic;

    private bool IsMidiKind => Kind is ControlScriptTriggerKind.MidiMessage
        or ControlScriptTriggerKind.MidiControlChange
        or ControlScriptTriggerKind.MidiNote;

    private static readonly IReadOnlyList<ControlMidiMessageType?> AllMidiMessageTypeOptions =
        new ControlMidiMessageType?[] { null }
            .Concat(Enum.GetValues<ControlMidiMessageType>()
                .Where(t => t != ControlMidiMessageType.Unknown)
                .Select(t => (ControlMidiMessageType?)t))
            .ToArray();

    private static ControlScriptTriggerConfig NormalizeMidiTrigger(ControlScriptTriggerConfig trigger)
    {
        if (trigger.Kind == ControlScriptTriggerKind.MidiControlChange)
        {
            return trigger with
            {
                MidiMessageType = trigger.MidiMessageType is null or ControlMidiMessageType.ControlChange
                    ? trigger.MidiMessageType
                    : null,
                MidiNote = null,
                MidiParameter = null,
            };
        }

        if (trigger.Kind == ControlScriptTriggerKind.MidiNote)
        {
            return trigger with
            {
                MidiMessageType = trigger.MidiMessageType is null
                    or ControlMidiMessageType.NoteOn
                    or ControlMidiMessageType.NoteOff
                        ? trigger.MidiMessageType
                        : null,
                MidiController = null,
                MidiParameter = null,
            };
        }

        if (trigger.Kind != ControlScriptTriggerKind.MidiMessage || trigger.MidiMessageType is not { } messageType)
            return trigger;

        return trigger with
        {
            MidiChannel = MidiMessageTypeUsesChannel(messageType) ? trigger.MidiChannel : null,
            MidiController = MidiMessageTypeUsesController(messageType) ? trigger.MidiController : null,
            MidiNote = MidiMessageTypeUsesNote(messageType) ? trigger.MidiNote : null,
            MidiValue = MidiMessageTypeUsesValue(messageType) ? trigger.MidiValue : null,
            MidiParameter = MidiMessageTypeUsesParameter(messageType) ? trigger.MidiParameter : null,
        };
    }

    private static bool MidiMessageTypeUsesChannel(ControlMidiMessageType? messageType) =>
        messageType is null
            or ControlMidiMessageType.NRPN
            or ControlMidiMessageType.RPN
            or ControlMidiMessageType.NoteOff
            or ControlMidiMessageType.NoteOn
            or ControlMidiMessageType.PolyphonicAftertouch
            or ControlMidiMessageType.ControlChange
            or ControlMidiMessageType.ProgramChange
            or ControlMidiMessageType.ChannelAftertouch
            or ControlMidiMessageType.PitchBend;

    private static bool MidiMessageTypeUsesController(ControlMidiMessageType? messageType) =>
        messageType is null or ControlMidiMessageType.ControlChange;

    private static bool MidiMessageTypeUsesNote(ControlMidiMessageType? messageType) =>
        messageType is null
            or ControlMidiMessageType.NoteOff
            or ControlMidiMessageType.NoteOn
            or ControlMidiMessageType.PolyphonicAftertouch;

    private static bool MidiMessageTypeUsesValue(ControlMidiMessageType? messageType) =>
        messageType is null
            or ControlMidiMessageType.NRPN
            or ControlMidiMessageType.RPN
            or ControlMidiMessageType.NoteOff
            or ControlMidiMessageType.NoteOn
            or ControlMidiMessageType.PolyphonicAftertouch
            or ControlMidiMessageType.ControlChange
            or ControlMidiMessageType.ProgramChange
            or ControlMidiMessageType.ChannelAftertouch
            or ControlMidiMessageType.PitchBend
            or ControlMidiMessageType.SysEx
            or ControlMidiMessageType.MIDITimeCode
            or ControlMidiMessageType.SongPosition
            or ControlMidiMessageType.SongSelect;

    private static bool MidiMessageTypeUsesParameter(ControlMidiMessageType? messageType) =>
        messageType is null or ControlMidiMessageType.NRPN or ControlMidiMessageType.RPN;

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
        if (record.MidiParameter is { } parameter)
            return $"{(record.MidiMessageType ?? ControlMidiMessageType.Unknown)} param {parameter.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        return $"{(record.MidiMessageType ?? ControlMidiMessageType.Unknown)}{channel}{value}";
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
