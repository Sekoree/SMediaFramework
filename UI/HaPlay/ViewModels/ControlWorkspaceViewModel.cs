using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

    partial void OnFilterTextChanged(string value) => _filterDirty = true;

    partial void OnErrorsOnlyChanged(bool value) => _filterDirty = true;

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
        _lastRenderedCount = -1;
        StatusMessage = "Disarmed.";
        NotifySummary();
        NotifyArmState();
    }

    /// <summary>Sets the directory that project-relative script files resolve against (the project folder).</summary>
    public void SetProjectRoot(string? projectRoot) => _projectRoot = projectRoot;

    public ControlSystemConfig BuildSnapshot() => _config with { IsArmed = false };

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
        try
        {
            var monitor = new ControlMonitorBuffer(Math.Max(1, _config.Monitor.MaxVisibleMessages));
            var osc = new UdpControlOscSender();
            var session = new ControlSystemRuntimeSession(
                _config with { IsArmed = true },
                CreateSourceProvider(),
                osc,
                monitor: monitor);
            await session.StartAsync().ConfigureAwait(true);

            _monitorBuffer = monitor;
            _oscSender = osc;
            _session = session;
            _lastRenderedCount = -1;
            StatusMessage = $"Armed — {ListenerCount} listener(s), {DeviceCount} device(s), {ScriptCount} script(s).";
        }
        catch (Exception ex)
        {
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

        IEnumerable<ControlMonitorRecord> query = records;
        if (ErrorsOnly)
            query = query.Where(r => r.Direction == ControlMonitorDirection.Error || r.Result == ControlMonitorResult.Failed);

        var text = FilterText;
        if (!string.IsNullOrWhiteSpace(text))
            query = query.Where(r => MatchesText(r, text));

        var filtered = query.TakeLast(MaxRenderedEntries).Select(r => new ControlMonitorEntryViewModel(r)).ToList();

        MonitorEntries.Clear();
        foreach (var entry in filtered)
            MonitorEntries.Add(entry);
    }

    private static bool MatchesText(ControlMonitorRecord record, string text) =>
        Contains(record.Address, text)
        || Contains(record.Message, text)
        || Contains(record.ErrorMessage, text)
        || Contains(record.DeviceKey, text)
        || Contains(record.Endpoint, text);

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
