using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.ControlGraph;
using HaPlay.Models;
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

    private readonly DispatcherTimer _refreshTimer;
    private ControlSystemConfig _config = new();
    private string? _projectRoot;
    private ControlMonitorBuffer? _monitorBuffer;
    private ControlSystemRuntimeSession? _session;
    private UdpControlOscSender? _oscSender;
    private int _lastRenderedCount = -1;
    private bool _filterDirty;
    private bool _busy;

    public ControlWorkspaceViewModel()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshMonitor();
        _refreshTimer.Start();
    }

    public ObservableCollection<ControlMonitorEntryViewModel> MonitorEntries { get; } = new();

    public ObservableCollection<ControlScriptRowViewModel> ScriptRows { get; } = new();

    public ObservableCollection<ControlStructureRowViewModel> StructureRows { get; } = new();

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

    partial void OnFilterTextChanged(string value) => _filterDirty = true;

    partial void OnErrorsOnlyChanged(bool value) => _filterDirty = true;

    partial void OnSelectedMonitorDirectionChanged(string value) => _filterDirty = true;

    partial void OnSelectedMonitorProtocolChanged(string value) => _filterDirty = true;

    partial void OnDeviceFilterTextChanged(string value) => _filterDirty = true;

    partial void OnSelectedScriptRowChanged(ControlScriptRowViewModel? value)
    {
        LoadSelectedScriptText(value);
        SaveSelectedScriptCommand.NotifyCanExecuteChanged();
    }

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
    }

    private bool CanSaveSelectedScript() =>
        SelectedScriptRow is not null
        && !string.IsNullOrWhiteSpace(SelectedScriptRow.Script.ScriptPath)
        && !string.IsNullOrWhiteSpace(_projectRoot);

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
        foreach (var row in BuildStructureRows(_config))
            StructureRows.Add(row);
    }

    internal static IReadOnlyList<ControlStructureRowViewModel> BuildStructureRows(ControlSystemConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rows = new List<ControlStructureRowViewModel>();

        AddGroup(rows, "MIDI devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.Midi));
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Midi).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "MIDI",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed MIDI)" : device.Name,
                FormatMidiBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1));
        }

        AddGroup(rows, "OSC listeners", config.OscListeners.Count);
        foreach (var listener in config.OscListeners.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Listen",
                string.IsNullOrWhiteSpace(listener.Name) ? "(unnamed listener)" : listener.Name,
                $"port {listener.LocalPort.ToString(CultureInfo.InvariantCulture)} - {listener.SocketMode}",
                FormatEnabled(listener.IsEnabled),
                Level: 1));
        }

        AddGroup(rows, "OSC devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.Osc));
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Osc).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "OSC",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                FormatOscBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1));
        }

        AddGroup(rows, "Layers", config.Layers.Count);
        foreach (var layer in config.Layers.OrderBy(l => l.Priority).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Layer",
                string.IsNullOrWhiteSpace(layer.Name) ? "(unnamed layer)" : layer.Name,
                $"priority {layer.Priority.ToString(CultureInfo.InvariantCulture)} - {layer.ScriptIds.Count.ToString(CultureInfo.InvariantCulture)} script(s)",
                FormatEnabled(layer.IsEnabled),
                Level: 1));
        }

        AddGroup(rows, "Scripts", config.Scripts.Count);
        foreach (var script in config.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Script",
                string.IsNullOrWhiteSpace(script.Name) ? "(unnamed script)" : script.Name,
                $"{script.Scope} - {FormatScriptPath(script.ScriptPath)} - {script.Triggers.Count.ToString(CultureInfo.InvariantCulture)} trigger(s)",
                FormatEnabled(script.IsEnabled),
                Level: 1));
        }

        var periodic = config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.Osc)
            .SelectMany(d => d.PeriodicOscSends.Select(s => (Device: d, Send: s)))
            .OrderBy(x => x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Send.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddGroup(rows, "Periodic sends", periodic.Count);
        foreach (var item in periodic)
        {
            rows.Add(new ControlStructureRowViewModel(
                "Periodic",
                string.IsNullOrWhiteSpace(item.Send.Name) ? item.Send.Address : item.Send.Name,
                $"{item.Device.Name}: {item.Send.Address} every {item.Send.IntervalMs.ToString(CultureInfo.InvariantCulture)} ms",
                FormatEnabled(item.Send.IsEnabled && item.Device.IsEnabled),
                Level: 1));
        }

        return rows;
    }

    private static void AddGroup(List<ControlStructureRowViewModel> rows, string name, int count) =>
        rows.Add(new ControlStructureRowViewModel(
            "Group",
            name,
            $"{count.ToString(CultureInfo.InvariantCulture)} configured",
            string.Empty,
            Level: 0,
            IsGroup: true));

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
            return;
        }

        var path = ResolveScriptPath(row.Script.ScriptPath);
        if (path is null)
        {
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = string.IsNullOrWhiteSpace(row.Script.ScriptPath)
                ? "Script has no file path."
                : "Open or save the project before editing project-relative scripts.";
            return;
        }

        if (!File.Exists(path))
        {
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = $"New file: {row.Script.ScriptPath}.";
            return;
        }

        SelectedScriptText = File.ReadAllText(path);
        ScriptEditorStatus = row.Script.ScriptPath;
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

    private async Task ArmInternalAsync()
    {
        ControlSystemRuntimeSession? pendingSession = null;
        UdpControlOscSender? pendingOsc = null;
        try
        {
            var armedConfig = _config with { IsArmed = true };
            var monitor = new ControlMonitorBuffer(Math.Max(1, _config.Monitor.MaxVisibleMessages));
            var osc = new UdpControlOscSender();
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

    private void RefreshMonitor()
    {
        var buffer = _monitorBuffer;
        if (buffer is null || IsPaused)
            return;

        var records = buffer.Records;
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
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(ArmButtonText));
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

public sealed class ControlStructureRowViewModel
{
    public ControlStructureRowViewModel(
        string kind,
        string name,
        string detail,
        string state,
        int Level,
        bool IsGroup = false)
    {
        Kind = kind;
        Name = name;
        Detail = detail;
        State = state;
        this.Level = Level;
        this.IsGroup = IsGroup;
    }

    public string Kind { get; }

    public string Name { get; }

    public string Detail { get; }

    public string State { get; }

    public int Level { get; }

    public bool IsGroup { get; }

    public double IndentWidth => Level * 16;
}

public sealed class ControlScriptRowViewModel : ViewModelBase
{
    private readonly Action<ControlScriptRowViewModel, ControlScriptConfig>? _onChanged;
    private ControlScriptConfig _script;

    public ControlScriptRowViewModel(ControlScriptConfig script, Action<ControlScriptRowViewModel, ControlScriptConfig>? onChanged = null)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _onChanged = onChanged;
    }

    public ControlScriptConfig Script => _script;

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
