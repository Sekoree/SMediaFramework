using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.Playback;

namespace HaPlay.ViewModels;

public enum CueNodeKind
{
    Group,
    Media,
    Action,
    Comment,
}

public enum CueMidiCommandType
{
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
}

public sealed partial class CueVirtualOutputChannelViewModel : ObservableObject
{
    [ObservableProperty]
    private int _channel;

    [ObservableProperty]
    private string _label = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"VOut {Channel}" : $"VOut {Channel} - {Label}";

    partial void OnChannelChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnLabelChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    public CueVirtualOutputChannel ToModel() => new()
    {
        Channel = Channel,
        Label = Label,
    };

    public static CueVirtualOutputChannelViewModel FromModel(CueVirtualOutputChannel model) => new()
    {
        Channel = model.Channel,
        Label = model.Label,
    };
}

public sealed partial class CueRouteConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    private int _inputChannel;

    [ObservableProperty]
    private int _virtualOutputChannel = 1;

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private bool _muted;

    public CueRouteConnectionOverride ToModel() => new()
    {
        InputChannel = InputChannel,
        VirtualOutputChannel = VirtualOutputChannel,
        GainDb = GainDb,
        Muted = Muted,
    };

    public static CueRouteConnectionViewModel FromModel(CueRouteConnectionOverride model) => new()
    {
        InputChannel = model.InputChannel,
        VirtualOutputChannel = model.VirtualOutputChannel,
        GainDb = model.GainDb,
        Muted = model.Muted,
    };
}

public sealed partial class CueNodeViewModel : ObservableObject
{
    public CueNodeViewModel(CueNodeKind kind)
    {
        Kind = kind;
        Children.CollectionChanged += OnChildrenCollectionChanged;
        RouteConnections.CollectionChanged += OnRouteConnectionsCollectionChanged;
        VirtualOutputChannels.CollectionChanged += OnVirtualOutputsCollectionChanged;
    }

    public CueNodeKind Kind { get; }

    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    public ObservableCollection<CueNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _number = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private CueTriggerMode _triggerMode = CueTriggerMode.Manual;

    [ObservableProperty]
    private int _preWaitMs;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private string _sourceOrAction = string.Empty;

    /// <summary>Canonical media source for <see cref="CueNodeKind.Media"/> rows (files and live inputs).</summary>
    [ObservableProperty]
    private PlaylistItem? _mediaSourceItem;

    [ObservableProperty]
    private int _fadeInMs;

    [ObservableProperty]
    private int _fadeOutMs;

    [ObservableProperty]
    private int _startOffsetMs;

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    private CueEndBehavior _endBehavior = CueEndBehavior.Stop;

    [ObservableProperty]
    private string _endpointIdText = string.Empty;

    [ObservableProperty]
    private bool _isEndpointBroken;

    [ObservableProperty]
    private string _extra = string.Empty;

    public CueGroupFireMode GroupFireMode
    {
        get => Enum.TryParse<CueGroupFireMode>(Extra, out var mode) ? mode : CueGroupFireMode.FirstCueOnly;
        set => Extra = value.ToString();
    }

    public CueActionKind ActionKind
    {
        get => Enum.TryParse<CueActionKind>(Extra, out var kind) ? kind : CueActionKind.OscOut;
        set => Extra = value.ToString();
    }

    public ObservableCollection<int> VirtualOutputChannels { get; } = new();

    public ObservableCollection<CueRouteConnectionViewModel> RouteConnections { get; } = new();

    public bool SupportsRouteConnections => Kind == CueNodeKind.Media;

    public bool IsGroup => Kind == CueNodeKind.Group;

    public bool HasChildren => Children.Count > 0;

    public string KindLabel => Kind switch
    {
        CueNodeKind.Group => "Group",
        CueNodeKind.Media => "Media",
        CueNodeKind.Action => "Action",
        CueNodeKind.Comment => "Comment",
        _ => "Cue",
    };

    partial void OnExtraChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(GroupFireMode));
        OnPropertyChanged(nameof(ActionKind));
    }

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(HasChildren));
    }

    private void OnVirtualOutputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateMediaExtraSummary();
    }

    private void OnRouteConnectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateMediaExtraSummary();
        RouteConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when <see cref="RouteConnections"/> mutates so hosts can refresh route grids.</summary>
    public event EventHandler? RouteConnectionsChanged;

    private void UpdateMediaExtraSummary()
    {
        if (Kind != CueNodeKind.Media)
            return;
        Extra = RouteConnections.Count > 0
            ? $"{RouteConnections.Count} routes"
            : (VirtualOutputChannels.Count > 0 ? $"VOut {string.Join(",", VirtualOutputChannels)}" : string.Empty);
    }

    public static CueNodeViewModel FromModel(CueNode node)
    {
        switch (node)
        {
            case CueGroupNode g:
            {
                var vm = new CueNodeViewModel(CueNodeKind.Group)
                {
                    Id = g.Id,
                    Number = g.Number,
                    Label = g.Label,
                    TriggerMode = g.TriggerMode,
                    PreWaitMs = g.PreWaitMs,
                    Notes = g.Notes,
                    Extra = g.FireMode.ToString(),
                };
                foreach (var c in g.Children)
                    vm.Children.Add(FromModel(c));
                return vm;
            }
            case MediaCueNode m:
            {
                var vm = new CueNodeViewModel(CueNodeKind.Media)
                {
                    Id = m.Id,
                    Number = m.Number,
                    Label = m.Label,
                    TriggerMode = m.TriggerMode,
                    PreWaitMs = m.PreWaitMs,
                    Notes = m.Notes,
                    MediaSourceItem = m.Source,
                    SourceOrAction = m.Source?.DisplayName ?? string.Empty,
                    FadeInMs = m.FadeInMs,
                    FadeOutMs = m.FadeOutMs,
                    StartOffsetMs = m.StartOffsetMs,
                    Loop = m.Loop,
                    EndBehavior = m.EndBehavior,
                };
                foreach (var v in m.VirtualOutputChannels.Distinct().OrderBy(x => x))
                    vm.VirtualOutputChannels.Add(v);
                foreach (var route in m.RouteConnections)
                    vm.RouteConnections.Add(CueRouteConnectionViewModel.FromModel(route));
                vm.UpdateMediaExtraSummary();
                return vm;
            }
            case ActionCueNode a:
                return new CueNodeViewModel(CueNodeKind.Action)
                {
                    Id = a.Id,
                    Number = a.Number,
                    Label = a.Label,
                    TriggerMode = a.TriggerMode,
                    PreWaitMs = a.PreWaitMs,
                    Notes = a.Notes,
                    SourceOrAction = a.AddressOrMessage,
                    EndpointIdText = a.EndpointId?.ToString() ?? string.Empty,
                    Extra = a.ActionKind.ToString(),
                };
            case CommentCueNode c:
                return new CueNodeViewModel(CueNodeKind.Comment)
                {
                    Id = c.Id,
                    Number = c.Number,
                    Label = c.Label,
                    TriggerMode = c.TriggerMode,
                    PreWaitMs = c.PreWaitMs,
                    Notes = c.Notes,
                    SourceOrAction = c.Text,
                };
            default:
                return new CueNodeViewModel(CueNodeKind.Comment) { Label = "Unsupported cue node" };
        }
    }

    public CueNode ToModel()
    {
        return Kind switch
        {
            CueNodeKind.Group => new CueGroupNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                FireMode = Enum.TryParse<CueGroupFireMode>(Extra, out var fm) ? fm : CueGroupFireMode.FirstCueOnly,
                Children = Children.Select(c => c.ToModel()).ToList(),
            },
            CueNodeKind.Media => new MediaCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                Source = MediaSourceItem
                           ?? (string.IsNullOrWhiteSpace(SourceOrAction)
                               ? null
                               : new FilePlaylistItem(SourceOrAction)),
                FadeInMs = Math.Max(0, FadeInMs),
                FadeOutMs = Math.Max(0, FadeOutMs),
                StartOffsetMs = Math.Max(0, StartOffsetMs),
                Loop = Loop,
                EndBehavior = EndBehavior,
                VirtualOutputChannels = VirtualOutputChannels.Distinct().OrderBy(x => x).ToList(),
                RouteConnections = RouteConnections.Select(x => x.ToModel()).ToList(),
            },
            CueNodeKind.Action => new ActionCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                AddressOrMessage = SourceOrAction,
                EndpointId = Guid.TryParse(EndpointIdText, out var endpointId) ? endpointId : null,
                ActionKind = Enum.TryParse<CueActionKind>(Extra, out var ak) ? ak : CueActionKind.OscOut,
            },
            _ => new CommentCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                Text = SourceOrAction,
            },
        };
    }
}

public sealed partial class CueListEditorViewModel : ObservableObject
{
    public CueListEditorViewModel(string name)
    {
        Name = name;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _path;

    public ObservableCollection<CueVirtualOutputChannelViewModel> VirtualOutputs { get; } = new();

    public ObservableCollection<CueNodeViewModel> Nodes { get; } = new();

    public CueList ToModel() => new()
    {
        Name = Name,
        PreRollCount = PreRollCount,
        VirtualOutputs = VirtualOutputs.Select(v => v.ToModel()).ToList(),
        Nodes = Nodes.Select(n => n.ToModel()).ToList(),
    };

    [ObservableProperty]
    private int _preRollCount = 4;

    public static CueListEditorViewModel FromModel(CueList list, string? path = null)
    {
        var vm = new CueListEditorViewModel(list.Name)
        {
            Path = path,
            PreRollCount = list.PreRollCount > 0 ? list.PreRollCount : 4,
        };
        if (list.VirtualOutputs.Count == 0)
        {
            vm.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 1, Label = "Main L" });
            vm.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 2, Label = "Main R" });
        }
        else
        {
            foreach (var output in list.VirtualOutputs.OrderBy(x => x.Channel))
                vm.VirtualOutputs.Add(CueVirtualOutputChannelViewModel.FromModel(output));
        }
        foreach (var node in list.Nodes)
            vm.Nodes.Add(CueNodeViewModel.FromModel(node));
        return vm;
    }
}

public partial class CuePlayerViewModel : ViewModelBase
{
    private CancellationTokenSource? _transportRunCts;
    private CueNodeViewModel? _watchedRouteMediaCue;
    private CueListEditorViewModel? _watchedCueList;

    /// <summary>
    /// Host-provided media execution callback. When null, media cues only update transport state.
    /// </summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueExecutor { get; set; }

    /// <summary>
    /// Host-provided action execution callback. When null, action cues only update transport state.
    /// </summary>
    public Func<ActionCueNode, CancellationToken, Task<string?>>? ActionCueExecutor { get; set; }

    public CuePlayerViewModel()
    {
        var initial = new CueListEditorViewModel("Cue List 1");
        initial.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 1, Label = "Main L" });
        initial.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 2, Label = "Main R" });
        CueLists.Add(initial);
        SelectedCueList = initial;
    }

    public ObservableCollection<CueListEditorViewModel> CueLists { get; } = new();

    public IReadOnlyList<CueEndBehavior> CueEndBehaviors { get; } = Enum.GetValues<CueEndBehavior>();

    [ObservableProperty]
    private CueListEditorViewModel? _selectedCueList;

    [ObservableProperty]
    private CueNodeViewModel? _selectedCueNode;

    [ObservableProperty]
    private CueRouteConnectionViewModel? _selectedRouteConnection;

    [ObservableProperty]
    private CueVirtualOutputChannelViewModel? _selectedVirtualOutput;

    [ObservableProperty]
    private ActionEndpoint? _selectedActionEndpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private CueNodeViewModel? _standbyCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private CueNodeViewModel? _currentCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private bool _isTransportPaused;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private CueActionKind _builderActionKind = CueActionKind.OscOut;

    [ObservableProperty]
    private string _oscBuilderAddress = "/address";

    [ObservableProperty]
    private string _oscBuilderArguments = "1";

    [ObservableProperty]
    private CueMidiCommandType _midiBuilderCommandType = CueMidiCommandType.NoteOn;

    [ObservableProperty]
    private int _midiBuilderChannel = 1;

    [ObservableProperty]
    private int _midiBuilderData1 = 60;

    [ObservableProperty]
    private int _midiBuilderData2 = 100;

    public bool IsOscBuilderVisible => BuilderActionKind == CueActionKind.OscOut;

    public bool IsMidiBuilderVisible => BuilderActionKind == CueActionKind.MidiOut;

    public ObservableCollection<CueNodeViewModel> VisibleNodes =>
        SelectedCueList?.Nodes ?? _emptyNodes;

    private readonly ObservableCollection<CueNodeViewModel> _emptyNodes = new();
    private readonly ObservableCollection<CueRouteConnectionViewModel> _emptyRoutes = new();
    private readonly ObservableCollection<CueVirtualOutputChannelViewModel> _emptyVirtualOutputs = new();
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    public ObservableCollection<CueRouteConnectionViewModel> VisibleRouteConnections =>
        SelectedCueNode is { Kind: CueNodeKind.Media } node ? node.RouteConnections : _emptyRoutes;

    public ObservableCollection<CueVirtualOutputChannelViewModel> VisibleVirtualOutputs =>
        SelectedCueList?.VirtualOutputs ?? _emptyVirtualOutputs;

    public bool HasSelectedMediaCue => SelectedCueNode?.Kind == CueNodeKind.Media;
    public IReadOnlyList<CueActionKind> BuilderActionKinds { get; } = Enum.GetValues<CueActionKind>();
    public IReadOnlyList<CueMidiCommandType> MidiBuilderCommandTypes { get; } = Enum.GetValues<CueMidiCommandType>();

    public string TransportState =>
        CurrentCueNode is null
            ? $"Standby: {(StandbyCueNode is null ? "(none)" : CueDisplay(StandbyCueNode))}"
            : $"{(IsTransportPaused ? "Paused" : "Running")}: {CueDisplay(CurrentCueNode)}"
              + (StandbyCueNode is null ? string.Empty : $" | Next: {CueDisplay(StandbyCueNode)}");

    partial void OnSelectedCueListChanged(CueListEditorViewModel? value)
    {
        UnwatchCueListVirtualOutputs();
        _watchedCueList = value;
        if (_watchedCueList is not null)
            _watchedCueList.VirtualOutputs.CollectionChanged += OnWatchedVirtualOutputsCollectionChanged;

        CancelTransportRun();
        OnPropertyChanged(nameof(VisibleNodes));
        NotifyVisibleVirtualOutputsChanged();
        SelectedVirtualOutput = value?.VirtualOutputs.FirstOrDefault();
        SelectedCueNode = null;
        SelectedRouteConnection = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RemoveCueListCommand.NotifyCanExecuteChanged();
        RemoveVirtualOutputCommand.NotifyCanExecuteChanged();
        AddRouteConnectionCommand.NotifyCanExecuteChanged();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        UnwatchRouteConnections();
        if (value is { Kind: CueNodeKind.Media } media)
        {
            _watchedRouteMediaCue = media;
            media.RouteConnectionsChanged += OnWatchedRouteConnectionsChanged;
        }

        SelectedRouteConnection = value is { Kind: CueNodeKind.Media } selectedMedia
            ? selectedMedia.RouteConnections.FirstOrDefault()
            : null;
        RemoveNodeCommand.NotifyCanExecuteChanged();
        NotifyVisibleRouteConnectionsChanged();
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        AddRouteConnectionCommand.NotifyCanExecuteChanged();
        RemoveRouteConnectionCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        BrowseMediaSourceCommand.NotifyCanExecuteChanged();
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
        ApplyActionBuilderCommand.NotifyCanExecuteChanged();
        EditActionCueCommand.NotifyCanExecuteChanged();

        if (value?.Kind == CueNodeKind.Action && Guid.TryParse(value.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        else
            SelectedActionEndpoint = null;

        SyncActionBuilderFromCue(value);
    }

    partial void OnSelectedRouteConnectionChanged(CueRouteConnectionViewModel? value)
    {
        _ = value;
        RemoveRouteConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVirtualOutputChanged(CueVirtualOutputChannelViewModel? value)
    {
        _ = value;
        RemoveVirtualOutputCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        _ = value;
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
    }

    partial void OnBuilderActionKindChanged(CueActionKind value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOscBuilderVisible));
        OnPropertyChanged(nameof(IsMidiBuilderVisible));
    }

    partial void OnStandbyCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        PreRollRefreshSuggested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Host subscribes to warm the selected player's pre-roll cache (§5.7).</summary>
    public event EventHandler? PreRollRefreshSuggested;

    /// <summary>Next <paramref name="maxCount"/> file media cues from standby (or list start).</summary>
    public IReadOnlyList<(Guid CueId, PlaylistItem Item, int FadeInMs, int FadeOutMs)> GetPreRollTargets(int maxCount)
    {
        if (maxCount <= 0 || SelectedCueList is null)
            return [];

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return [];

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        var targets = new List<(Guid, PlaylistItem, int, int)>();
        for (var i = startIdx; i < ordered.Count && targets.Count < maxCount; i++)
        {
            var cue = ordered[i];
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not { } source
                || !source.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, source, Math.Max(0, cue.FadeInMs), Math.Max(0, cue.FadeOutMs)));
        }

        return targets;
    }

    /// <summary>NDI media cues in the pre-roll window (§6.11).</summary>
    public IReadOnlyList<(Guid CueId, NDIInputPlaylistItem Item)> GetNdiPreConnectTargets(int maxCount)
    {
        if (maxCount <= 0 || SelectedCueList is null)
            return [];

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return [];

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        var targets = new List<(Guid, NDIInputPlaylistItem)>();
        for (var i = startIdx; i < ordered.Count && targets.Count < maxCount; i++)
        {
            var cue = ordered[i];
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not NDIInputPlaylistItem ndi
                || !ndi.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, ndi));
        }

        return targets;
    }

    partial void OnCurrentCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        PauseCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTransportPausedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TransportState));
    }

    private ICollection<CueNodeViewModel>? SelectedParentCollection()
    {
        if (SelectedCueList is null)
            return null;
        if (SelectedCueNode is null)
            return SelectedCueList.Nodes;
        if (SelectedCueNode.IsGroup)
            return SelectedCueNode.Children;
        return FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) ?? SelectedCueList.Nodes;
    }

    private static ICollection<CueNodeViewModel>? FindParentCollection(
        ICollection<CueNodeViewModel> nodes,
        CueNodeViewModel target)
    {
        if (nodes.Contains(target))
            return nodes;
        foreach (var n in nodes)
        {
            var c = FindParentCollection(n.Children, target);
            if (c is not null) return c;
        }
        return null;
    }

    private static bool RemoveNodeRecursive(ICollection<CueNodeViewModel> nodes, CueNodeViewModel target)
    {
        if (nodes.Remove(target))
            return true;
        foreach (var n in nodes)
            if (RemoveNodeRecursive(n.Children, target))
                return true;
        return false;
    }

    [RelayCommand]
    private void AddCueList()
    {
        var list = new CueListEditorViewModel($"Cue List {CueLists.Count + 1}");
        list.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 1, Label = "Main L" });
        list.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 2, Label = "Main R" });
        CueLists.Add(list);
        SelectedCueList = list;
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCueList))]
    private void RemoveCueList()
    {
        if (SelectedCueList is null || CueLists.Count <= 1)
            return;
        var idx = CueLists.IndexOf(SelectedCueList);
        CueLists.RemoveAt(idx);
        SelectedCueList = CueLists[Math.Clamp(idx - 1, 0, CueLists.Count - 1)];
        SelectedCueNode = null;
    }

    private bool CanRemoveCueList() => SelectedCueList is not null && CueLists.Count > 1;

    [RelayCommand]
    private void AddVirtualOutput()
    {
        if (SelectedCueList is null)
            return;
        var next = SelectedCueList.VirtualOutputs.Count == 0
            ? 1
            : SelectedCueList.VirtualOutputs.Max(x => x.Channel) + 1;
        var vm = new CueVirtualOutputChannelViewModel
        {
            Channel = next,
            Label = next == 1 ? "Main L" : next == 2 ? "Main R" : string.Empty,
        };
        SelectedCueList.VirtualOutputs.Add(vm);
        SelectedVirtualOutput = vm;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVirtualOutput))]
    private void RemoveVirtualOutput()
    {
        if (SelectedCueList is null || SelectedVirtualOutput is null)
            return;
        var removedChannel = SelectedVirtualOutput.Channel;
        if (!SelectedCueList.VirtualOutputs.Remove(SelectedVirtualOutput))
            return;
        foreach (var media in EnumerateMediaNodes(SelectedCueList.Nodes))
        {
            while (media.VirtualOutputChannels.Remove(removedChannel))
            {
            }
            for (var i = media.RouteConnections.Count - 1; i >= 0; i--)
                if (media.RouteConnections[i].VirtualOutputChannel == removedChannel)
                    media.RouteConnections.RemoveAt(i);
        }
        SelectedVirtualOutput = SelectedCueList.VirtualOutputs.FirstOrDefault();
    }

    private bool CanRemoveVirtualOutput() => SelectedCueList is not null && SelectedVirtualOutput is not null;

    [RelayCommand]
    private void AddGroup()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Group)
        {
            Number = NextNumber(parent),
            Label = "Group",
            Extra = CueGroupFireMode.FirstCueOnly.ToString(),
        };
        parent.Add(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task AddMediaCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = "Media cue",
        };
        parent.Add(row);
        SelectedCueNode = row;
        var picked = await PickMediaFilePathAsync();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            row.MediaSourceItem = new FilePlaylistItem(picked);
            row.SourceOrAction = picked;
        }
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanBrowseMediaSource))]
    private async Task BrowseMediaSourceAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } mediaCue)
            return;
        var path = await PickMediaFilePathAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            mediaCue.MediaSourceItem = new FilePlaylistItem(path);
            mediaCue.SourceOrAction = path;
        }
    }

    private bool CanBrowseMediaSource() => SelectedCueNode?.Kind == CueNodeKind.Media;

    private static async Task<string?> PickMediaFilePathAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = "Pick media file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.mp3", "*.wav", "*.flac", "*.m4a"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var picked = (await owner.StorageProvider.OpenFilePickerAsync(opts)).FirstOrDefault();
        return picked?.TryGetLocalPath();
    }

    [RelayCommand]
    private void AddActionCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Action)
        {
            Number = NextNumber(parent),
            Label = "Action cue",
            Extra = CueActionKind.OscOut.ToString(),
        };
        if (SelectedActionEndpoint is not null)
            row.EndpointIdText = SelectedActionEndpoint.Id.ToString();
        parent.Add(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand]
    private void AddCommentCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Comment)
        {
            Number = NextNumber(parent),
            Label = "Comment",
            SourceOrAction = "Notes",
        };
        parent.Add(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    private void RemoveNode()
    {
        if (SelectedCueList is null || SelectedCueNode is null)
            return;
        if (!RemoveNodeRecursive(SelectedCueList.Nodes, SelectedCueNode))
            return;
        SelectedCueNode = null;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveNode() => SelectedCueList is not null && SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanAddRouteConnection))]
    private void AddRouteConnection()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media)
            return;
        EnsureDefaultVirtualOutputs();
        var targetVOut = SelectedVirtualOutput?.Channel
                         ?? SelectedCueList?.VirtualOutputs.OrderBy(x => x.Channel).FirstOrDefault()?.Channel
                         ?? media.VirtualOutputChannels.OrderBy(x => x).Select(x => (int?)x).FirstOrDefault()
                         ?? 1;
        if (!media.VirtualOutputChannels.Contains(targetVOut))
            media.VirtualOutputChannels.Add(targetVOut);
        var route = new CueRouteConnectionViewModel
        {
            InputChannel = media.RouteConnections.Count,
            VirtualOutputChannel = targetVOut,
            GainDb = 0,
            Muted = false,
        };
        media.RouteConnections.Add(route);
        SelectedRouteConnection = route;
        NotifyVisibleRouteConnectionsChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRouteConnection))]
    private void RemoveRouteConnection()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedRouteConnection is null)
            return;
        if (media.RouteConnections.Remove(SelectedRouteConnection))
        {
            SelectedRouteConnection = media.RouteConnections.FirstOrDefault();
            NotifyVisibleRouteConnectionsChanged();
        }
    }

    private bool CanAddRouteConnection() => SelectedCueNode is { Kind: CueNodeKind.Media };

    private void EnsureDefaultVirtualOutputs()
    {
        if (SelectedCueList is null || SelectedCueList.VirtualOutputs.Count > 0)
            return;
        SelectedCueList.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 1, Label = "Main L" });
        SelectedCueList.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 2, Label = "Main R" });
        NotifyVisibleVirtualOutputsChanged();
    }

    private void NotifyVisibleRouteConnectionsChanged() =>
        OnPropertyChanged(nameof(VisibleRouteConnections));

    private void NotifyVisibleVirtualOutputsChanged() =>
        OnPropertyChanged(nameof(VisibleVirtualOutputs));

    private void OnWatchedRouteConnectionsChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        NotifyVisibleRouteConnectionsChanged();
    }

    private void OnWatchedVirtualOutputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        NotifyVisibleVirtualOutputsChanged();
        AddRouteConnectionCommand.NotifyCanExecuteChanged();
    }

    private void UnwatchRouteConnections()
    {
        if (_watchedRouteMediaCue is null)
            return;
        _watchedRouteMediaCue.RouteConnectionsChanged -= OnWatchedRouteConnectionsChanged;
        _watchedRouteMediaCue = null;
    }

    private void UnwatchCueListVirtualOutputs()
    {
        if (_watchedCueList is null)
            return;
        _watchedCueList.VirtualOutputs.CollectionChanged -= OnWatchedVirtualOutputsCollectionChanged;
        _watchedCueList = null;
    }

    private bool CanRemoveRouteConnection() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedRouteConnection is not null;

    [RelayCommand(CanExecute = nameof(CanAssignSelectedActionEndpoint))]
    private void AssignSelectedActionEndpoint()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Action } actionCue || SelectedActionEndpoint is null)
            return;
        actionCue.EndpointIdText = SelectedActionEndpoint.Id.ToString();
    }

    private bool CanAssignSelectedActionEndpoint() =>
        SelectedCueNode?.Kind == CueNodeKind.Action && SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanApplyActionBuilder))]
    private void ApplyActionBuilder()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Action } cue)
            return;

        if (SelectedActionEndpoint is not null)
            cue.EndpointIdText = SelectedActionEndpoint.Id.ToString();

        cue.Extra = BuilderActionKind.ToString();
        cue.SourceOrAction = BuilderActionKind == CueActionKind.OscOut
            ? BuildOscCommandText()
            : BuildMidiCommandText();
    }

    private bool CanApplyActionBuilder() => SelectedCueNode?.Kind == CueNodeKind.Action;

    [RelayCommand(CanExecute = nameof(CanEditActionCue))]
    private async Task EditActionCueAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Action } cue)
            return;

        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var dialogVm = new Dialogs.ActionCueBuilderDialogViewModel();
        var actionKind = Enum.TryParse<CueActionKind>(cue.Extra, out var parsed)
            ? parsed
            : CueActionKind.OscOut;
        Guid? endpointId = Guid.TryParse(cue.EndpointIdText, out var id) ? id : null;
        dialogVm.Load(cue.Label, actionKind, cue.SourceOrAction, endpointId, ActionEndpoints);

        var dialog = new Views.Dialogs.ActionCueBuilderDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Views.Dialogs.ActionCueBuilderResult?>(owner);
        if (result is null)
            return;

        if (result.EndpointId is { } endpoint)
            cue.EndpointIdText = endpoint.ToString();
        else
            cue.EndpointIdText = string.Empty;
        cue.Extra = result.ActionKind.ToString();
        cue.SourceOrAction = result.CommandText;
        StatusMessage = $"Updated action cue {CueDisplay(cue)}.";
    }

    private bool CanEditActionCue() => SelectedCueNode?.Kind == CueNodeKind.Action;

    private static string NextNumber(ICollection<CueNodeViewModel> siblings) => (siblings.Count + 1).ToString();

    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private void StandbySelected()
    {
        if (SelectedCueNode is null)
            return;
        if (SelectedCueNode.Kind == CueNodeKind.Group && ResolveFireableCue(SelectedCueNode) is null)
            return;
        StandbyCueNode = SelectedCueNode;
        StatusMessage = $"Standby {CueDisplay(SelectedCueNode)}";
    }

    private bool CanStandbySelected() =>
        SelectedCueNode is { Kind: CueNodeKind.Group } group
            ? ResolveFireableCue(group) is not null
            : SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private async Task Go()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;

        if (CurrentCueNode is not null && IsTransportPaused)
        {
            IsTransportPaused = false;
            StatusMessage = $"Resumed {CueDisplay(CurrentCueNode)}";
            return;
        }

        var fire = StandbyCueNode ?? ordered.FirstOrDefault();
        if (fire is null)
            return;

        CancelTransportRun();
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
            return;

        CurrentCueNode = plan[0].Cue;
        IsTransportPaused = false;
        StandbyCueNode = NextCueAfter(ResolveFireableCue(fire) ?? fire, ordered);
        SelectedCueNode = plan[0].Cue;
        StatusMessage = $"GO {CueDisplay(fire)} ({plan.Count} trigger{(plan.Count == 1 ? "" : "s")})";
        PreRollRefreshSuggested?.Invoke(this, EventArgs.Empty);

        _transportRunCts = new CancellationTokenSource();
        try
        {
            await RunTriggerPlanAsync(plan, _transportRunCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop/Panic/next GO cancelled the prior run.
        }
    }

    private bool CanGo() => EnumerateFireableCueOrder().Any();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (CurrentCueNode is null)
            return;
        IsTransportPaused = !IsTransportPaused;
        StatusMessage = IsTransportPaused
            ? $"Paused {CueDisplay(CurrentCueNode)}"
            : $"Resumed {CueDisplay(CurrentCueNode)}";
    }

    private bool CanPause() => CurrentCueNode is not null;

    [RelayCommand]
    private void Stop()
    {
        CancelTransportRun();
        if (CurrentCueNode is null && StandbyCueNode is null && !IsTransportPaused)
            return;
        CurrentCueNode = null;
        IsTransportPaused = false;
        StatusMessage = "Stopped.";
    }

    [RelayCommand]
    private void Panic()
    {
        CancelTransportRun();
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        StatusMessage = "Panic stop: all cue transport state cleared.";
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;
        var anchor = StandbyCueNode ?? CurrentCueNode ?? ordered.First();
        var resolvedAnchor = ResolveFireableCue(anchor) ?? anchor;
        var idx = ordered.IndexOf(resolvedAnchor);
        if (idx < 0)
            return;
        var prev = idx > 0 ? ordered[idx - 1] : ordered[0];
        StandbyCueNode = prev;
        StatusMessage = $"Standby {CueDisplay(prev)}";
    }

    private bool CanBack() => EnumerateFireableCueOrder().Any();

    private CueNodeViewModel? ResolveFireableCue(CueNodeViewModel? node)
    {
        if (node is null)
            return null;
        if (node.Kind != CueNodeKind.Group)
            return node;
        return EnumerateFireableCueOrder(node.Children).FirstOrDefault();
    }

    private IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder() =>
        SelectedCueList is null ? [] : EnumerateFireableCueOrder(SelectedCueList.Nodes);

    private static IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Group)
            {
                foreach (var child in EnumerateFireableCueOrder(node.Children))
                    yield return child;
                continue;
            }
            yield return node;
        }
    }

    private static CueNodeViewModel? NextCueAfter(CueNodeViewModel current, IReadOnlyList<CueNodeViewModel> ordered)
    {
        var idx = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ReferenceEquals(ordered[i], current))
                continue;
            idx = i;
            break;
        }
        if (idx < 0 || idx + 1 >= ordered.Count)
            return null;
        return ordered[idx + 1];
    }

    private static string CueDisplay(CueNodeViewModel cue) =>
        string.IsNullOrWhiteSpace(cue.Number)
            ? cue.Label
            : $"{cue.Number} {cue.Label}".Trim();

    private string BuildOscCommandText()
    {
        var address = string.IsNullOrWhiteSpace(OscBuilderAddress)
            ? "/address"
            : OscBuilderAddress.Trim();
        if (!address.StartsWith('/'))
            address = "/" + address;
        return string.IsNullOrWhiteSpace(OscBuilderArguments)
            ? address
            : $"{address} {OscBuilderArguments.Trim()}";
    }

    private string BuildMidiCommandText()
    {
        var channel = Math.Clamp(MidiBuilderChannel, 1, 16);
        var d1 = Math.Clamp(MidiBuilderData1, 0, 127);
        var d2 = Math.Clamp(MidiBuilderData2, 0, 127);
        return MidiBuilderCommandType switch
        {
            CueMidiCommandType.NoteOn => $"ch{channel} noteon {d1} {d2}",
            CueMidiCommandType.NoteOff => $"ch{channel} noteoff {d1} {d2}",
            CueMidiCommandType.ControlChange => $"ch{channel} cc {d1} {d2}",
            CueMidiCommandType.ProgramChange => $"ch{channel} pc {d1}",
            _ => $"ch{channel} noteon {d1} {d2}",
        };
    }

    private void SyncActionBuilderFromCue(CueNodeViewModel? cue)
    {
        if (cue?.Kind != CueNodeKind.Action)
            return;

        BuilderActionKind = Enum.TryParse<CueActionKind>(cue.Extra, out var actionKind)
            ? actionKind
            : CueActionKind.OscOut;

        if (BuilderActionKind == CueActionKind.OscOut)
        {
            var raw = cue.SourceOrAction?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                OscBuilderAddress = "/address";
                OscBuilderArguments = "1";
                return;
            }

            var split = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
            OscBuilderAddress = split[0];
            OscBuilderArguments = split.Length > 1 ? split[1] : string.Empty;
            return;
        }

        ParseMidiBuilderFromSource(cue.SourceOrAction);
    }

    private void ParseMidiBuilderFromSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var tokens = raw.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        var idx = 0;
        if (tokens[0].StartsWith("ch", StringComparison.OrdinalIgnoreCase))
        {
            var channelToken = tokens[0];
            var valueText = channelToken.Contains('=')
                ? channelToken[(channelToken.IndexOf('=') + 1)..]
                : channelToken[2..];
            if (int.TryParse(valueText, out var parsedChannel))
                MidiBuilderChannel = Math.Clamp(parsedChannel, 1, 16);
            idx++;
        }

        if (idx >= tokens.Length)
            return;

        var cmd = tokens[idx++].ToLowerInvariant();
        MidiBuilderCommandType = cmd switch
        {
            "noteoff" => CueMidiCommandType.NoteOff,
            "cc" => CueMidiCommandType.ControlChange,
            "pc" or "program" => CueMidiCommandType.ProgramChange,
            _ => CueMidiCommandType.NoteOn,
        };

        if (idx < tokens.Length && int.TryParse(tokens[idx], out var d1))
            MidiBuilderData1 = Math.Clamp(d1, 0, 127);
        if (idx + 1 < tokens.Length && int.TryParse(tokens[idx + 1], out var d2))
            MidiBuilderData2 = Math.Clamp(d2, 0, 127);
    }

    private static CueGroupFireMode ParseGroupFireMode(CueNodeViewModel group) =>
        Enum.TryParse<CueGroupFireMode>(group.Extra, out var mode)
            ? mode
            : CueGroupFireMode.FirstCueOnly;

    private List<(CueNodeViewModel Cue, int DelayMs)> BuildTriggerPlan(CueNodeViewModel target)
    {
        var plan = new List<(CueNodeViewModel Cue, int DelayMs)>();
        if (target.Kind != CueNodeKind.Group)
        {
            plan.Add((target, Math.Max(0, target.PreWaitMs)));
            AppendAutoContinueCues(plan, target);
            return plan;
        }

        var mode = ParseGroupFireMode(target);
        var children = target.Children.ToList();
        var groupPreWait = Math.Max(0, target.PreWaitMs);
        if (children.Count == 0)
            return plan;

        if (mode == CueGroupFireMode.FireAllSimultaneously)
        {
            foreach (var cue in EnumerateFireableCueOrder(children))
                plan.Add((cue, checked(groupPreWait + Math.Max(0, cue.PreWaitMs))));
            plan.Sort(static (a, b) => a.DelayMs.CompareTo(b.DelayMs));
            return plan;
        }

        var first = EnumerateFireableCueOrder(children).FirstOrDefault();
        if (first is not null)
        {
            plan.Add((first, checked(groupPreWait + Math.Max(0, first.PreWaitMs))));
            AppendAutoContinueCues(plan, first);
        }
        return plan;
    }

    private void AppendAutoContinueCues(List<(CueNodeViewModel Cue, int DelayMs)> plan, CueNodeViewModel anchor)
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, anchor));
        if (idx < 0)
            return;

        for (var i = idx + 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.TriggerMode != CueTriggerMode.AutoContinue)
                break;
            if (plan.Any(p => ReferenceEquals(p.Cue, next)))
                continue;
            plan.Add((next, Math.Max(0, next.PreWaitMs)));
        }
    }

    /// <summary>Called when the active player finishes a file naturally during cue-driven playback.</summary>
    public async Task OnMediaCueNaturallyEndedAsync()
    {
        if (CurrentCueNode is not { Kind: CueNodeKind.Media })
            return;

        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, CurrentCueNode));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;

        var next = ordered[idx + 1];
        if (next.TriggerMode != CueTriggerMode.AutoFollow)
            return;

        StandbyCueNode = next;
        SelectedCueNode = next;
        StatusMessage = $"Auto-follow → {CueDisplay(next)}";
        await Go();
    }

    public void RefreshBrokenEndpointFlags()
    {
        var ids = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var broken = 0;
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
            {
                node.IsEndpointBroken = false;
                continue;
            }

            node.IsEndpointBroken = Guid.TryParse(node.EndpointIdText, out var endpointId)
                                    && !ids.Contains(endpointId);
            if (node.IsEndpointBroken)
                broken++;
        }

        if (broken > 0)
            StatusMessage = $"{broken} action cue(s) reference missing endpoints.";
    }

    /// <summary>Distinct missing endpoint IDs referenced by action cues.</summary>
    public IReadOnlyList<(Guid MissingId, int CueCount, CueActionKind Kind)> GetBrokenEndpointGroups()
    {
        var liveIds = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var groups = new Dictionary<Guid, (int Count, CueActionKind Kind)>();
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId) || liveIds.Contains(missingId))
                continue;
            var kind = Enum.TryParse<CueActionKind>(node.Extra, out var k) ? k : CueActionKind.OscOut;
            if (groups.TryGetValue(missingId, out var g))
                groups[missingId] = (g.Count + 1, g.Kind);
            else
                groups[missingId] = (1, kind);
        }

        return groups.Select(kv => (kv.Key, kv.Value.Count, kv.Value.Kind)).ToList();
    }

    public void RemapActionEndpoints(IReadOnlyDictionary<Guid, Guid> missingToReplacement)
    {
        if (missingToReplacement.Count == 0)
            return;

        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId))
                continue;
            if (!missingToReplacement.TryGetValue(missingId, out var replacement))
                continue;
            node.EndpointIdText = replacement.ToString();
        }

        RefreshBrokenEndpointFlags();
    }

    public IReadOnlyList<(Guid CueId, PortAudioInputPlaylistItem Item)> GetPortAudioPreConnectTargets(int maxCount)
    {
        if (maxCount <= 0 || SelectedCueList is null)
            return [];

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return [];

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        var targets = new List<(Guid, PortAudioInputPlaylistItem)>();
        for (var i = startIdx; i < ordered.Count && targets.Count < maxCount; i++)
        {
            var cue = ordered[i];
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not PortAudioInputPlaylistItem pa
                || !pa.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, pa));
        }

        return targets;
    }

    public void AddMediaFilesFromDrop(IEnumerable<string> paths)
    {
        if (SelectedCueList is null)
            return;

        var parent = SelectedParentCollection() ?? SelectedCueList.Nodes;
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            var row = new CueNodeViewModel(CueNodeKind.Media)
            {
                Number = NextNumber(parent),
                Label = Path.GetFileName(path),
                MediaSourceItem = new FilePlaylistItem(path),
                SourceOrAction = path,
            };
            parent.Add(row);
            added++;
        }

        if (added > 0)
            StatusMessage = $"Added {added} media cue(s) from drop.";
    }

    private IEnumerable<CueNodeViewModel> EnumerateAllCueNodes()
    {
        if (SelectedCueList is null)
            yield break;
        foreach (var node in EnumerateAllCueNodes(SelectedCueList.Nodes))
            yield return node;
    }

    private static IEnumerable<CueNodeViewModel> EnumerateAllCueNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateAllCueNodes(node.Children))
                yield return child;
        }
    }

    private async Task RunTriggerPlanAsync(IReadOnlyList<(CueNodeViewModel Cue, int DelayMs)> plan, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        foreach (var step in plan)
        {
            await WaitUntilDelayAsync(startedAt, step.DelayMs, ct);
            ct.ThrowIfCancellationRequested();
            CurrentCueNode = step.Cue;
            SelectedCueNode = step.Cue;
            var exec = await ExecuteCueAsync(step.Cue, ct);
            StatusMessage = string.IsNullOrWhiteSpace(exec)
                ? $"Triggered {CueDisplay(step.Cue)}"
                : $"Triggered {CueDisplay(step.Cue)} — {exec}";
        }
    }

    private async Task<string?> ExecuteCueAsync(CueNodeViewModel cue, CancellationToken ct)
    {
        switch (cue.Kind)
        {
            case CueNodeKind.Media:
                if (MediaCueExecutor is null)
                    return "media execution not configured";
                return cue.ToModel() is MediaCueNode media
                    ? await MediaCueExecutor(media, ct)
                    : "invalid media cue";
            case CueNodeKind.Action:
                if (ActionCueExecutor is null)
                    return "action execution not configured";
                return cue.ToModel() is ActionCueNode action
                    ? await ActionCueExecutor(action, ct)
                    : "invalid action cue";
            case CueNodeKind.Comment:
                return "comment";
            case CueNodeKind.Group:
            default:
                return null;
        }
    }

    private async Task WaitUntilDelayAsync(DateTime startedAtUtc, int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0)
            return;

        while (true)
        {
                ct.ThrowIfCancellationRequested();
                while (IsTransportPaused)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(40, ct);
                    startedAtUtc = startedAtUtc.AddMilliseconds(40);
                }

            var due = startedAtUtc.AddMilliseconds(delayMs);
            var remaining = due - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var slice = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
            await Task.Delay(slice, ct);
        }
    }

    private void CancelTransportRun()
    {
        try { _transportRunCts?.Cancel(); } catch { /* best effort */ }
        try { _transportRunCts?.Dispose(); } catch { /* best effort */ }
        _transportRunCts = null;
    }

    private static IEnumerable<CueNodeViewModel> EnumerateMediaNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Media)
                yield return node;
            foreach (var child in EnumerateMediaNodes(node.Children))
                yield return child;
        }
    }

    public void SetActionEndpoints(IEnumerable<ActionEndpoint> endpoints)
    {
        ActionEndpoints.Clear();
        foreach (var endpoint in endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            ActionEndpoints.Add(endpoint);
        if (SelectedCueNode?.Kind == CueNodeKind.Action && Guid.TryParse(SelectedCueNode.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        RefreshBrokenEndpointFlags();
    }

    public List<CueList> BuildCueListsSnapshot() => CueLists.Select(c => c.ToModel()).ToList();

    public void ApplyCueLists(IReadOnlyList<CueList> lists)
    {
        CueLists.Clear();
        foreach (var list in lists)
            CueLists.Add(CueListEditorViewModel.FromModel(list));
        if (CueLists.Count == 0)
        {
            var list = new CueListEditorViewModel("Cue List 1");
            list.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 1, Label = "Main L" });
            list.VirtualOutputs.Add(new CueVirtualOutputChannelViewModel { Channel = 2, Label = "Main R" });
            CueLists.Add(list);
        }
        SelectedCueList = CueLists[0];
        SelectedCueNode = null;
        SelectedRouteConnection = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
    }

    [RelayCommand]
    private async Task LoadCueListAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Open cue list",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay cue list") { Patterns = ["*." + CueListIO.FileExtension] },
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var list = await CueListIO.LoadAsync(path);
            var vm = CueListEditorViewModel.FromModel(list, path);
            CueLists.Add(vm);
            SelectedCueList = vm;
            SelectedCueNode = null;
            StatusMessage = $"Loaded cue list '{Path.GetFileName(path)}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cue list load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task SaveCueListAsync() =>
        SelectedCueList is { Path: { } path } ? SaveCueListToPathAsync(path) : SaveCueListAsAsync();

    [RelayCommand]
    private async Task SaveCueListAsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null || SelectedCueList is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save cue list",
            DefaultExtension = CueListIO.FileExtension,
            SuggestedFileName = string.IsNullOrWhiteSpace(SelectedCueList.Path)
                ? $"{SanitizeFileName(SelectedCueList.Name)}.{CueListIO.FileExtension}"
                : Path.GetFileName(SelectedCueList.Path),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay cue list") { Patterns = ["*." + CueListIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await SaveCueListToPathAsync(path);
    }

    private async Task SaveCueListToPathAsync(string path)
    {
        if (SelectedCueList is null)
            return;
        try
        {
            await CueListIO.SaveAsync(SelectedCueList.ToModel(), path);
            SelectedCueList.Path = path;
            StatusMessage = $"Saved cue list '{Path.GetFileName(path)}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cue list save failed: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "cue-list";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow;
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime single
            && single.MainView is Window w)
            return w;
        return null;
    }
}
