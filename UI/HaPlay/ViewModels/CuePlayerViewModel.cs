using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public enum CueNodeKind
{
    Group,
    Media,
    Action,
    Comment,
}

public enum CueRowStatus
{
    Idle,
    Standby,
    Current,
}

public enum CueMidiCommandType
{
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
}

public sealed partial class CueCompositionViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _width = 1920;

    [ObservableProperty]
    private int _height = 1080;

    [ObservableProperty]
    private int _frameRateNum = 60;

    [ObservableProperty]
    private int _frameRateDen = 1;

    public string Summary =>
        $"{Width}×{Height} @ {(FrameRateDen > 0 ? FrameRateNum / (double)FrameRateDen : 0):0.##}fps";

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Summary
        : $"{Name} ({Summary})";

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnWidthChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnHeightChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnFrameRateNumChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnFrameRateDenChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    public CueComposition ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Width = Width,
        Height = Height,
        FrameRateNum = FrameRateNum,
        FrameRateDen = FrameRateDen,
    };

    public static CueCompositionViewModel FromModel(CueComposition model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Width = model.Width,
        Height = model.Height,
        FrameRateNum = model.FrameRateNum,
        FrameRateDen = model.FrameRateDen,
    };
}

public sealed partial class CueVideoOutputBindingViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private Guid _outputLineId;

    [ObservableProperty]
    private Guid _compositionId;

    public CueVideoOutputBinding ToModel() => new()
    {
        Id = Id,
        OutputLineId = OutputLineId,
        CompositionId = CompositionId,
    };

    public static CueVideoOutputBindingViewModel FromModel(CueVideoOutputBinding model) => new()
    {
        Id = model.Id,
        OutputLineId = model.OutputLineId,
        CompositionId = model.CompositionId,
    };
}

public sealed partial class CueAudioRouteViewModel : ObservableObject
{
    [ObservableProperty]
    private int _sourceChannel;

    [ObservableProperty]
    private Guid _outputLineId;

    [ObservableProperty]
    private int _outputChannel = 1;

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private bool _muted;

    public CueAudioRoute ToModel() => new()
    {
        SourceChannel = SourceChannel,
        OutputLineId = OutputLineId,
        OutputChannel = OutputChannel,
        GainDb = GainDb,
        Muted = Muted,
    };

    public static CueAudioRouteViewModel FromModel(CueAudioRoute model) => new()
    {
        SourceChannel = model.SourceChannel,
        OutputLineId = model.OutputLineId,
        OutputChannel = model.OutputChannel,
        GainDb = model.GainDb,
        Muted = model.Muted,
    };
}

public sealed partial class CueVideoPlacementViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _compositionId;

    [ObservableProperty]
    private int _layerIndex;

    [ObservableProperty]
    private CueLayerPosition _position = CueLayerPosition.Cover;

    [ObservableProperty]
    private double _opacity = 1.0;

    public CueVideoPlacement ToModel() => new()
    {
        CompositionId = CompositionId,
        LayerIndex = LayerIndex,
        Position = Position,
        Opacity = Math.Clamp(Opacity, 0.0, 1.0),
    };

    public static CueVideoPlacementViewModel FromModel(CueVideoPlacement model) => new()
    {
        CompositionId = model.CompositionId,
        LayerIndex = model.LayerIndex,
        Position = model.Position,
        Opacity = model.Opacity,
    };
}

public sealed partial class CueNodeViewModel : ObservableObject
{
    public CueNodeViewModel(CueNodeKind kind)
    {
        Kind = kind;
        Children.CollectionChanged += OnChildrenCollectionChanged;
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
    private int _durationMs;

    [ObservableProperty]
    private CueRowStatus _rowStatus = CueRowStatus.Idle;

    public ObservableCollection<CueAudioRouteViewModel> AudioRoutes { get; } = new();

    public ObservableCollection<CueVideoPlacementViewModel> VideoPlacements { get; } = new();

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

    public bool IsGroup => Kind == CueNodeKind.Group;

    public bool HasChildren => Children.Count > 0;

    public string KindLabel => Kind switch
    {
        CueNodeKind.Group => Strings.CueKindGroupLabel,
        CueNodeKind.Media => Strings.CueKindMediaLabel,
        CueNodeKind.Action => Strings.CueKindActionLabel,
        CueNodeKind.Comment => Strings.CueKindCommentLabel,
        _ => Strings.CueKindDefaultLabel,
    };

    public string DurationDisplay
    {
        get
        {
            if (Kind != CueNodeKind.Media || DurationMs <= 0)
                return Strings.EmDash;
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    partial void OnDurationMsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DurationDisplay));
    }

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
                    DurationMs = m.DurationMs,
                    StartOffsetMs = m.StartOffsetMs,
                    Loop = m.Loop,
                    EndBehavior = m.EndBehavior,
                };
                foreach (var route in m.AudioRoutes)
                    vm.AudioRoutes.Add(CueAudioRouteViewModel.FromModel(route));
                foreach (var placement in m.VideoPlacements)
                    vm.VideoPlacements.Add(CueVideoPlacementViewModel.FromModel(placement));
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
                return new CueNodeViewModel(CueNodeKind.Comment) { Label = Strings.UnsupportedCueNodeLabel };
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
                DurationMs = Math.Max(0, DurationMs),
                StartOffsetMs = Math.Max(0, StartOffsetMs),
                Loop = Loop,
                EndBehavior = EndBehavior,
                AudioRoutes = AudioRoutes.Select(r => r.ToModel()).ToList(),
                VideoPlacements = VideoPlacements.Select(p => p.ToModel()).ToList(),
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

    public ObservableCollection<CueCompositionViewModel> Compositions { get; } = new();

    public ObservableCollection<CueVideoOutputBindingViewModel> VideoOutputs { get; } = new();

    public ObservableCollection<CueNodeViewModel> Nodes { get; } = new();

    public CueList ToModel() => new()
    {
        Name = Name,
        PreRollCount = PreRollCount,
        Compositions = Compositions.Select(c => c.ToModel()).ToList(),
        VideoOutputs = VideoOutputs.Select(o => o.ToModel()).ToList(),
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
        foreach (var c in list.Compositions)
            vm.Compositions.Add(CueCompositionViewModel.FromModel(c));
        foreach (var o in list.VideoOutputs)
            vm.VideoOutputs.Add(CueVideoOutputBindingViewModel.FromModel(o));
        foreach (var node in list.Nodes)
            vm.Nodes.Add(CueNodeViewModel.FromModel(node));
        return vm;
    }
}

public partial class CuePlayerViewModel : ViewModelBase
{
    private CancellationTokenSource? _transportRunCts;

    /// <summary>
    /// Host-provided media execution callback. When null, media cues only update transport state.
    /// </summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueExecutor { get; set; }

    /// <summary>
    /// Host-provided action execution callback. When null, action cues only update transport state.
    /// </summary>
    public Func<ActionCueNode, CancellationToken, Task<string?>>? ActionCueExecutor { get; set; }

    /// <summary>Host-provided stop callback — Stop / Panic forwards to this so the playback
    /// engine can tear down its session. Optional; null in tests.</summary>
    public Func<Task>? StopPlaybackCallback { get; set; }

    public CuePlayerViewModel()
    {
        var initial = new CueListEditorViewModel(Strings.DefaultCueListName);
        CueLists.Add(initial);
        SelectedCueList = initial;
    }

    /// <summary>Wire the cue player to the shared output registry. Audio routes and video output
    /// bindings pick lines from this list directly — no per-cue-list device config.</summary>
    public void SetAvailableOutputs(ObservableCollection<OutputLineViewModel> outputs)
    {
        AvailableOutputs = outputs;
        outputs.CollectionChanged += (_, _) => RefreshAvailableOutputBuckets();
        RefreshAvailableOutputBuckets();
    }

    private void RefreshAvailableOutputBuckets()
    {
        AvailableAudioOutputs.Clear();
        AvailableVideoOutputs.Clear();
        foreach (var line in AvailableOutputs)
        {
            if (line.Definition is Models.PortAudioOutputDefinition)
                AvailableAudioOutputs.Add(line);
            else if (line.Definition is Models.LocalVideoOutputDefinition or Models.NDIOutputDefinition)
                AvailableVideoOutputs.Add(line);
        }
    }

    public ObservableCollection<CueListEditorViewModel> CueLists { get; } = new();

    public IReadOnlyList<CueEndBehavior> CueEndBehaviors { get; } = Enum.GetValues<CueEndBehavior>();
    public IReadOnlyList<CueTriggerMode> CueTriggerModes { get; } = Enum.GetValues<CueTriggerMode>();
    public IReadOnlyList<CueGroupFireMode> GroupFireModes { get; } = Enum.GetValues<CueGroupFireMode>();
    public IReadOnlyList<CueLayerPosition> LayerPositions { get; } = Enum.GetValues<CueLayerPosition>();

    [ObservableProperty]
    private CueListEditorViewModel? _selectedCueList;

    [ObservableProperty]
    private CueNodeViewModel? _selectedCueNode;

    [ObservableProperty]
    private CueCompositionViewModel? _selectedComposition;

    [ObservableProperty]
    private CueVideoOutputBindingViewModel? _selectedVideoOutput;

    [ObservableProperty]
    private CueAudioRouteViewModel? _selectedAudioRoute;

    [ObservableProperty]
    private CueVideoPlacementViewModel? _selectedVideoPlacement;

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
    private string _oscBuilderAddress = Strings.OscBuilderDefaultAddress;

    [ObservableProperty]
    private string _oscBuilderArguments = Strings.OscBuilderDefaultArgument;

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
    private readonly ObservableCollection<CueCompositionViewModel> _emptyCompositions = new();
    private readonly ObservableCollection<CueVideoOutputBindingViewModel> _emptyVideoOutputs = new();
    private readonly ObservableCollection<CueAudioRouteViewModel> _emptyAudioRoutes = new();
    private readonly ObservableCollection<CueVideoPlacementViewModel> _emptyVideoPlacements = new();
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    /// <summary>Bag of output lines the operator has created in the shared
    /// <c>OutputManagementView</c>. <see cref="MainViewModel"/> populates this via
    /// <see cref="SetAvailableOutputs"/>. Updates are live — adding/removing in OutputManagement
    /// flows through to the cue player's dropdowns immediately.</summary>
    public ObservableCollection<OutputLineViewModel> AvailableOutputs { get; private set; } = new();

    public ObservableCollection<OutputLineViewModel> AvailableAudioOutputs { get; } = new();
    public ObservableCollection<OutputLineViewModel> AvailableVideoOutputs { get; } = new();

    public ObservableCollection<CueCompositionViewModel> VisibleCompositions =>
        SelectedCueList?.Compositions ?? _emptyCompositions;

    public ObservableCollection<CueVideoOutputBindingViewModel> VisibleVideoOutputs =>
        SelectedCueList?.VideoOutputs ?? _emptyVideoOutputs;

    public ObservableCollection<CueAudioRouteViewModel> VisibleAudioRoutes =>
        SelectedCueNode is { Kind: CueNodeKind.Media } node ? node.AudioRoutes : _emptyAudioRoutes;

    public ObservableCollection<CueVideoPlacementViewModel> VisibleVideoPlacements =>
        SelectedCueNode is { Kind: CueNodeKind.Media } node ? node.VideoPlacements : _emptyVideoPlacements;

    public bool HasSelectedMediaCue => SelectedCueNode?.Kind == CueNodeKind.Media;
    public bool HasSelectedActionCue => SelectedCueNode?.Kind == CueNodeKind.Action;
    public bool HasSelectedCommentCue => SelectedCueNode?.Kind == CueNodeKind.Comment;
    public bool HasSelectedGroupCue => SelectedCueNode?.Kind == CueNodeKind.Group;
    public bool HasSelectedCue => SelectedCueNode is not null;

    public string SelectedCueDrawerTitle => SelectedCueNode is null
        ? Strings.SelectACueDrawerHint
        : string.IsNullOrWhiteSpace(SelectedCueNode.Number)
            ? $"{SelectedCueNode.Label} — {SelectedCueNode.KindLabel}"
            : $"{SelectedCueNode.Number} {SelectedCueNode.Label} — {SelectedCueNode.KindLabel}";
    public IReadOnlyList<CueActionKind> BuilderActionKinds { get; } = Enum.GetValues<CueActionKind>();
    public IReadOnlyList<CueMidiCommandType> MidiBuilderCommandTypes { get; } = Enum.GetValues<CueMidiCommandType>();

    public string TransportState =>
        CurrentCueNode is null
            ? Strings.Format(
                nameof(Strings.CueTransportStandbyFormat),
                StandbyCueNode is null ? Strings.NoneInParensLabel : CueDisplay(StandbyCueNode))
            : Strings.Format(
                nameof(Strings.CueTransportRunningFormat),
                IsTransportPaused ? Strings.CueTransportPausedLabel : Strings.CueTransportRunningLabel,
                CueDisplay(CurrentCueNode))
              + (StandbyCueNode is null
                  ? string.Empty
                  : Strings.Format(nameof(Strings.CueTransportNextFormat), CueDisplay(StandbyCueNode)));

    partial void OnSelectedCueListChanged(CueListEditorViewModel? value)
    {
        CancelTransportRun();
        OnPropertyChanged(nameof(VisibleNodes));
        OnPropertyChanged(nameof(VisibleCompositions));
        OnPropertyChanged(nameof(VisibleVideoOutputs));
        SelectedComposition = value?.Compositions.FirstOrDefault();
        SelectedVideoOutput = value?.VideoOutputs.FirstOrDefault();
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RemoveCueListCommand.NotifyCanExecuteChanged();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        SelectedAudioRoute = value is { Kind: CueNodeKind.Media } media
            ? media.AudioRoutes.FirstOrDefault()
            : null;
        SelectedVideoPlacement = value is { Kind: CueNodeKind.Media } media2
            ? media2.VideoPlacements.FirstOrDefault()
            : null;
        RemoveNodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(HasSelectedCue));
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
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

    partial void OnSelectedAudioRouteChanged(CueAudioRouteViewModel? value)
    {
        _ = value;
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoPlacementChanged(CueVideoPlacementViewModel? value)
    {
        _ = value;
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
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
        RefreshRowStatuses();
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
        RefreshRowStatuses();
        PauseCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRowStatuses()
    {
        foreach (var node in EnumerateAllCueNodes())
        {
            var status = ReferenceEquals(node, CurrentCueNode)
                ? CueRowStatus.Current
                : ReferenceEquals(node, StandbyCueNode)
                    ? CueRowStatus.Standby
                    : CueRowStatus.Idle;
            if (node.RowStatus != status)
                node.RowStatus = status;
        }
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
        var list = new CueListEditorViewModel(Strings.Format(nameof(Strings.CueListNameFormat), CueLists.Count + 1));
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
    private void AddComposition()
    {
        if (SelectedCueList is null) return;
        var comp = new CueCompositionViewModel
        {
            Name = Strings.Format(nameof(Strings.CueOutputDefaultVideoNameFormat),
                SelectedCueList.Compositions.Count + 1),
        };
        SelectedCueList.Compositions.Add(comp);
        SelectedComposition = comp;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveComposition))]
    private void RemoveComposition()
    {
        if (SelectedCueList is null || SelectedComposition is null) return;
        var removedId = SelectedComposition.Id;
        if (!SelectedCueList.Compositions.Remove(SelectedComposition)) return;
        foreach (var media in EnumerateMediaNodes(SelectedCueList.Nodes))
            for (var i = media.VideoPlacements.Count - 1; i >= 0; i--)
                if (media.VideoPlacements[i].CompositionId == removedId)
                    media.VideoPlacements.RemoveAt(i);
        for (var i = SelectedCueList.VideoOutputs.Count - 1; i >= 0; i--)
            if (SelectedCueList.VideoOutputs[i].CompositionId == removedId)
                SelectedCueList.VideoOutputs.RemoveAt(i);
        SelectedComposition = SelectedCueList.Compositions.FirstOrDefault();
    }

    private bool CanRemoveComposition() => SelectedComposition is not null;

    [RelayCommand]
    private void AddVideoOutput()
    {
        if (SelectedCueList is null) return;
        var binding = new CueVideoOutputBindingViewModel
        {
            OutputLineId = AvailableVideoOutputs.FirstOrDefault()?.Definition.Id ?? Guid.Empty,
            CompositionId = SelectedCueList.Compositions.FirstOrDefault()?.Id ?? Guid.Empty,
        };
        SelectedCueList.VideoOutputs.Add(binding);
        SelectedVideoOutput = binding;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVideoOutput))]
    private void RemoveVideoOutput()
    {
        if (SelectedCueList is null || SelectedVideoOutput is null) return;
        if (!SelectedCueList.VideoOutputs.Remove(SelectedVideoOutput)) return;
        SelectedVideoOutput = SelectedCueList.VideoOutputs.FirstOrDefault();
    }

    private bool CanRemoveVideoOutput() => SelectedVideoOutput is not null;

    partial void OnSelectedCompositionChanged(CueCompositionViewModel? value)
    {
        _ = value;
        RemoveCompositionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoOutputChanged(CueVideoOutputBindingViewModel? value)
    {
        _ = value;
        RemoveVideoOutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddAudioRoute))]
    private void AddAudioRoute()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media) return;
        var firstOutput = AvailableAudioOutputs.FirstOrDefault();
        var channelCount = firstOutput?.Definition is Models.PortAudioOutputDefinition pa ? pa.ChannelCount : 2;
        var route = new CueAudioRouteViewModel
        {
            SourceChannel = media.AudioRoutes.Count,
            OutputLineId = firstOutput?.Definition.Id ?? Guid.Empty,
            OutputChannel = 1 + (media.AudioRoutes.Count % Math.Max(1, channelCount)),
        };
        media.AudioRoutes.Add(route);
        SelectedAudioRoute = route;
        OnPropertyChanged(nameof(VisibleAudioRoutes));
    }

    private bool CanAddAudioRoute() => SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanRemoveAudioRoute))]
    private void RemoveAudioRoute()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedAudioRoute is null) return;
        if (media.AudioRoutes.Remove(SelectedAudioRoute))
        {
            SelectedAudioRoute = media.AudioRoutes.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleAudioRoutes));
        }
    }

    private bool CanRemoveAudioRoute() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedAudioRoute is not null;

    [RelayCommand(CanExecute = nameof(CanAddVideoPlacement))]
    private void AddVideoPlacement()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedCueList is null) return;
        var firstComp = SelectedCueList.Compositions.FirstOrDefault();
        var placement = new CueVideoPlacementViewModel
        {
            CompositionId = firstComp?.Id ?? Guid.Empty,
            LayerIndex = media.VideoPlacements.Count,
        };
        media.VideoPlacements.Add(placement);
        SelectedVideoPlacement = placement;
        OnPropertyChanged(nameof(VisibleVideoPlacements));
    }

    private bool CanAddVideoPlacement() => SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanRemoveVideoPlacement))]
    private void RemoveVideoPlacement()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedVideoPlacement is null) return;
        if (media.VideoPlacements.Remove(SelectedVideoPlacement))
        {
            SelectedVideoPlacement = media.VideoPlacements.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleVideoPlacements));
        }
    }

    private bool CanRemoveVideoPlacement() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedVideoPlacement is not null;

    [RelayCommand]
    private void AddGroup()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Group)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultGroupLabel,
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
            Label = Strings.CueNodeDefaultMediaLabel,
        };
        parent.Add(row);
        SelectedCueNode = row;
        var picked = await PickMediaFilePathAsync();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            row.MediaSourceItem = new FilePlaylistItem(picked);
            row.SourceOrAction = picked;
            row.Label = Path.GetFileNameWithoutExtension(picked);
            await ProbeAndAssignDurationAsync(row, picked);
        }
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task AddNdiInputCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddNDIInputDialogViewModel();
        await dialogVm.StartDiscoveryAsync();
        var dialog = new Views.Dialogs.AddNDIInputDialog { DataContext = dialogVm };
        try
        {
            var result = await dialog.ShowDialog<NDIInputPlaylistItem?>(owner);
            if (result is null) return;
            AddLiveInputCue(result, result.DisplayName);
        }
        finally
        {
            dialogVm.StopDiscovery();
        }
    }

    [RelayCommand]
    private async Task AddPortAudioInputCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddPortAudioInputDialogViewModel();
        dialogVm.ReloadHostApis();
        var dialog = new Views.Dialogs.AddPortAudioInputDialog { DataContext = dialogVm };

        var result = await dialog.ShowDialog<PortAudioInputPlaylistItem?>(owner);
        if (result is null) return;
        AddLiveInputCue(result, result.DeviceName);
    }

    private void AddLiveInputCue(PlaylistItem source, string label)
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = string.IsNullOrWhiteSpace(label) ? source.DisplayName : label,
            MediaSourceItem = source,
            SourceOrAction = source.DisplayName,
        };
        parent.Add(row);
        SelectedCueNode = row;
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
            mediaCue.Label = Path.GetFileNameWithoutExtension(path);
            await ProbeAndAssignDurationAsync(mediaCue, path);
        }
    }

    private static async Task ProbeAndAssignDurationAsync(CueNodeViewModel row, string path)
    {
        var ms = await CueMediaProbe.TryProbeDurationMsAsync(path);
        row.DurationMs = ms ?? 0;
    }

    private bool CanBrowseMediaSource() => SelectedCueNode?.Kind == CueNodeKind.Media;

    private static async Task<string?> PickMediaFilePathAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.PickMediaFileDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.MediaFileTypeLabel) { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.mp3", "*.wav", "*.flac", "*.m4a"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
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
            Label = Strings.CueNodeDefaultActionLabel,
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
            Label = Strings.CueNodeDefaultCommentLabel,
            SourceOrAction = Strings.CueNodeDefaultNotesText,
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
        StatusMessage = Strings.Format(nameof(Strings.UpdatedActionCueStatusFormat), CueDisplay(cue));
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
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(SelectedCueNode));
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
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
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
        StatusMessage = Strings.Format(
            nameof(Strings.CueGoStatusFormat),
            CueDisplay(fire),
            plan.Count,
            plan.Count == 1 ? string.Empty : Strings.PluralSuffixS);
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
            ? Strings.Format(nameof(Strings.CuePausedStatusFormat), CueDisplay(CurrentCueNode))
            : Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
    }

    private bool CanPause() => CurrentCueNode is not null;

    [RelayCommand]
    private void Stop()
    {
        CancelTransportRun();
        _ = StopPlaybackCallback?.Invoke();
        if (CurrentCueNode is null && StandbyCueNode is null && !IsTransportPaused)
            return;
        CurrentCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CueStoppedStatus;
    }

    [RelayCommand]
    private void Panic()
    {
        CancelTransportRun();
        _ = StopPlaybackCallback?.Invoke();
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CuePanicStatus;
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
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(prev));
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
            ? Strings.OscBuilderDefaultAddress
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
                OscBuilderAddress = Strings.OscBuilderDefaultAddress;
                OscBuilderArguments = Strings.OscBuilderDefaultArgument;
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
        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplay(next));
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
            StatusMessage = Strings.Format(nameof(Strings.CueBrokenEndpointCountStatusFormat), broken);
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
                Label = Path.GetFileNameWithoutExtension(path),
                MediaSourceItem = new FilePlaylistItem(path),
                SourceOrAction = path,
            };
            parent.Add(row);
            _ = ProbeAndAssignDurationAsync(row, path);
            added++;
        }

        if (added > 0)
            StatusMessage = Strings.Format(nameof(Strings.CueAddedFromDropStatusFormat), added);
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
                ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplay(step.Cue))
                : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(step.Cue), exec);
        }
    }

    private async Task<string?> ExecuteCueAsync(CueNodeViewModel cue, CancellationToken ct)
    {
        switch (cue.Kind)
        {
            case CueNodeKind.Media:
                if (MediaCueExecutor is null)
                    return Strings.CueMediaExecutionNotConfigured;
                return cue.ToModel() is MediaCueNode media
                    ? await MediaCueExecutor(media, ct)
                    : Strings.CueInvalidMediaCue;
            case CueNodeKind.Action:
                if (ActionCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is ActionCueNode action
                    ? await ActionCueExecutor(action, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Comment:
                return Strings.CueCommentResult;
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
            CueLists.Add(new CueListEditorViewModel(Strings.DefaultCueListName));
        SelectedCueList = CueLists[0];
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
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
            Title = Strings.OpenCueListDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
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
            StatusMessage = Strings.Format(nameof(Strings.LoadedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListLoadFailedStatusFormat), ex.Message);
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
            Title = Strings.SaveCueListDialogTitle,
            DefaultExtension = CueListIO.FileExtension,
            SuggestedFileName = string.IsNullOrWhiteSpace(SelectedCueList.Path)
                ? Strings.Format(nameof(Strings.CueListDefaultFileNameFormat), SanitizeFileName(SelectedCueList.Name), CueListIO.FileExtension)
                : Path.GetFileName(SelectedCueList.Path),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
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
            StatusMessage = Strings.Format(nameof(Strings.SavedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListSaveFailedStatusFormat), ex.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Strings.CueListFileNameFallback;
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
