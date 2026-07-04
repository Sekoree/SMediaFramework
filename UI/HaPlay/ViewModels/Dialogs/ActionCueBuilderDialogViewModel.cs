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
    private CueActionKind _actionKind = CueActionKind.OSCOut;

    [ObservableProperty]
    private ActionEndpoint? _selectedEndpoint;

    [ObservableProperty]
    private string _oSCAddress = Strings.OSCAddressPlaceholder;

    [ObservableProperty]
    private string _oSCArguments = "1";

    [ObservableProperty]
    private CueMIDICommandType _mIDICommandType = CueMIDICommandType.NoteOn;

    [ObservableProperty]
    private int _mIDIChannel = 1;

    [ObservableProperty]
    private int _mIDIData1 = 60;

    [ObservableProperty]
    private int _mIDIData2 = 100;

    [ObservableProperty]
    private string _mIDIDataText = CueMIDIActionMessage.DefaultSysExDataText;

    [ObservableProperty]
    private string? _validationMessage;

    public bool IsOSCVisible => ActionKind == CueActionKind.OSCOut;
    public bool IsMIDIVisible => ActionKind == CueActionKind.MIDIOut;

    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();
    public IReadOnlyList<CueMIDICommandType> MIDICommandTypes { get; } = CueMIDIActionMessage.CommandTypes;

    public ObservableCollection<ActionEndpoint> Endpoints { get; } = new();

    public bool IsMIDIChannelVisible => CueMIDIActionMessage.UsesChannel(MIDICommandType);
    public bool IsMIDIData1Visible => CueMIDIActionMessage.UsesData1(MIDICommandType);
    public bool IsMIDIData2Visible => CueMIDIActionMessage.UsesData2(MIDICommandType);
    public bool IsMIDIDataTextVisible => CueMIDIActionMessage.UsesDataText(MIDICommandType);
    public string MIDIData1Label => CueMIDIActionMessage.Data1Label(MIDICommandType);
    public string MIDIData2Label => CueMIDIActionMessage.Data2Label(MIDICommandType);
    public int MIDIData1Minimum => CueMIDIActionMessage.Data1Minimum(MIDICommandType);
    public int MIDIData1Maximum => CueMIDIActionMessage.Data1Maximum(MIDICommandType);
    public int MIDIData2Minimum => CueMIDIActionMessage.Data2Minimum(MIDICommandType);
    public int MIDIData2Maximum => CueMIDIActionMessage.Data2Maximum(MIDICommandType);

    partial void OnActionKindChanged(CueActionKind value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOSCVisible));
        OnPropertyChanged(nameof(IsMIDIVisible));
        RefreshEndpointChoices(SelectedEndpoint?.Id, selectFirstWhenMissing: true);
    }

    partial void OnMIDICommandTypeChanged(CueMIDICommandType value)
    {
        var defaults = CueMIDIActionMessage.Defaults(value);
        MIDIData1 = Math.Clamp(MIDIData1, CueMIDIActionMessage.Data1Minimum(value), CueMIDIActionMessage.Data1Maximum(value));
        MIDIData2 = Math.Clamp(MIDIData2, CueMIDIActionMessage.Data2Minimum(value), CueMIDIActionMessage.Data2Maximum(value));
        if (!CueMIDIActionMessage.UsesData1(value))
            MIDIData1 = defaults.Data1;
        if (!CueMIDIActionMessage.UsesData2(value))
            MIDIData2 = defaults.Data2;
        if (CueMIDIActionMessage.UsesDataText(value) && string.IsNullOrWhiteSpace(MIDIDataText))
            MIDIDataText = defaults.DataText;

        OnPropertyChanged(nameof(IsMIDIChannelVisible));
        OnPropertyChanged(nameof(IsMIDIData1Visible));
        OnPropertyChanged(nameof(IsMIDIData2Visible));
        OnPropertyChanged(nameof(IsMIDIDataTextVisible));
        OnPropertyChanged(nameof(MIDIData1Label));
        OnPropertyChanged(nameof(MIDIData2Label));
        OnPropertyChanged(nameof(MIDIData1Minimum));
        OnPropertyChanged(nameof(MIDIData1Maximum));
        OnPropertyChanged(nameof(MIDIData2Minimum));
        OnPropertyChanged(nameof(MIDIData2Maximum));
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

        if (actionKind == CueActionKind.OSCOut)
            LoadOSC(addressOrMessage);
        else
            LoadMIDI(addressOrMessage);
    }

    public bool TryBuild(out Guid? endpointId, out CueActionKind actionKind, out string commandText, out string? error)
    {
        endpointId = SelectedEndpoint?.Id;
        actionKind = ActionKind;
        commandText = string.Empty;
        error = null;

        if (ActionKind == CueActionKind.OSCOut)
        {
            var address = string.IsNullOrWhiteSpace(OSCAddress) ? Strings.OSCAddressPlaceholder : OSCAddress.Trim();
            if (!address.StartsWith('/'))
                address = "/" + address;
            commandText = string.IsNullOrWhiteSpace(OSCArguments)
                ? address
                : $"{address} {OSCArguments.Trim()}";
            ValidationMessage = null;
            return true;
        }

        commandText = CueMIDIActionMessage.BuildCommandText(
            MIDICommandType,
            MIDIChannel,
            MIDIData1,
            MIDIData2,
            MIDIDataText);
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
            CueActionKind.OSCOut => endpoint is OSCActionEndpoint,
            CueActionKind.MIDIOut => endpoint is MIDIActionEndpoint,
            _ => false,
        };

    private void LoadOSC(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            OSCAddress = Strings.OSCAddressPlaceholder;
            OSCArguments = "1";
            return;
        }

        var split = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        OSCAddress = split[0];
        OSCArguments = split.Length > 1 ? split[1] : string.Empty;
    }

    private void LoadMIDI(string? raw)
    {
        var state = CueMIDIActionMessage.ParseEditorState(raw);
        MIDICommandType = state.CommandType;
        MIDIChannel = state.Channel;
        MIDIData1 = state.Data1;
        MIDIData2 = state.Data2;
        MIDIDataText = state.DataText;
    }
}
