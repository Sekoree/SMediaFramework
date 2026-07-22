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
/// Context-menu test sends and MIDI learn mode.
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- Context-menu test sends ------------------------------------------------------------

    /// <summary>Prefills the test-send fields from the OSC device endpoint and sends the current test address.</summary>
    private async Task TestOSCDeviceAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId)
            return;

        var device = _config.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
            return;

        if (!string.IsNullOrWhiteSpace(device.Binding.OSCHost))
            TestOSCHost = device.Binding.OSCHost!;
        if (device.Binding.OSCPort is { } port)
            TestOSCPort = port.ToString(CultureInfo.InvariantCulture);

        await SendTestOSCAsync().ConfigureAwait(true);
    }

    /// <summary>Sends a single recognizable test CC to the selected MIDI device's output.</summary>
    private async Task TestMIDIDeviceAsync(ControlStructureRowViewModel row)
    {
        if (!IsMIDIAvailable)
        {
            StatusMessage = MIDIUnavailableStatus;
            return;
        }

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
                Protocol = ControlMonitorProtocol.MIDI,
                Result = ControlMonitorResult.Sent,
                DeviceInstanceId = deviceId,
                MIDIChannel = channel,
                MIDIController = controller,
                MIDIValue = value,
                Message = "test send",
            });
            StatusMessage = $"Sent test MIDI cc{controller}={value}.";
        }
        catch (Exception ex)
        {
            _monitorBuffer?.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.MIDI,
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

        var trigger = BuildLearnedTrigger(candidate.Record, functionName);
        if (trigger.MIDIValue is null && candidate.HasValueRange)
            trigger = trigger with { MIDIValueMin = candidate.MinimumValue, MIDIValueMax = candidate.MaximumValue };
        row.AddLearnedTrigger(trigger);

        if (candidate.InsertStub && !HasExport(SelectedScriptText, functionName))
            SelectedScriptText += BuildLearnedStub(candidate.Record, functionName);

        IsLearning = false;
        LearnCandidate = null;
        StatusMessage = $"Added '{functionName}' trigger. Review and save the script.";
    }

    private bool CanConfirmLearn() => HasLearnCandidate && HasSelectedScript;

    /// <summary>Promotes a captured monitor record into an editable learn candidate. Internal for tests.</summary>
    internal void ApplyLearnCapture(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (LearnCandidate is { } candidate && candidate.TryObserve(record))
        {
            _learnSinceUtc = record.TimestampUtc.AddTicks(1);
            StatusMessage = $"Learning {candidate.Description}. Move the control through its range, then confirm.";
            return;
        }

        LearnCandidate = new ControlLearnCandidateViewModel(record, SuggestLearnFunctionName(record));
        _learnSinceUtc = record.TimestampUtc.AddTicks(1);
        StatusMessage = $"Learned {LearnCandidate.Description}. Move it through min/max, then confirm.";
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
            && r.Protocol == ControlMonitorProtocol.MIDI
            && (r.MIDIMessageType is not null
                || r.MIDIController is not null
                || r.MIDINote is not null
                || r.MIDIValue is not null));
    }

    internal static string SuggestLearnFunctionName(ControlMonitorRecord record) =>
        record.MIDIController is { } controller
            ? $"onCc{controller.ToString(CultureInfo.InvariantCulture)}"
            : record.MIDINote is { } note
                ? $"onNote{note.ToString(CultureInfo.InvariantCulture)}"
                : $"on{SanitizeFunctionSuffix((record.MIDIMessageType ?? ControlMIDIMessageType.Unknown).ToString())}";

    internal static ControlScriptTriggerConfig BuildLearnedTrigger(ControlMonitorRecord record, string functionName)
    {
        ArgumentNullException.ThrowIfNull(record);
        var messageType = InferMIDIMessageType(record);
        if (record.MIDIController is { } controller)
        {
            return new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDIControlChange,
                FunctionName = functionName,
                MIDIMessageType = messageType,
                MIDIChannel = record.MIDIChannel,
                MIDIController = controller,
            };
        }

        if (record.MIDINote is { } note)
        {
            return new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDINote,
                FunctionName = functionName,
                MIDIMessageType = messageType is ControlMIDIMessageType.NoteOn or ControlMIDIMessageType.NoteOff ? messageType : null,
                MIDIChannel = record.MIDIChannel,
                MIDINote = note,
            };
        }

        return new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.MIDIMessage,
            FunctionName = functionName,
            MIDIMessageType = messageType == ControlMIDIMessageType.Unknown ? null : messageType,
            MIDIChannel = record.MIDIChannel,
            MIDIValue = ShouldLearnMIDIValue(messageType) ? record.MIDIValue : null,
            MIDIParameter = record.MIDIParameter,
        };
    }

    internal static string BuildLearnedStub(ControlMonitorRecord record, string functionName)
    {
        var description = DescribeMIDIRecord(record);
        return $"{Environment.NewLine}export fun {functionName}(event, context) {{{Environment.NewLine}"
            + $"    // TODO: handle {description}{Environment.NewLine}"
            + $"    // event.value holds the incoming value{Environment.NewLine}"
            + $"}}{Environment.NewLine}";
    }

    private static ControlMIDIMessageType InferMIDIMessageType(ControlMonitorRecord record)
    {
        if (record.MIDIMessageType is { } messageType)
            return messageType;
        if (record.MIDIController is not null)
            return ControlMIDIMessageType.ControlChange;
        if (record.MIDINote is not null)
            return ControlMIDIMessageType.NoteOn;
        return ControlMIDIMessageType.Unknown;
    }

    private static bool ShouldLearnMIDIValue(ControlMIDIMessageType messageType) =>
        messageType is ControlMIDIMessageType.ProgramChange
            or ControlMIDIMessageType.SongSelect
            or ControlMIDIMessageType.MIDITimeCode;

    private static string DescribeMIDIRecord(ControlMonitorRecord record)
    {
        var channel = record.MIDIChannel is { } ch ? $" on channel {ch.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var value = record.MIDIValue is { } v ? $" value {v.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        if (record.MIDIController is { } controller)
            return $"MIDI CC {controller.ToString(CultureInfo.InvariantCulture)}{channel}";
        if (record.MIDINote is { } note)
            return $"MIDI note {note.ToString(CultureInfo.InvariantCulture)}{channel}";
        if (record.MIDIParameter is { } parameter)
            return $"MIDI {(record.MIDIMessageType ?? ControlMIDIMessageType.Unknown)} parameter {parameter.ToString(CultureInfo.InvariantCulture)}{channel}";
        return $"MIDI {(record.MIDIMessageType ?? ControlMIDIMessageType.Unknown)}{channel}{value}";
    }

    private static string SanitizeFunctionSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "MIDI";

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.Length == 0 ? "MIDI" : builder.ToString();
    }

    internal static bool HasExport(string? scriptText, string functionName) =>
        !string.IsNullOrEmpty(scriptText)
        && !string.IsNullOrWhiteSpace(functionName)
        && Regex.IsMatch(scriptText, $@"\bexport\s+fun\s+{Regex.Escape(functionName)}\s*\(");

    private void RefreshMonitor()
    {
        SyncActiveLayerFromSession();

        var buffer = _monitorBuffer;
        if (buffer is null || IsPaused)
            return;

        var cache = _session?.ScriptSession.OSCCache;
        if ((cache?.Version ?? -1) != _lastX32CacheVersion)
            RefreshX32CommandCacheValues(cache);

        var version = buffer.Version;
        if (version == _lastRenderedVersion && !_filterDirty)
            return;

        // Remember the version observed before taking the snapshot. If a producer writes between the two,
        // the next timer tick sees the newer version and reconciles once more; no update can be missed.
        var records = buffer.Records;
        _lastRenderedVersion = version;

        if (IsLearning)
        {
            var capture = FindLearnCapture(records, _learnSinceUtc);
            if (capture is not null)
                ApplyLearnCapture(capture);
        }

        var rebuild = _filterDirty;
        _filterDirty = false;

        var query = ApplyMonitorFilters(
            records,
            new ControlMonitorFilterSettings(
                ErrorsOnly,
                FilterText,
                SelectedMonitorDirection,
                SelectedMonitorProtocol,
                DeviceFilterText));

        var filtered = query.TakeLast(MaxRenderedEntries).ToArray();
        ReconcileMonitorEntries(MonitorEntries, filtered, rebuild);
    }

    /// <summary>
    /// Applies the usual append/drop-oldest monitor update without rebuilding every row. A full rebuild is
    /// retained for filter changes and unusual discontinuities. Internal so the saturated-ring behaviour can
    /// be regression tested without running an Avalonia dispatcher.
    /// </summary>
    internal static void ReconcileMonitorEntries(
        ObservableCollection<ControlMonitorEntryViewModel> entries,
        IReadOnlyList<ControlMonitorRecord> records,
        bool rebuild)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(records);

        if (!rebuild && TryReconcileMonitorTail(entries, records))
            return;

        entries.Clear();
        foreach (var record in records)
            entries.Add(new ControlMonitorEntryViewModel(record));
    }

    private static bool TryReconcileMonitorTail(
        ObservableCollection<ControlMonitorEntryViewModel> entries,
        IReadOnlyList<ControlMonitorRecord> records)
    {
        if (records.Count == 0)
        {
            entries.Clear();
            return true;
        }

        if (entries.Count == 0)
        {
            foreach (var record in records)
                entries.Add(new ControlMonitorEntryViewModel(record));
            return true;
        }

        // A monitor snapshot evolves by removing a prefix and appending a suffix. Locate the new first row
        // in the existing view, then verify the overlap before mutating the observable collection.
        var existingStart = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == records[0].Id)
            {
                existingStart = i;
                break;
            }
        }

        if (existingStart < 0)
            return false;

        var overlap = Math.Min(entries.Count - existingStart, records.Count);
        for (var i = 0; i < overlap; i++)
        {
            if (entries[existingStart + i].Id != records[i].Id)
                return false;
        }

        // If old rows remain after the overlap, the sequences diverged rather than forming a tail update.
        if (existingStart + overlap != entries.Count)
            return false;

        for (var i = 0; i < existingStart; i++)
            entries.RemoveAt(0);
        for (var i = overlap; i < records.Count; i++)
            entries.Add(new ControlMonitorEntryViewModel(records[i]));
        return true;
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

    private static IReadOnlyList<OSCArgument> ParseOSCArgs(string raw)
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
}
