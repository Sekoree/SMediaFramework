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
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.Services;
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
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const int MaxRenderedEntries = 1000;
    private const string AllFilter = "All";
    private const string DefaultX32ProfileId = "behringer.x32.osc";

    private readonly DispatcherTimer _refreshTimer;
    private ControlSystemConfig _config = new();
    private string? _projectRoot;
    private string? _scratchRoot;
    private string? _configFilePath;
    private ControlMonitorBuffer? _monitorBuffer;
    private ControlSystemRuntimeSession? _session;
    private UdpControlOSCSender? _oscSender;
    private IControlMIDISender? _midiSender;
    private long _lastRenderedVersion = -1;
    private long _lastX32CacheVersion = -1;
    private readonly Dictionary<Guid, string[]> _x32DeviceCacheKeys = new();
    private bool _filterDirty;
    private bool _busy;
    private DateTimeOffset _learnSinceUtc;

    // Fallback MIDI device resolution - injectable so unit tests can supply a fake catalog/prompt
    // without touching PortMIDI or showing a real dialog.
    internal Func<ControlMIDIPortCatalog?> MIDICatalogProvider { get; set; } = EnumerateMIDIPorts;

    internal Func<IReadOnlyList<ControlMIDIResolutionRequest>, Task<IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>?>> MIDIResolutionPrompt { get; set; } = DefaultPromptAsync;

    internal Func<Task<string?>> ProfileImportPathPrompt { get; set; } = DefaultProfileImportPathPromptAsync;

    internal Func<Task<string?>> ProfileExportDirectoryPrompt { get; set; } = DefaultProfileExportDirectoryPromptAsync;

    public ControlWorkspaceViewModel()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshMonitor();
        _refreshTimer.Start();

        // Build the structure for the default (empty) config so the workspace shows its groups - OSC
        // devices, layers, scripts, periodic sends - before any project is loaded or any MIDI/OSC device
        // exists. That lets you prepare an OSC-only or scripts/layers-only setup straight away.
        RebuildScriptRows();
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        MonitorEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMonitorEntries));

        // Docking layout for the four Control panes (Surfaces / Scripts / Monitor / Tools) - see
        // ControlDockFactory. The DockControl in ControlWorkspaceView binds to DockLayout.
        BuildDockLayout();
    }

    /// <summary>The Dock.Avalonia layout the Control workspace's <c>DockControl</c> renders. Lets the operator
    /// split / float / re-dock the Surfaces, Scripts, Monitor, and Tools panes. Rebuilt by
    /// <see cref="ResetDockLayoutCommand"/> to bring back a pane that was closed or floated away.</summary>
    [ObservableProperty]
    private Dock.Model.Controls.IRootDock _dockLayout = null!;

    /// <summary>Restores the default docking arrangement (all four panes, tabbed) - the way back after a pane
    /// is accidentally closed or dragged into a floating window.</summary>
    [RelayCommand]
    private void ResetDockLayout() => BuildDockLayout();

    private void BuildDockLayout()
    {
        // Close any floating panes from the previous layout first, so Reset doesn't leave orphaned windows.
        DockLayout?.ExitWindows?.Execute(null);
        var factory = new ControlDock.ControlDockFactory(this);
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);
        DockLayout = layout;
    }

    public ObservableCollection<ControlMonitorEntryViewModel> MonitorEntries { get; } = new();
    public bool HasNoMonitorEntries => MonitorEntries.Count == 0;

    public ObservableCollection<ControlScriptRowViewModel> ScriptRows { get; } = new();

    public ObservableCollection<ControlScriptDiagnosticRowViewModel> ScriptDiagnostics { get; } = new();

    public ObservableCollection<ControlStructureRowViewModel> StructureRows { get; } = new();

    public ObservableCollection<ControlX32CommandRowViewModel> X32CommandRows { get; } = new();

    public ObservableCollection<ControlProfileRowViewModel> ProfileRows { get; } = new();

    public ObservableCollection<string> ProfileWarnings { get; } = new();

    // Test seam (like MIDICatalogProvider): the device-RESOLUTION flows are pure over an injected catalog,
    // so tests on runners without a native portmidi override this probe - production always asks the runtime.
    internal Func<bool> MIDIAvailabilityProbe { get; set; } = static () => RuntimeModules.IsMIDIAvailable;

    public bool IsMIDIAvailable => MIDIAvailabilityProbe();
    public string MIDIUnavailableStatus => RuntimeModules.MIDIUnavailableReason ?? "MIDI runtime unavailable.";

    [ObservableProperty]
    private string _profileBuilderDisplayName = "Custom MIDI Surface";

    [ObservableProperty]
    private string _profileBuilderId = string.Empty;

    [ObservableProperty]
    private string _profileBuilderControlName = "Fader 1";

    [ObservableProperty]
    private string _profileBuilderMIDIChannelText = "1";

    [ObservableProperty]
    private string _profileBuilderMIDIControllerText = "0";

    [ObservableProperty]
    private bool _profileBuilderHighResolution14Bit;

    [ObservableProperty]
    private string _profileBuilderMinValueText = string.Empty;

    [ObservableProperty]
    private string _profileBuilderMaxValueText = string.Empty;

    [ObservableProperty]
    private string _profileBuilderStatus = string.Empty;

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
        nameof(ControlMonitorProtocol.MIDI),
        nameof(ControlMonitorProtocol.OSC),
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

    // The selected script's on-disk (or last-saved) text. The editor buffer is <see cref="SelectedScriptText"/>;
    // when the two differ the script has unsaved edits - see <see cref="IsSelectedScriptDirty"/>.
    private string _savedScriptBaseline = string.Empty;

    /// <summary>True when the editor buffer differs from what's on disk for the selected script - drives the
    /// script editor's "unsaved changes" bar. Set the baseline via <see cref="LoadSelectedScriptText"/> (load)
    /// and after a successful save.</summary>
    public bool IsSelectedScriptDirty =>
        SelectedScriptRow is not null
        && !string.Equals(SelectedScriptText, _savedScriptBaseline, StringComparison.Ordinal);

    /// <summary>Saves the current editor overlay when needed. Used by the shell's Save All workflow.</summary>
    internal bool TrySaveDirtyScriptBuffer()
    {
        if (!IsSelectedScriptDirty)
            return true;
        SaveSelectedScript();
        return !IsSelectedScriptDirty;
    }

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
        OnPropertyChanged(nameof(IsSelectedScriptDirty));
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
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);

    partial void OnSelectedX32CommandRowChanged(ControlX32CommandRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedX32CommandRow));
        OnPropertyChanged(nameof(CanRequestSelectedX32Command));
        UseSelectedX32CommandForTestSendCommand.NotifyCanExecuteChanged();
        RequestSelectedX32CommandCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelectedX32CommandRow => SelectedX32CommandRow is not null;

    public bool CanRequestSelectedX32Command => SelectedX32CommandRow?.CanRequest == true;

    partial void OnSelectedScriptTextChanged(string value)
    {
        RefreshScriptAnalysis(SelectedScriptRow);
        OnPropertyChanged(nameof(IsSelectedScriptDirty));
    }

    /// <summary>Reverts the selected script's editor buffer to what's on disk (drops unsaved edits).</summary>
    [RelayCommand]
    private void DiscardSelectedScriptChanges() => LoadSelectedScriptText(SelectedScriptRow);

    public int DeviceCount => _config.Devices.Count;

    public int ScriptCount => _config.Scripts.Count;

    public int ListenerCount => _config.OSCListeners.Count;

    public int LayerCount => _config.Layers.Count;

    [ObservableProperty]
    private string _testOSCHost = string.Empty;

    [ObservableProperty]
    private string _testOSCPort = string.Empty;

    [ObservableProperty]
    private string _testOSCAddress = "/info";

    [ObservableProperty]
    private string _testOSCArgs = string.Empty;

    /// <summary>Loads a project's control configuration. CTRL-04 safety: this always leaves the system
    /// DISARMED - a freshly-opened project never runs its (trusted-code) scripts until the operator explicitly
    /// arms it. <see cref="BuildSnapshot"/> likewise persists <c>IsArmed = false</c>, so an armed state is
    /// never saved into a project and can never auto-run on open.</summary>
    public void LoadConfig(ControlSystemConfig config, string? configFilePath = null)
    {
        StopSessionFireAndForget(); // ensures a previously-armed session can't leak scripts across a project load
        _config = config ?? new ControlSystemConfig();
        _configFilePath = configFilePath;
        MonitorEntries.Clear();
        RebuildScriptRows();
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(cache: null);
        _lastRenderedVersion = -1;
        StatusMessage = "Disarmed.";
        NotifySummary();
        NotifyArmState();
    }

    /// <summary>Sets the directory that project-relative script files resolve against (the project folder).
    /// When a project gains a home on disk for the first time, any scripts the operator authored while the
    /// project was unsaved (kept under the scratch cache - see <see cref="EffectiveScriptRoot"/>) are migrated
    /// into the project so they're saved alongside it.</summary>
    public void SetProjectRoot(string? projectRoot)
    {
        var gainedRealRoot = string.IsNullOrWhiteSpace(_projectRoot) && !string.IsNullOrWhiteSpace(projectRoot);
        _projectRoot = projectRoot;
        if (gainedRealRoot)
            MigrateScratchScriptsInto(projectRoot!);
        LoadSelectedScriptText(SelectedScriptRow);
        SaveSelectedScriptCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedScratchScripts));
    }

    /// <summary>Where script files are read/written. Falls back to a per-session scratch cache directory while
    /// the project has no home on disk, so scripts are fully editable before the first project save (rather
    /// than dead-ending on "save the project first"). Migrated into the project by <see cref="SetProjectRoot"/>.</summary>
    internal string EffectiveScriptRoot =>
        string.IsNullOrWhiteSpace(_projectRoot) ? EnsureScratchRoot() : _projectRoot!;

    /// <summary>True while scripts live only in the scratch cache (the project has never been saved). Used by
    /// the shell to prompt "save your work?" before the app closes.</summary>
    public bool HasUnsavedScratchScripts =>
        string.IsNullOrWhiteSpace(_projectRoot) && _config.Scripts.Count > 0;

    /// <summary>The scratch directory whose script files session-recovery should mirror, or <see langword="null"/>
    /// when the project is saved (its scripts live on disk beside the project and need no crash copy). Only
    /// returns a path when there are unsaved scripts, so it never materializes an empty scratch root just to be
    /// polled.</summary>
    internal string? UnsavedScriptsRoot =>
        HasUnsavedScratchScripts ? EffectiveScriptRoot : null;

    /// <summary>Re-materializes recovered scratch scripts (from a crashed untitled session) into a fresh scratch
    /// root, so a following <see cref="LoadConfig"/> resolves the project's script references against files that
    /// exist. Best-effort; call before applying the recovered control config.</summary>
    internal bool RestoreScratchScriptsFrom(string scriptsDir)
    {
        if (string.IsNullOrWhiteSpace(scriptsDir) || !Directory.Exists(scriptsDir))
            return false;

        try
        {
            // Untitled project ⇒ scripts belong in the scratch cache. Force a fresh root so the recovered files
            // don't collide with anything this launch already created.
            _projectRoot = null;
            _scratchRoot = null;
            var root = EnsureScratchRoot();
            foreach (var source in Directory.EnumerateFiles(scriptsDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(scriptsDir, source);
                var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScriptEditorStatus = $"Some recovered scripts could not be restored: {ex.Message}";
            return false;
        }
    }

    /// <summary>Captures every configured script plus the selected editor overlay. A saved project can then be
    /// restored as a self-contained copy, and text that had not reached disk is not lost.</summary>
    internal IReadOnlyList<RecoveryScriptFile> BuildRecoveryScriptFiles()
    {
        if (_config.Scripts.Count == 0)
            return [];

        var root = Path.GetFullPath(EffectiveScriptRoot);
        var files = new Dictionary<string, RecoveryScriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in _config.Scripts)
        {
            var path = ResolveScriptPath(script.ScriptPath);
            if (path is null)
                continue;
            var relative = Path.GetRelativePath(root, path);
            var isDirty = IsSelectedScriptDirty && SelectedScriptRow?.Script.Id == script.Id;
            if (!isDirty && !File.Exists(path))
                continue;
            var contents = isDirty ? SelectedScriptText : File.ReadAllText(path);
            files[relative] = new RecoveryScriptFile(relative, contents, isDirty);
        }
        return files.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Re-applies a recovered editor overlay without writing through to the original script file.</summary>
    internal bool RestoreDirtyScriptBufferFrom(string scriptsDir, IReadOnlyList<string> dirtyScriptPaths)
    {
        var dirtyPath = dirtyScriptPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dirtyPath))
            return true;
        var row = ScriptRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Script.ScriptPath, dirtyPath, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return false;

        var recoveredPath = Path.GetFullPath(Path.Combine(scriptsDir, dirtyPath));
        var scriptsRoot = Path.GetFullPath(scriptsDir) + Path.DirectorySeparatorChar;
        if (!recoveredPath.StartsWith(scriptsRoot, PathComparison) || !File.Exists(recoveredPath))
            return false;

        SelectedScriptRow = row; // loads the on-disk baseline first
        SelectedScriptText = File.ReadAllText(recoveredPath); // overlay remains dirty until Save All
        return true;
    }

    private string EnsureScratchRoot()
    {
        if (_scratchRoot is not null)
            return _scratchRoot;

        // Per-instance folder so two unsaved sessions never fight over the same script paths.
        _scratchRoot = Path.Combine(HaPlayStoragePaths.LocalAppRoot, "unsaved-scripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
        return _scratchRoot;
    }

    private void MigrateScratchScriptsInto(string projectRoot)
    {
        if (_scratchRoot is null || !Directory.Exists(_scratchRoot))
            return;

        try
        {
            foreach (var source in Directory.EnumerateFiles(_scratchRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_scratchRoot, source);
                var destination = Path.Combine(projectRoot, relative);
                // Don't clobber a file the project already carries (e.g. re-saving into an existing project).
                if (File.Exists(destination))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }

            Directory.Delete(_scratchRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScriptEditorStatus = $"Some unsaved scripts could not be moved into the project: {ex.Message}";
            return;
        }

        _scratchRoot = null;
    }

    public ControlSystemConfig BuildSnapshot() => _config with { IsArmed = false };

    public string? ConfigFilePath => _configFilePath;

    internal async Task LoadControlConfigFromPathAsync(string path)
    {
        try
        {
            var config = await ControlSystemIO.LoadConfigAsync(path).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                SetProjectRoot(Path.GetDirectoryName(path));
                LoadConfig(config, path);
                StatusMessage = Strings.Format(nameof(Strings.LoadedControlConfigStatusFormat), Path.GetFileName(path));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
                StatusMessage = Strings.Format(nameof(Strings.ControlConfigLoadFailedStatusFormat), ex.Message))
                .ConfigureAwait(false);
        }
    }

    internal async Task SaveControlConfigToPathAsync(string path)
    {
        try
        {
            if (IsArmed)
                await DisarmInternalAsync().ConfigureAwait(true);

            await ControlSystemIO.SaveConfigAsync(BuildSnapshot(), path, "HaPlay").ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                _configFilePath = path;
                OnPropertyChanged(nameof(ConfigFilePath));
                StatusMessage = Strings.Format(nameof(Strings.SavedControlConfigStatusFormat), Path.GetFileName(path));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
                StatusMessage = Strings.Format(nameof(Strings.ControlConfigSaveFailedStatusFormat), ex.Message))
                .ConfigureAwait(false);
        }
    }

    private static string SanitizeControlConfigFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Strings.ControlConfigFileNameFallback;

        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public IReadOnlyList<ControlDeviceInstanceConfig> GetMIDIInputDevices() =>
        _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && HasMIDIInputBinding(d.Binding))
            .ToList();

    public IReadOnlyList<ControlDeviceInstanceConfig> GetMIDIOutputDevices() =>
        _config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && HasMIDIOutputBinding(d.Binding))
            .ToList();

    public void AddOrUpdateMIDIInputDevice(int deviceId, string deviceName) =>
        AddOrUpdateMIDIDevice(deviceId, deviceName, isInput: true);

    public void AddOrUpdateMIDIOutputDevice(int deviceId, string deviceName) =>
        AddOrUpdateMIDIDevice(deviceId, deviceName, isInput: false);

    public bool RemoveMIDIInputDevice(Guid deviceInstanceId) =>
        RemoveMIDIBinding(deviceInstanceId, isInput: true);

    public bool RemoveMIDIOutputDevice(Guid deviceInstanceId) =>
        RemoveMIDIBinding(deviceInstanceId, isInput: false);

    private void AddOrUpdateMIDIDevice(int deviceId, string deviceName, bool isInput)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = deviceId.ToString(CultureInfo.InvariantCulture);

        var trimmedName = deviceName.Trim();
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d =>
            d.Protocol == ControlDeviceProtocol.MIDI
            && (string.Equals(d.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Binding.MIDIInputDeviceName, trimmedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Binding.MIDIOutputDeviceName, trimmedName, StringComparison.OrdinalIgnoreCase)));

        if (index < 0)
        {
            devices.Add(new ControlDeviceInstanceConfig
            {
                Name = trimmedName,
                ProfileId = "generic-midi",
                Protocol = ControlDeviceProtocol.MIDI,
                IsEnabled = true,
                Binding = CreateMIDIBinding(deviceId, trimmedName, isInput, existingAliases: devices.Select(d => d.Binding.Alias)),
            });
        }
        else
        {
            var device = devices[index];
            var binding = isInput
                ? device.Binding with { MIDIInputDeviceId = deviceId, MIDIInputDeviceName = trimmedName }
                : device.Binding with { MIDIOutputDeviceId = deviceId, MIDIOutputDeviceName = trimmedName };
            devices[index] = device with
            {
                Name = string.IsNullOrWhiteSpace(device.Name) ? trimmedName : device.Name,
                Protocol = ControlDeviceProtocol.MIDI,
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
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        NotifySummary();
    }

    private bool RemoveMIDIBinding(Guid deviceInstanceId, bool isInput)
    {
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d => d.Id == deviceInstanceId && d.Protocol == ControlDeviceProtocol.MIDI);
        if (index < 0)
            return false;

        var device = devices[index];
        var binding = isInput
            ? device.Binding with { MIDIInputDeviceId = null, MIDIInputDeviceName = null }
            : device.Binding with { MIDIOutputDeviceId = null, MIDIOutputDeviceName = null };

        if (!HasMIDIInputBinding(binding) && !HasMIDIOutputBinding(binding))
            devices.RemoveAt(index);
        else
            devices[index] = device with { Binding = binding };

        _config = _config with { Devices = devices };
        if (IsArmed)
            StatusMessage = "MIDI device updated. Re-arm control to apply device changes.";
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        NotifySummary();
        return true;
    }

    private static bool HasMIDIInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIInputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName);

    private static bool HasMIDIOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIOutputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName);

    private static ControlDeviceBindingConfig CreateMIDIBinding(
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
            ? binding with { MIDIInputDeviceId = deviceId, MIDIInputDeviceName = deviceName }
            : binding with { MIDIOutputDeviceId = deviceId, MIDIOutputDeviceName = deviceName };
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

        var contents = SelectedScriptText;

        // Scripts write to the project folder, or to the scratch cache while the project is unsaved (they
        // migrate into the project on its first save - see SetProjectRoot). Either way the operator can
        // author scripts immediately instead of dead-ending on "save the project first".
        var path = ResolveScriptPath(SelectedScriptRow.Script.ScriptPath);
        if (path is null)
        {
            ScriptEditorStatus = "Script path is not available.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            _savedScriptBaseline = contents; // buffer now matches disk → no longer dirty
            OnPropertyChanged(nameof(IsSelectedScriptDirty));
            OnPropertyChanged(nameof(HasUnsavedScratchScripts));
            ScriptEditorStatus = string.IsNullOrWhiteSpace(_projectRoot)
                ? $"Saved {SelectedScriptRow.Script.ScriptPath} to the scratch cache - save the project to keep it."
                : $"Saved {SelectedScriptRow.Script.ScriptPath}.";
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

    [RelayCommand]
    private void AddHelperFile() => AddHelperScript();

    private void AddDeviceScript(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is { } deviceId)
            AddScriptInternal(ControlScriptScope.Device, deviceId, layerId: null);
    }

    private void AddEndpointScript(ControlStructureRowViewModel row)
    {
        if (row.OSCListenerId is { } listenerId)
            AddScriptInternal(ControlScriptScope.Endpoint, deviceInstanceId: null, layerId: null, endpointInstanceId: listenerId);
    }

    private void AddLayerScript(ControlStructureRowViewModel row)
    {
        if (row.LayerId is { } layerId)
            AddScriptInternal(ControlScriptScope.Layer, deviceInstanceId: null, layerId);
    }


    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        await DisarmInternalAsync().ConfigureAwait(false);
    }
}
