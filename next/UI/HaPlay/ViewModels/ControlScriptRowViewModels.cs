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
        if (trigger.MidiValueMin is not null || trigger.MidiValueMax is not null)
        {
            var minText = trigger.MidiValueMin?.ToString(CultureInfo.InvariantCulture) ?? "*";
            var maxText = trigger.MidiValueMax?.ToString(CultureInfo.InvariantCulture) ?? "*";
            label += $" range{minText}..{maxText}";
        }
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
                nameof(MidiValueMinText),
                nameof(MidiValueMaxText),
                nameof(MidiParameterText),
                nameof(ShowOscAddress),
                nameof(ShowMidiMessageType),
                nameof(ShowMidiChannel),
                nameof(ShowMidiController),
                nameof(ShowMidiNote),
                nameof(ShowMidiValue),
                nameof(ShowMidiValueRange),
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
                nameof(MidiValueMinText),
                nameof(MidiValueMaxText),
                nameof(MidiParameterText),
                nameof(ShowMidiChannel),
                nameof(ShowMidiController),
                nameof(ShowMidiNote),
                nameof(ShowMidiValue),
                nameof(ShowMidiValueRange),
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

    public string MidiValueMinText
    {
        get => FormatOptionalInt(_trigger.MidiValueMin);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiValueMin)
                return;

            Update(_trigger with { MidiValueMin = next }, nameof(MidiValueMinText));
        }
    }

    public string MidiValueMaxText
    {
        get => FormatOptionalInt(_trigger.MidiValueMax);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MidiValueMax)
                return;

            Update(_trigger with { MidiValueMax = next }, nameof(MidiValueMaxText));
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

    public bool ShowMidiValueRange => ShowMidiValue;

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
            MidiValueMin = MidiMessageTypeUsesValue(messageType) ? trigger.MidiValueMin : null,
            MidiValueMax = MidiMessageTypeUsesValue(messageType) ? trigger.MidiValueMax : null,
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

