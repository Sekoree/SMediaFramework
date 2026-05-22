using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

public partial class ActionCueBuilderDialogViewModel : ViewModelBase
{
    public string DialogTitle { get; private set; } = Strings.EditActionCueDialogTitle;

    [ObservableProperty]
    private CueActionKind _actionKind = CueActionKind.OscOut;

    [ObservableProperty]
    private ActionEndpoint? _selectedEndpoint;

    [ObservableProperty]
    private string _oscAddress = Strings.OscAddressPlaceholder;

    [ObservableProperty]
    private string _oscArguments = "1";

    [ObservableProperty]
    private CueMidiCommandType _midiCommandType = CueMidiCommandType.NoteOn;

    [ObservableProperty]
    private int _midiChannel = 1;

    [ObservableProperty]
    private int _midiData1 = 60;

    [ObservableProperty]
    private int _midiData2 = 100;

    [ObservableProperty]
    private string? _validationMessage;

    public bool IsOscVisible => ActionKind == CueActionKind.OscOut;
    public bool IsMidiVisible => ActionKind == CueActionKind.MidiOut;

    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();
    public IReadOnlyList<CueMidiCommandType> MidiCommandTypes { get; } = Enum.GetValues<CueMidiCommandType>();

    public ObservableCollection<ActionEndpoint> Endpoints { get; } = new();

    partial void OnActionKindChanged(CueActionKind value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOscVisible));
        OnPropertyChanged(nameof(IsMidiVisible));
    }

    public void Load(
        string cueLabel,
        CueActionKind actionKind,
        string? addressOrMessage,
        Guid? endpointId,
        IEnumerable<ActionEndpoint> endpoints)
    {
        DialogTitle = string.IsNullOrWhiteSpace(cueLabel)
            ? Strings.EditActionCueDialogTitle
            : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.EditActionCueDialogTitleWithLabel, cueLabel.Trim());
        OnPropertyChanged(nameof(DialogTitle));

        Endpoints.Clear();
        foreach (var endpoint in endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Endpoints.Add(endpoint);

        ActionKind = actionKind;
        SelectedEndpoint = endpointId is { } id
            ? Endpoints.FirstOrDefault(e => e.Id == id)
            : Endpoints.FirstOrDefault();

        if (actionKind == CueActionKind.OscOut)
            LoadOsc(addressOrMessage);
        else
            LoadMidi(addressOrMessage);
    }

    public bool TryBuild(out Guid? endpointId, out CueActionKind actionKind, out string commandText, out string? error)
    {
        endpointId = SelectedEndpoint?.Id;
        actionKind = ActionKind;
        commandText = string.Empty;
        error = null;

        if (ActionKind == CueActionKind.OscOut)
        {
            var address = string.IsNullOrWhiteSpace(OscAddress) ? Strings.OscAddressPlaceholder : OscAddress.Trim();
            if (!address.StartsWith('/'))
                address = "/" + address;
            commandText = string.IsNullOrWhiteSpace(OscArguments)
                ? address
                : $"{address} {OscArguments.Trim()}";
            ValidationMessage = null;
            return true;
        }

        var channel = Math.Clamp(MidiChannel, 1, 16);
        var d1 = Math.Clamp(MidiData1, 0, 127);
        var d2 = Math.Clamp(MidiData2, 0, 127);
        commandText = MidiCommandType switch
        {
            CueMidiCommandType.NoteOn => $"ch{channel} noteon {d1} {d2}",
            CueMidiCommandType.NoteOff => $"ch{channel} noteoff {d1} {d2}",
            CueMidiCommandType.ControlChange => $"ch{channel} cc {d1} {d2}",
            CueMidiCommandType.ProgramChange => $"ch{channel} pc {d1}",
            _ => $"ch{channel} noteon {d1} {d2}",
        };
        ValidationMessage = null;
        return true;
    }

    private void LoadOsc(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            OscAddress = Strings.OscAddressPlaceholder;
            OscArguments = "1";
            return;
        }

        var split = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        OscAddress = split[0];
        OscArguments = split.Length > 1 ? split[1] : string.Empty;
    }

    private void LoadMidi(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var tokens = raw.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        var idx = 0;
        if (tokens[idx].StartsWith("ch", StringComparison.OrdinalIgnoreCase))
        {
            var chText = tokens[idx][2..].TrimStart('=');
            if (int.TryParse(chText, out var ch))
                MidiChannel = Math.Clamp(ch, 1, 16);
            idx++;
        }

        if (idx >= tokens.Length)
            return;

        var cmd = tokens[idx++].ToLowerInvariant();
        MidiCommandType = cmd switch
        {
            "noteon" or "on" => CueMidiCommandType.NoteOn,
            "noteoff" or "off" => CueMidiCommandType.NoteOff,
            "cc" or "controlchange" => CueMidiCommandType.ControlChange,
            "pc" or "programchange" => CueMidiCommandType.ProgramChange,
            _ => CueMidiCommandType.NoteOn,
        };

        if (idx < tokens.Length && int.TryParse(tokens[idx], out var d1))
        {
            MidiData1 = Math.Clamp(d1, 0, 127);
            idx++;
        }

        if (idx < tokens.Length && int.TryParse(tokens[idx], out var d2))
            MidiData2 = Math.Clamp(d2, 0, 127);
    }
}
