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
using HaPlay.Resources;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

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
        var initialRange = BuildInitialRange(record);
        _minimumValue = initialRange.Min;
        _maximumValue = initialRange.Max;
        if (record.MIDIValue is { } value)
            _observedValues.Add(value);
    }

    public ControlMonitorRecord Record { get; }

    public string Description { get; }

    public bool HasValueRange =>
        (MinimumValue.HasValue || MaximumValue.HasValue)
        && (_rangeEdited || _observedValues.Count > 1);

    public int? MinimumValue => _minimumValue;

    public int? MaximumValue => _maximumValue;

    public string MinimumValueText
    {
        get => FormatOptionalInt(_minimumValue);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _minimumValue)
                return;

            _rangeEdited = true;
            _minimumValue = next;
            OnRangeChanged();
        }
    }

    public string MaximumValueText
    {
        get => FormatOptionalInt(_maximumValue);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _maximumValue)
                return;

            _rangeEdited = true;
            _maximumValue = next;
            OnRangeChanged();
        }
    }

    public string RangeDescription =>
        HasValueRange
            ? $"range {FormatRangeValue(_minimumValue)}..{FormatRangeValue(_maximumValue)}"
            : "no value range";

    [ObservableProperty]
    private string _functionName;

    [ObservableProperty]
    private bool _insertStub = true;

    private int? _minimumValue;
    private int? _maximumValue;
    private bool _rangeEdited;
    private readonly HashSet<int> _observedValues = new();

    public bool TryObserve(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Matches(record))
            return false;

        if (record.MIDIValue is { } value)
        {
            _observedValues.Add(value);
            _minimumValue = _minimumValue.HasValue ? Math.Min(_minimumValue.Value, value) : value;
            _maximumValue = _maximumValue.HasValue ? Math.Max(_maximumValue.Value, value) : value;
            OnRangeChanged();
        }

        return true;
    }

    public bool Matches(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return InferMessageType(Record) == InferMessageType(record)
            && SameNullable(Record.MIDIChannel, record.MIDIChannel)
            && SameNullable(Record.MIDIController, record.MIDIController)
            && SameNullable(Record.MIDINote, record.MIDINote)
            && SameNullable(Record.MIDIParameter, record.MIDIParameter)
            && SameDevice(Record.DeviceInstanceId, record.DeviceInstanceId);
    }

    private static (int? Min, int? Max) BuildInitialRange(ControlMonitorRecord record)
    {
        if (record.MIDIValue is not { } value)
            return (null, null);

        if (record.MIDIController is not null)
            return (0, value);
        if (record.MIDINote is not null)
            return (0, 127);

        var messageType = record.MIDIMessageType ?? ControlMIDIMessageType.Unknown;
        return messageType switch
        {
            ControlMIDIMessageType.ControlChange => (0, value),
            ControlMIDIMessageType.NoteOn or ControlMIDIMessageType.NoteOff or ControlMIDIMessageType.PolyphonicAftertouch => (0, 127),
            ControlMIDIMessageType.ChannelAftertouch => (0, 127),
            ControlMIDIMessageType.PitchBend => (0, Math.Max(value, 16383)),
            _ => (value, value),
        };
    }

    private static bool SameNullable<T>(T? left, T? right) where T : struct =>
        EqualityComparer<T?>.Default.Equals(left, right);

    private static bool SameNullable(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool SameDevice(Guid? left, Guid? right) =>
        !left.HasValue || !right.HasValue || left.Value == right.Value;

    private static ControlMIDIMessageType InferMessageType(ControlMonitorRecord record)
    {
        if (record.MIDIMessageType is { } messageType)
            return messageType;
        if (record.MIDIController is not null)
            return ControlMIDIMessageType.ControlChange;
        if (record.MIDINote is not null)
            return ControlMIDIMessageType.NoteOn;
        return ControlMIDIMessageType.Unknown;
    }

    private void OnRangeChanged()
    {
        OnPropertyChanged(nameof(MinimumValue));
        OnPropertyChanged(nameof(MaximumValue));
        OnPropertyChanged(nameof(MinimumValueText));
        OnPropertyChanged(nameof(MaximumValueText));
        OnPropertyChanged(nameof(HasValueRange));
        OnPropertyChanged(nameof(RangeDescription));
    }

    private static string BuildDescription(ControlMonitorRecord record)
    {
        var channel = record.MIDIChannel is { } ch ? $" ch {ch.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var value = record.MIDIValue is { } v ? $" (value {v.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        if (record.MIDIController is { } controller)
            return $"CC {controller.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        if (record.MIDINote is { } note)
            return $"Note {note.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        if (record.MIDIParameter is { } parameter)
            return $"{(record.MIDIMessageType ?? ControlMIDIMessageType.Unknown)} param {parameter.ToString(CultureInfo.InvariantCulture)}{channel}{value}";
        return $"{(record.MIDIMessageType ?? ControlMIDIMessageType.Unknown)}{channel}{value}";
    }

    private static string FormatOptionalInt(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatRangeValue(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "*";

    private static int? ParseOptionalInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
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
