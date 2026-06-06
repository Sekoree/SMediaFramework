using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

public partial class ActionCueBuilderDialogViewModel : ViewModelBase
{
    private readonly List<ActionEndpoint> _allEndpoints = new();

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
    private string _midiDataText = CueMidiActionMessage.DefaultSysExDataText;

    [ObservableProperty]
    private string? _validationMessage;

    public bool IsOscVisible => ActionKind == CueActionKind.OscOut;
    public bool IsMidiVisible => ActionKind == CueActionKind.MidiOut;

    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();
    public IReadOnlyList<CueMidiCommandType> MidiCommandTypes { get; } = CueMidiActionMessage.CommandTypes;

    public ObservableCollection<ActionEndpoint> Endpoints { get; } = new();

    public bool IsMidiChannelVisible => CueMidiActionMessage.UsesChannel(MidiCommandType);
    public bool IsMidiData1Visible => CueMidiActionMessage.UsesData1(MidiCommandType);
    public bool IsMidiData2Visible => CueMidiActionMessage.UsesData2(MidiCommandType);
    public bool IsMidiDataTextVisible => CueMidiActionMessage.UsesDataText(MidiCommandType);
    public string MidiData1Label => CueMidiActionMessage.Data1Label(MidiCommandType);
    public string MidiData2Label => CueMidiActionMessage.Data2Label(MidiCommandType);
    public int MidiData1Minimum => CueMidiActionMessage.Data1Minimum(MidiCommandType);
    public int MidiData1Maximum => CueMidiActionMessage.Data1Maximum(MidiCommandType);
    public int MidiData2Minimum => CueMidiActionMessage.Data2Minimum(MidiCommandType);
    public int MidiData2Maximum => CueMidiActionMessage.Data2Maximum(MidiCommandType);

    partial void OnActionKindChanged(CueActionKind value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOscVisible));
        OnPropertyChanged(nameof(IsMidiVisible));
        RefreshEndpointChoices(SelectedEndpoint?.Id, selectFirstWhenMissing: true);
    }

    partial void OnMidiCommandTypeChanged(CueMidiCommandType value)
    {
        var defaults = CueMidiActionMessage.Defaults(value);
        MidiData1 = Math.Clamp(MidiData1, CueMidiActionMessage.Data1Minimum(value), CueMidiActionMessage.Data1Maximum(value));
        MidiData2 = Math.Clamp(MidiData2, CueMidiActionMessage.Data2Minimum(value), CueMidiActionMessage.Data2Maximum(value));
        if (!CueMidiActionMessage.UsesData1(value))
            MidiData1 = defaults.Data1;
        if (!CueMidiActionMessage.UsesData2(value))
            MidiData2 = defaults.Data2;
        if (CueMidiActionMessage.UsesDataText(value) && string.IsNullOrWhiteSpace(MidiDataText))
            MidiDataText = defaults.DataText;

        OnPropertyChanged(nameof(IsMidiChannelVisible));
        OnPropertyChanged(nameof(IsMidiData1Visible));
        OnPropertyChanged(nameof(IsMidiData2Visible));
        OnPropertyChanged(nameof(IsMidiDataTextVisible));
        OnPropertyChanged(nameof(MidiData1Label));
        OnPropertyChanged(nameof(MidiData2Label));
        OnPropertyChanged(nameof(MidiData1Minimum));
        OnPropertyChanged(nameof(MidiData1Maximum));
        OnPropertyChanged(nameof(MidiData2Minimum));
        OnPropertyChanged(nameof(MidiData2Maximum));
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

        _allEndpoints.Clear();
        _allEndpoints.AddRange(endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

        ActionKind = actionKind;
        RefreshEndpointChoices(endpointId, selectFirstWhenMissing: true);

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

        commandText = CueMidiActionMessage.BuildCommandText(
            MidiCommandType,
            MidiChannel,
            MidiData1,
            MidiData2,
            MidiDataText);
        ValidationMessage = null;
        return true;
    }

    private void RefreshEndpointChoices(Guid? preferredEndpointId, bool selectFirstWhenMissing)
    {
        var currentId = preferredEndpointId ?? SelectedEndpoint?.Id;
        Endpoints.Clear();
        foreach (var endpoint in _allEndpoints.Where(MatchesActionKind))
            Endpoints.Add(endpoint);

        SelectedEndpoint = currentId is { } id
            ? Endpoints.FirstOrDefault(e => e.Id == id)
            : null;
        if (SelectedEndpoint is null && selectFirstWhenMissing)
            SelectedEndpoint = Endpoints.FirstOrDefault();
    }

    private bool MatchesActionKind(ActionEndpoint endpoint) =>
        ActionKind switch
        {
            CueActionKind.OscOut => endpoint is OscActionEndpoint,
            CueActionKind.MidiOut => endpoint is MidiActionEndpoint,
            _ => false,
        };

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
        var state = CueMidiActionMessage.ParseEditorState(raw);
        MidiCommandType = state.CommandType;
        MidiChannel = state.Channel;
        MidiData1 = state.Data1;
        MidiData2 = state.Data2;
        MidiDataText = state.DataText;
    }
}
