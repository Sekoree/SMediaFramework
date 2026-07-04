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
        if (!string.IsNullOrWhiteSpace(trigger.OSCAddressPattern))
            label += $" {trigger.OSCAddressPattern}";
        if (trigger.MIDIMessageType is { } messageType)
            label += $" {messageType}";
        if (trigger.MIDIController is { } controller)
            label += $" cc{controller}";
        if (trigger.MIDINote is { } note)
            label += $" note{note}";
        if (trigger.MIDIValue is { } value)
            label += $" value{value}";
        if (trigger.MIDIValueMin is not null || trigger.MIDIValueMax is not null)
        {
            var minText = trigger.MIDIValueMin?.ToString(CultureInfo.InvariantCulture) ?? "*";
            var maxText = trigger.MIDIValueMax?.ToString(CultureInfo.InvariantCulture) ?? "*";
            label += $" range{minText}..{maxText}";
        }
        if (trigger.MIDIParameter is { } parameter)
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
                NormalizeMIDITrigger(_trigger with { Kind = value }),
                nameof(Kind),
                nameof(MIDIMessageType),
                nameof(MIDIMessageTypeOptions),
                nameof(MIDIChannelText),
                nameof(MIDIControllerText),
                nameof(MIDINoteText),
                nameof(MIDIValueText),
                nameof(MIDIValueMinText),
                nameof(MIDIValueMaxText),
                nameof(MIDIParameterText),
                nameof(ShowOSCAddress),
                nameof(ShowMIDIMessageType),
                nameof(ShowMIDIChannel),
                nameof(ShowMIDIController),
                nameof(ShowMIDINote),
                nameof(ShowMIDIValue),
                nameof(ShowMIDIValueRange),
                nameof(ShowMIDIParameter),
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

    public string OSCAddressPattern
    {
        get => _trigger.OSCAddressPattern ?? string.Empty;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (next == _trigger.OSCAddressPattern)
                return;

            Update(_trigger with { OSCAddressPattern = next }, nameof(OSCAddressPattern));
        }
    }

    public IReadOnlyList<ControlMIDIMessageType?> MIDIMessageTypeOptions =>
        Kind switch
        {
            ControlScriptTriggerKind.MIDINote => [null, ControlMIDIMessageType.NoteOn, ControlMIDIMessageType.NoteOff],
            ControlScriptTriggerKind.MIDIControlChange => [null, ControlMIDIMessageType.ControlChange],
            _ => AllMIDIMessageTypeOptions,
        };

    public ControlMIDIMessageType? MIDIMessageType
    {
        get => _trigger.MIDIMessageType;
        set
        {
            if (value == _trigger.MIDIMessageType)
                return;

            Update(
                NormalizeMIDITrigger(_trigger with { MIDIMessageType = value }),
                nameof(MIDIMessageType),
                nameof(MIDIChannelText),
                nameof(MIDIControllerText),
                nameof(MIDINoteText),
                nameof(MIDIValueText),
                nameof(MIDIValueMinText),
                nameof(MIDIValueMaxText),
                nameof(MIDIParameterText),
                nameof(ShowMIDIChannel),
                nameof(ShowMIDIController),
                nameof(ShowMIDINote),
                nameof(ShowMIDIValue),
                nameof(ShowMIDIValueRange),
                nameof(ShowMIDIParameter));
        }
    }

    public string MIDIChannelText
    {
        get => FormatOptionalInt(_trigger.MIDIChannel);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIChannel)
                return;

            Update(_trigger with { MIDIChannel = next }, nameof(MIDIChannelText));
        }
    }

    public string MIDIControllerText
    {
        get => FormatOptionalInt(_trigger.MIDIController);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIController)
                return;

            Update(_trigger with { MIDIController = next }, nameof(MIDIControllerText));
        }
    }

    public string MIDINoteText
    {
        get => FormatOptionalInt(_trigger.MIDINote);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDINote)
                return;

            Update(_trigger with { MIDINote = next }, nameof(MIDINoteText));
        }
    }

    public string MIDIValueText
    {
        get => FormatOptionalInt(_trigger.MIDIValue);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIValue)
                return;

            Update(_trigger with { MIDIValue = next }, nameof(MIDIValueText));
        }
    }

    public string MIDIValueMinText
    {
        get => FormatOptionalInt(_trigger.MIDIValueMin);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIValueMin)
                return;

            Update(_trigger with { MIDIValueMin = next }, nameof(MIDIValueMinText));
        }
    }

    public string MIDIValueMaxText
    {
        get => FormatOptionalInt(_trigger.MIDIValueMax);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIValueMax)
                return;

            Update(_trigger with { MIDIValueMax = next }, nameof(MIDIValueMaxText));
        }
    }

    public string MIDIParameterText
    {
        get => FormatOptionalInt(_trigger.MIDIParameter);
        set
        {
            var next = ParseOptionalInt(value);
            if (next == _trigger.MIDIParameter)
                return;

            Update(_trigger with { MIDIParameter = next }, nameof(MIDIParameterText));
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

    public bool ShowOSCAddress => Kind is ControlScriptTriggerKind.OSCMessage or ControlScriptTriggerKind.OSCCacheChanged;

    public bool ShowMIDIMessageType => Kind is ControlScriptTriggerKind.MIDIMessage or ControlScriptTriggerKind.MIDINote;

    public bool ShowMIDIChannel => Kind is ControlScriptTriggerKind.MIDIControlChange
        or ControlScriptTriggerKind.MIDINote
        || (Kind == ControlScriptTriggerKind.MIDIMessage && MIDIMessageTypeUsesChannel(_trigger.MIDIMessageType));

    public bool ShowMIDIController => Kind == ControlScriptTriggerKind.MIDIControlChange
        || (Kind == ControlScriptTriggerKind.MIDIMessage && MIDIMessageTypeUsesController(_trigger.MIDIMessageType));

    public bool ShowMIDINote => Kind == ControlScriptTriggerKind.MIDINote
        || (Kind == ControlScriptTriggerKind.MIDIMessage && MIDIMessageTypeUsesNote(_trigger.MIDIMessageType));

    public bool ShowMIDIValue => Kind is ControlScriptTriggerKind.MIDIControlChange
        or ControlScriptTriggerKind.MIDINote
        || (Kind == ControlScriptTriggerKind.MIDIMessage && MIDIMessageTypeUsesValue(_trigger.MIDIMessageType));

    public bool ShowMIDIValueRange => ShowMIDIValue;

    public bool ShowMIDIParameter => Kind == ControlScriptTriggerKind.MIDIMessage
        && MIDIMessageTypeUsesParameter(_trigger.MIDIMessageType);

    public bool ShowInterval => Kind is ControlScriptTriggerKind.Periodic;

    private bool IsMIDIKind => Kind is ControlScriptTriggerKind.MIDIMessage
        or ControlScriptTriggerKind.MIDIControlChange
        or ControlScriptTriggerKind.MIDINote;

    private static readonly IReadOnlyList<ControlMIDIMessageType?> AllMIDIMessageTypeOptions =
        new ControlMIDIMessageType?[] { null }
            .Concat(Enum.GetValues<ControlMIDIMessageType>()
                .Where(t => t != ControlMIDIMessageType.Unknown)
                .Select(t => (ControlMIDIMessageType?)t))
            .ToArray();

    private static ControlScriptTriggerConfig NormalizeMIDITrigger(ControlScriptTriggerConfig trigger)
    {
        if (trigger.Kind == ControlScriptTriggerKind.MIDIControlChange)
        {
            return trigger with
            {
                MIDIMessageType = trigger.MIDIMessageType is null or ControlMIDIMessageType.ControlChange
                    ? trigger.MIDIMessageType
                    : null,
                MIDINote = null,
                MIDIParameter = null,
            };
        }

        if (trigger.Kind == ControlScriptTriggerKind.MIDINote)
        {
            return trigger with
            {
                MIDIMessageType = trigger.MIDIMessageType is null
                    or ControlMIDIMessageType.NoteOn
                    or ControlMIDIMessageType.NoteOff
                        ? trigger.MIDIMessageType
                        : null,
                MIDIController = null,
                MIDIParameter = null,
            };
        }

        if (trigger.Kind != ControlScriptTriggerKind.MIDIMessage || trigger.MIDIMessageType is not { } messageType)
            return trigger;

        return trigger with
        {
            MIDIChannel = MIDIMessageTypeUsesChannel(messageType) ? trigger.MIDIChannel : null,
            MIDIController = MIDIMessageTypeUsesController(messageType) ? trigger.MIDIController : null,
            MIDINote = MIDIMessageTypeUsesNote(messageType) ? trigger.MIDINote : null,
            MIDIValue = MIDIMessageTypeUsesValue(messageType) ? trigger.MIDIValue : null,
            MIDIValueMin = MIDIMessageTypeUsesValue(messageType) ? trigger.MIDIValueMin : null,
            MIDIValueMax = MIDIMessageTypeUsesValue(messageType) ? trigger.MIDIValueMax : null,
            MIDIParameter = MIDIMessageTypeUsesParameter(messageType) ? trigger.MIDIParameter : null,
        };
    }

    private static bool MIDIMessageTypeUsesChannel(ControlMIDIMessageType? messageType) =>
        messageType is null
            or ControlMIDIMessageType.NRPN
            or ControlMIDIMessageType.RPN
            or ControlMIDIMessageType.NoteOff
            or ControlMIDIMessageType.NoteOn
            or ControlMIDIMessageType.PolyphonicAftertouch
            or ControlMIDIMessageType.ControlChange
            or ControlMIDIMessageType.ProgramChange
            or ControlMIDIMessageType.ChannelAftertouch
            or ControlMIDIMessageType.PitchBend;

    private static bool MIDIMessageTypeUsesController(ControlMIDIMessageType? messageType) =>
        messageType is null or ControlMIDIMessageType.ControlChange;

    private static bool MIDIMessageTypeUsesNote(ControlMIDIMessageType? messageType) =>
        messageType is null
            or ControlMIDIMessageType.NoteOff
            or ControlMIDIMessageType.NoteOn
            or ControlMIDIMessageType.PolyphonicAftertouch;

    private static bool MIDIMessageTypeUsesValue(ControlMIDIMessageType? messageType) =>
        messageType is null
            or ControlMIDIMessageType.NRPN
            or ControlMIDIMessageType.RPN
            or ControlMIDIMessageType.NoteOff
            or ControlMIDIMessageType.NoteOn
            or ControlMIDIMessageType.PolyphonicAftertouch
            or ControlMIDIMessageType.ControlChange
            or ControlMIDIMessageType.ProgramChange
            or ControlMIDIMessageType.ChannelAftertouch
            or ControlMIDIMessageType.PitchBend
            or ControlMIDIMessageType.SysEx
            or ControlMIDIMessageType.MIDITimeCode
            or ControlMIDIMessageType.SongPosition
            or ControlMIDIMessageType.SongSelect;

    private static bool MIDIMessageTypeUsesParameter(ControlMIDIMessageType? messageType) =>
        messageType is null or ControlMIDIMessageType.NRPN or ControlMIDIMessageType.RPN;

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

