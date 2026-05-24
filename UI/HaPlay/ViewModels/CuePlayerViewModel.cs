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

/// <summary>Process-wide registry used by route/binding VMs to resolve their <c>OutputLineId</c>
/// into a live <see cref="OutputLineViewModel"/> for health-dot binding. <see cref="CuePlayerViewModel"/>
/// keeps it populated whenever the shared output collection changes — this avoids threading a
/// resolver delegate through every CueAudioRouteViewModel / CueVideoOutputBindingViewModel
/// instance loaded from disk. Single-process MVVM only; not thread-safe.</summary>
internal static class OutputLineRegistry
{
    private static IReadOnlyDictionary<Guid, OutputLineViewModel> _byId =
        new Dictionary<Guid, OutputLineViewModel>();

    public static event EventHandler? Changed;

    public static OutputLineViewModel? Resolve(Guid lineId) =>
        _byId.TryGetValue(lineId, out var line) ? line : null;

    public static void Replace(IEnumerable<OutputLineViewModel> lines)
    {
        _byId = lines
            .GroupBy(l => l.Definition.Id)
            .ToDictionary(g => g.Key, g => g.First());
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>One swatch in the drawer's color-tag picker (Phase 5.8.1). Plain DTO — the
/// button command lives on <see cref="CuePlayerViewModel"/>; this VM just supplies the
/// fill/border colors and the tag index.</summary>
public sealed class CueColorSwatchViewModel
{
    public CueColorSwatchViewModel(int index)
    {
        Index = index;
        FillBrush = CueColorTagPalette.BrushHex(index);
        Name = CueColorTagPalette.Name(index);
        BorderBrush = index == 0 ? "#888888" : "#22000000";
    }

    public int Index { get; }
    public string FillBrush { get; }
    public string Name { get; }
    public string BorderBrush { get; }
}

public sealed partial class CueVideoOutputBindingViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private Guid _outputLineId;

    [ObservableProperty]
    private Guid _compositionId;

    /// <summary>Resolved reference to the line so the row can show its health dot/tooltip.
    /// Kept in sync by <see cref="CuePlayerViewModel"/>.</summary>
    [ObservableProperty]
    private OutputLineViewModel? _lineRef;

    partial void OnOutputLineIdChanged(Guid value) => LineRef = OutputLineRegistry.Resolve(value);

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

    /// <summary>Resolved reference to the line so the row can show its health dot/tooltip.
    /// Kept in sync by <see cref="CuePlayerViewModel"/>.</summary>
    [ObservableProperty]
    private OutputLineViewModel? _lineRef;

    partial void OnOutputLineIdChanged(Guid value) => LineRef = OutputLineRegistry.Resolve(value);

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
    private bool _sourceHasVideo;

    [ObservableProperty]
    private bool _sourceHasAudio;

    [ObservableProperty]
    private int _sourceAudioChannels;

    [ObservableProperty]
    private bool _sourceVideoIsAttachedPicture;

    [ObservableProperty]
    private CueRowStatus _rowStatus = CueRowStatus.Idle;

    /// <summary>True while this cue's media is held warm in the pre-roll cache (Phase 5.7.2).
    /// The status badge column draws a light outline when this is set and the row is idle.</summary>
    [ObservableProperty]
    private bool _isPreRollWarm;

    /// <summary>Color tag index 0..7 (Phase 5.8.1). 0 = no tag. The tree's first column shows
    /// a thin vertical strip filled with the palette color; the drawer's General tab lets the
    /// operator pick a swatch.</summary>
    [ObservableProperty]
    private int _colorTag;

    public string ColorTagBrush => CueColorTagPalette.BrushHex(ColorTag);

    partial void OnColorTagChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(ColorTagBrush));
    }

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
            if (Kind == CueNodeKind.Group)
                return BuildGroupDurationDisplay();
            if (Kind != CueNodeKind.Media || DurationMs <= 0)
                return Strings.EmDash;
            return FormatDurationMs(DurationMs);
        }
    }

    private string BuildGroupDurationDisplay()
    {
        long rollupMs;
        int itemCount;
        switch (GroupFireMode)
        {
            case CueGroupFireMode.FireAllSimultaneously:
                (rollupMs, itemCount) = AggregateChildrenDurations(static (sumMs, childMs) => Math.Max(sumMs, childMs));
                break;
            case CueGroupFireMode.FirstCueOnly:
                rollupMs = Children.FirstOrDefault(c => c.Kind != CueNodeKind.Comment)?.RolledDurationMs ?? 0;
                itemCount = Children.Count;
                break;
            case CueGroupFireMode.ArmedList:
            default:
                (rollupMs, itemCount) = AggregateChildrenDurations(static (sumMs, childMs) => sumMs + childMs);
                break;
        }

        if (rollupMs <= 0 && itemCount == 0)
            return Strings.EmDash;

        var time = rollupMs <= 0 ? Strings.EmDash : FormatDurationMs((int)Math.Min(int.MaxValue, rollupMs));
        return $"{time} · {itemCount}";
    }

    /// <summary>Walk children, accumulate via <paramref name="combine"/>, count items recursively.
    /// Children that are groups roll up first via <see cref="RolledDurationMs"/>.</summary>
    private (long Ms, int Count) AggregateChildrenDurations(Func<long, long, long> combine)
    {
        long ms = 0;
        var count = 0;
        foreach (var child in Children)
        {
            if (child.Kind == CueNodeKind.Comment) continue;
            ms = combine(ms, child.RolledDurationMs);
            count++;
        }
        return (ms, count);
    }

    /// <summary>Effective duration for roll-ups: groups recursively roll up via their own
    /// <see cref="BuildGroupDurationDisplay"/> rules; media cues return their probed
    /// <see cref="DurationMs"/>; other kinds (Action / Comment) return 0.</summary>
    public long RolledDurationMs
    {
        get
        {
            switch (Kind)
            {
                case CueNodeKind.Media: return DurationMs;
                case CueNodeKind.Group:
                {
                    switch (GroupFireMode)
                    {
                        case CueGroupFireMode.FireAllSimultaneously:
                            return AggregateChildrenDurations(static (sumMs, childMs) => Math.Max(sumMs, childMs)).Ms;
                        case CueGroupFireMode.FirstCueOnly:
                            return Children.FirstOrDefault(c => c.Kind != CueNodeKind.Comment)?.RolledDurationMs ?? 0;
                        default:
                            return AggregateChildrenDurations(static (sumMs, childMs) => sumMs + childMs).Ms;
                    }
                }
                default: return 0;
            }
        }
    }

    private static string FormatDurationMs(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    partial void OnDurationMsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(RolledDurationMs));
    }

    partial void OnExtraChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(GroupFireMode));
        OnPropertyChanged(nameof(ActionKind));
        // GroupFireMode determines the roll-up formula — refresh derived displays.
        if (Kind == CueNodeKind.Group)
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
    }

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<CueNodeViewModel>())
                item.PropertyChanged -= OnChildPropertyChangedForRollup;
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<CueNodeViewModel>())
                item.PropertyChanged += OnChildPropertyChangedForRollup;
        }
        OnPropertyChanged(nameof(HasChildren));
        if (Kind == CueNodeKind.Group)
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
    }

    private void OnChildPropertyChangedForRollup(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Kind != CueNodeKind.Group) return;
        if (e.PropertyName is nameof(RolledDurationMs)
            or nameof(DurationMs)
            or nameof(GroupFireMode))
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
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
                    ColorTag = g.ColorTag,
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
                    ColorTag = m.ColorTag,
                    MediaSourceItem = m.Source,
                    SourceOrAction = m.Source?.DisplayName ?? string.Empty,
                    FadeInMs = m.FadeInMs,
                    FadeOutMs = m.FadeOutMs,
                    DurationMs = m.DurationMs,
                    SourceHasVideo = m.HasVideo,
                    SourceHasAudio = m.HasAudio,
                    SourceAudioChannels = m.AudioChannels,
                    SourceVideoIsAttachedPicture = m.VideoIsAttachedPicture,
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
                    ColorTag = a.ColorTag,
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
                    ColorTag = c.ColorTag,
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
                ColorTag = ColorTag,
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
                ColorTag = ColorTag,
                Source = MediaSourceItem
                           ?? (string.IsNullOrWhiteSpace(SourceOrAction)
                               ? null
                               : new FilePlaylistItem(SourceOrAction)),
                FadeInMs = Math.Max(0, FadeInMs),
                FadeOutMs = Math.Max(0, FadeOutMs),
                DurationMs = Math.Max(0, DurationMs),
                HasVideo = SourceHasVideo,
                HasAudio = SourceHasAudio,
                AudioChannels = Math.Max(0, SourceAudioChannels),
                VideoIsAttachedPicture = SourceVideoIsAttachedPicture,
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
                ColorTag = ColorTag,
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
                ColorTag = ColorTag,
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
        DefaultTriggerMode = DefaultTriggerMode,
        AutoRenumberOnInsert = AutoRenumberOnInsert,
        Compositions = Compositions.Select(c => c.ToModel()).ToList(),
        VideoOutputs = VideoOutputs.Select(o => o.ToModel()).ToList(),
        Nodes = Nodes.Select(n => n.ToModel()).ToList(),
    };

    [ObservableProperty]
    private int _preRollCount = 4;

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode = CueTriggerMode.Manual;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    public static CueListEditorViewModel FromModel(CueList list, string? path = null)
    {
        var vm = new CueListEditorViewModel(list.Name)
        {
            Path = path,
            PreRollCount = list.PreRollCount > 0 ? list.PreRollCount : 4,
            DefaultTriggerMode = list.DefaultTriggerMode,
            AutoRenumberOnInsert = list.AutoRenumberOnInsert,
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

    /// <summary>Host-provided pause callback — Pause/Resume forwards to this so the playback
    /// engine freezes active media instead of only deferring pending cue delays.</summary>
    public Func<bool, Task>? SetPlaybackPausedCallback { get; set; }

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
            {
                AvailableAudioOutputs.Add(line);
            }
            else if (line.Definition is Models.LocalVideoOutputDefinition)
            {
                AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.NDIOutputDefinition ndi)
            {
                if (ndi.StreamMode != NDIOutputStreamMode.VideoOnly)
                    AvailableAudioOutputs.Add(line);
                if (ndi.StreamMode != NDIOutputStreamMode.AudioOnly)
                    AvailableVideoOutputs.Add(line);
            }
        }
        OutputLineRegistry.Replace(AvailableOutputs);
        ResolveAllBindingLineRefs();
    }

    /// <summary>Walks every loaded cue list and refreshes the resolved <c>LineRef</c> on each
    /// audio route + video output binding. Called when the available output set changes (lines
    /// added/removed/swapped) so the row dots and tooltips stay accurate.</summary>
    private void ResolveAllBindingLineRefs()
    {
        foreach (var list in CueLists)
        {
            foreach (var binding in list.VideoOutputs)
                binding.LineRef = OutputLineRegistry.Resolve(binding.OutputLineId);
            ResolveLineRefsInNodes(list.Nodes);
        }
    }

    private static void ResolveLineRefsInNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var route in node.AudioRoutes)
                route.LineRef = OutputLineRegistry.Resolve(route.OutputLineId);
            ResolveLineRefsInNodes(node.Children);
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

    /// <summary>All cue nodes the operator currently has highlighted in the tree (multi-select).
    /// The drawer still shows fields from the singular <see cref="SelectedCueNode"/>, but
    /// "+ Route" / "+ Placement" fan their action out across every media cue in this list — so
    /// the operator can stage a route on 11 audio cues in one click.</summary>
    private readonly List<CueNodeViewModel> _selectedCueNodes = new();

    public IReadOnlyList<CueNodeViewModel> SelectedCueNodes => _selectedCueNodes;

    /// <summary>Called by <c>CuePlayerView</c>'s row-selection changed handler with the live set
    /// of selected nodes. Keeps the singular <see cref="SelectedCueNode"/> as the primary
    /// (first in the list) so all the existing drawer bindings keep working.</summary>
    public void UpdateSelection(IReadOnlyList<CueNodeViewModel> selected)
    {
        _selectedCueNodes.Clear();
        _selectedCueNodes.AddRange(selected);
        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

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

    /// <summary>Video tab visibility: media cue AND the source actually has a video stream
    /// (decodable — covers regular video files and audio files with attached picture cover art).</summary>
    public bool HasSelectedMediaCueWithVideo =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media && media.SourceHasVideo;

    /// <summary>Operator hint banner — true when the only "video" the source offers is an
    /// attached picture (e.g. MP3 album art). The Video tab still works (the still frame can be
    /// placed into a composition for a now-playing slate) but it's worth flagging.</summary>
    public bool HasSelectedMediaCueWithAttachedPictureOnly =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media && media.SourceVideoIsAttachedPicture;

    /// <summary>How many cues the operator currently has highlighted in the tree. The drawer
    /// shows a banner above the routes/placements lists when this is > 1 so the operator knows
    /// that "+ Route" / "+ Placement" applies to all of them, not just the primary.</summary>
    public int SelectedCueCount => _selectedCueNodes.Count;

    /// <summary>True iff <see cref="SelectedCueCount"/> > 1. Bound as the banner visibility flag —
    /// Avalonia's <c>ObjectConverters</c> doesn't ship a <c>GreaterThan</c>, so we expose a
    /// dedicated boolean rather than wire a per-view converter.</summary>
    public bool IsMultiSelected => _selectedCueNodes.Count > 1;

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

    private CueNodeViewModel? _watchedSelectedCueForProbe;

    private void OnSelectedCueProbeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture))
        {
            OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        }
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        // The selected cue's probe fields can land AFTER selection (when the operator picks a
        // file via "Browse media…"; the probe is async). Re-subscribe so the Video tab visibility
        // re-evaluates when the probe finishes.
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged -= OnSelectedCueProbeChanged;
        _watchedSelectedCueForProbe = value;
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged += OnSelectedCueProbeChanged;

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
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
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
        RebuildUpcomingCues();
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

    /// <summary>Set of cue ids the playback engine reports as currently active. Maintained via
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/> from the host (MainViewModel wires
    /// these to the engine's events). Used by <see cref="RefreshRowStatuses"/> so every active
    /// cue lights up — the singular <see cref="CurrentCueNode"/> only tracks the last-started
    /// one for AutoFollow / transport-state purposes.</summary>
    private readonly HashSet<Guid> _activeCueIds = new();

    /// <summary>Rows visible in the right-side Now Playing panel. Maintained by
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/>; their progress fields update via
    /// <see cref="OnCueProgress"/>.</summary>
    public ObservableCollection<ActiveCueViewModel> ActiveCues { get; } = new();

    /// <summary>Cues that *will* fire once the operator presses Go from the current Standby
    /// position — used by the Now Playing panel's Upcoming section.</summary>
    public ObservableCollection<CueNodeViewModel> UpcomingCues { get; } = new();

    /// <summary>Host-provided per-cue stop callback (engine.StopCueAsync). The Now Playing
    /// panel's per-row ✕ button forwards through this; null in tests.</summary>
    public Func<Guid, Task>? CancelCueCallback { get; set; }

    /// <summary>Engine callback — cue began playing. Marks its row Current and pushes a new
    /// <see cref="ActiveCueViewModel"/> into <see cref="ActiveCues"/>.</summary>
    public void OnCueStarted(Guid cueId)
    {
        _activeCueIds.Add(cueId);
        RefreshRowStatuses();

        var node = FindNodeById(cueId);
        if (node is not null && !ActiveCues.Any(a => a.CueId == cueId))
        {
            var entry = new ActiveCueViewModel(node, cueId, id => _ = (CancelCueCallback?.Invoke(id) ?? Task.CompletedTask))
            {
                DurationMs = Math.Max(0, node.DurationMs),
            };
            ActiveCues.Add(entry);
        }
        RebuildUpcomingCues();
    }

    /// <summary>Engine callback — cue stopped (natural end, Stop, or Panic). Clears Current
    /// status and removes the matching <see cref="ActiveCueViewModel"/>.</summary>
    public void OnCueEnded(Guid cueId)
    {
        _activeCueIds.Remove(cueId);
        RefreshRowStatuses();

        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (ActiveCues[i].CueId == cueId)
                ActiveCues.RemoveAt(i);
        RebuildUpcomingCues();
    }

    /// <summary>Engine callback — progress sample for one active cue. Updates the row's
    /// position so the progress bar and "mm:ss / mm:ss" display advance.</summary>
    public void OnCueProgress(CuePlaybackProgress p)
    {
        foreach (var a in ActiveCues)
        {
            if (a.CueId != p.CueId) continue;
            a.PositionMs = (long)p.Position.TotalMilliseconds;
            if (p.Duration > TimeSpan.Zero)
                a.DurationMs = (long)p.Duration.TotalMilliseconds;
            break;
        }
    }

    private CueNodeViewModel? FindNodeById(Guid id)
    {
        foreach (var node in EnumerateAllCueNodes())
            if (node.Id == id)
                return node;
        return null;
    }

    /// <summary>Host callback — pre-roll cache membership changed. Snapshot lists the cue ids
    /// that are currently warmed. Walks every loaded cue node and sets <c>IsPreRollWarm</c>
    /// accordingly so the status badge column can render the warming indicator (Phase 5.7.2).</summary>
    public void OnPreRollCacheChanged(IReadOnlyCollection<Guid> warmCueIds)
    {
        var warm = warmCueIds as HashSet<Guid> ?? new HashSet<Guid>(warmCueIds);
        foreach (var node in EnumerateAllCueNodes())
        {
            var shouldBeWarm = warm.Contains(node.Id);
            if (node.IsPreRollWarm != shouldBeWarm)
                node.IsPreRollWarm = shouldBeWarm;
        }
    }

    private void RebuildUpcomingCues()
    {
        UpcomingCues.Clear();
        if (SelectedCueList is null) return;
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0) return;

        var anchor = StandbyCueNode ?? ordered.FirstOrDefault();
        if (anchor is null) return;
        var startIdx = ordered.FindIndex(c => ReferenceEquals(c, ResolveFireableCue(anchor) ?? anchor));
        if (startIdx < 0) return;

        // Show up to 8 cues ahead — enough context for a chain without crowding the panel.
        for (var i = startIdx; i < ordered.Count && UpcomingCues.Count < 8; i++)
        {
            var c = ordered[i];
            // Don't list already-active cues as upcoming — they're in the Active section.
            if (_activeCueIds.Contains(c.Id)) continue;
            UpcomingCues.Add(c);
        }
    }

    private void RefreshRowStatuses()
    {
        foreach (var node in EnumerateAllCueNodes())
        {
            var status = _activeCueIds.Contains(node.Id)
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
        var targets = MediaCuesInSelection();
        if (targets.Count == 0) return;
        var firstOutput = AvailableAudioOutputs.FirstOrDefault();
        var channelCount = GetAudioOutputChannelCount(firstOutput);

        CueAudioRouteViewModel? lastOnPrimary = null;
        foreach (var media in targets)
        {
            var route = new CueAudioRouteViewModel
            {
                SourceChannel = media.AudioRoutes.Count,
                OutputLineId = firstOutput?.Definition.Id ?? Guid.Empty,
                OutputChannel = 1 + (media.AudioRoutes.Count % Math.Max(1, channelCount)),
            };
            media.AudioRoutes.Add(route);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = route;
        }
        if (lastOnPrimary is not null)
            SelectedAudioRoute = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleAudioRoutes));
    }

    private static int GetAudioOutputChannelCount(OutputLineViewModel? line) =>
        line?.Definition switch
        {
            Models.PortAudioOutputDefinition pa => Math.Max(1, pa.ChannelCount),
            Models.NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly =>
                Math.Max(1, nd.AudioChannelCount),
            _ => 2,
        };

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
        if (SelectedCueList is null) return;
        var targets = MediaCuesInSelection();
        if (targets.Count == 0) return;
        var firstComp = SelectedCueList.Compositions.FirstOrDefault();

        CueVideoPlacementViewModel? lastOnPrimary = null;
        foreach (var media in targets)
        {
            var placement = new CueVideoPlacementViewModel
            {
                CompositionId = firstComp?.Id ?? Guid.Empty,
                LayerIndex = media.VideoPlacements.Count,
            };
            media.VideoPlacements.Add(placement);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = placement;
        }
        if (lastOnPrimary is not null)
            SelectedVideoPlacement = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleVideoPlacements));
    }

    private bool CanAddVideoPlacement() => SelectedCueNode is { Kind: CueNodeKind.Media };

    /// <summary>Media cues in the current multi-selection. Falls back to the singular
    /// <see cref="SelectedCueNode"/> when only one row is selected (the common case).</summary>
    private List<CueNodeViewModel> MediaCuesInSelection()
    {
        if (_selectedCueNodes.Count > 1)
            return _selectedCueNodes.Where(n => n.Kind == CueNodeKind.Media).ToList();
        return SelectedCueNode is { Kind: CueNodeKind.Media } single
            ? new List<CueNodeViewModel> { single }
            : new List<CueNodeViewModel>();
    }

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
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    /// <summary>Phase 5.8.2 — central hook for "just-added" cues. Stamps the cue list's
    /// configured default trigger mode and (if the per-list flag is set) re-runs the renumber
    /// pass so numbering stays sequential.</summary>
    private void FinalizeAddedCue(CueNodeViewModel node)
    {
        if (SelectedCueList is null) return;
        node.TriggerMode = SelectedCueList.DefaultTriggerMode;
        if (SelectedCueList.AutoRenumberOnInsert)
            RenumberFlat(SelectedCueList.Nodes, start: 1, step: 1);
    }

    [RelayCommand]
    private async Task AddMediaCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;

        // Always seed at least one empty cue (matches the prior "+ Media → row, then pick" UX
        // and the test contract). Multi-select fills it with the first file plus N-1 follow-ups.
        var firstRow = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultMediaLabel,
        };
        parent.Add(firstRow);
        FinalizeAddedCue(firstRow);
        SelectedCueNode = firstRow;

        var picked = await PickMediaFilePathsAsync(allowMultiple: true);
        if (picked.Count == 0)
        {
            // Picker cancelled — leave the empty cue so the operator can still drag a file onto
            // it (and so the existing tests' assumptions hold).
            GoCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
            StatusMessage = null;
            return;
        }

        var firstPath = picked[0];
        firstRow.MediaSourceItem = new FilePlaylistItem(firstPath);
        firstRow.SourceOrAction = firstPath;
        firstRow.Label = Path.GetFileNameWithoutExtension(firstPath);
        await ProbeAndAssignDurationAsync(firstRow, firstPath);

        CueNodeViewModel lastAdded = firstRow;
        for (var i = 1; i < picked.Count; i++)
        {
            var path = picked[i];
            var row = new CueNodeViewModel(CueNodeKind.Media)
            {
                Number = NextNumber(parent),
                Label = Path.GetFileNameWithoutExtension(path),
                MediaSourceItem = new FilePlaylistItem(path),
                SourceOrAction = path,
            };
            parent.Add(row);
            FinalizeAddedCue(row);
            lastAdded = row;
            await ProbeAndAssignDurationAsync(row, path);
        }

        SelectedCueNode = lastAdded;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = picked.Count > 1
            ? Strings.Format(nameof(Strings.CueAddedFromDropStatusFormat), picked.Count)
            : null;
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
        FinalizeAddedCue(row);
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

    /// <summary>Open the file once, probe duration + audio/video stream info + audio channel
    /// count, and write the lot onto the cue VM. The drawer's Audio + Video tab visibility and
    /// hints depend on these — landing them right away (before <c>StatusMessage</c> resets)
    /// keeps the UI accurate even for the cancel-leaves-empty-cue case.</summary>
    private static async Task ProbeAndAssignDurationAsync(CueNodeViewModel row, string path)
    {
        var probe = await CueMediaProbe.TryProbeAsync(path).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (probe is null)
            {
                row.DurationMs = 0;
                row.SourceHasVideo = false;
                row.SourceHasAudio = false;
                row.SourceAudioChannels = 0;
                row.SourceVideoIsAttachedPicture = false;
                return;
            }

            row.DurationMs = probe.Value.DurationMs ?? 0;
            row.SourceHasVideo = probe.Value.HasVideo;
            row.SourceHasAudio = probe.Value.HasAudio;
            row.SourceAudioChannels = probe.Value.AudioChannels;
            row.SourceVideoIsAttachedPicture = probe.Value.VideoIsAttachedPicture;
        });
    }

    private bool CanBrowseMediaSource() => SelectedCueNode?.Kind == CueNodeKind.Media;

    private static async Task<string?> PickMediaFilePathAsync()
    {
        var paths = await PickMediaFilePathsAsync(allowMultiple: false);
        return paths.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<string>> PickMediaFilePathsAsync(bool allowMultiple)
    {
        var owner = TryGetMainWindow();
        if (owner is null) return [];
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.PickMediaFileDialogTitle,
            AllowMultiple = allowMultiple,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.MediaFileTypeLabel) { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.mp3", "*.wav", "*.flac", "*.m4a"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList()!;
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
        FinalizeAddedCue(row);
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

    /// <summary>Opens the rename popup for the currently selected cue. F2 triggers this from the
    /// tree's key bindings (Phase 5.6 wires F2); the right-click menu / drawer's "Rename…"
    /// affordance can also invoke it. Cancel discards changes; OK / Enter commits Number + Label.</summary>
    [RelayCommand(CanExecute = nameof(CanRenameSelectedCue))]
    private async Task RenameSelectedCueAsync()
    {
        if (SelectedCueNode is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = Dialogs.RenameCueDialogViewModel.For(SelectedCueNode);
        var dialog = new Views.Dialogs.RenameCueDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.RenameCueDialogResult?>(owner);
        if (result is null) return;

        var oldDisplay = CueDisplay(SelectedCueNode);
        SelectedCueNode.Number = result.Number;
        SelectedCueNode.Label = result.Label;
        StatusMessage = Strings.Format(nameof(Strings.RenamedCueStatusFormat), oldDisplay, CueDisplay(SelectedCueNode));
    }

    private bool CanRenameSelectedCue() => SelectedCueNode is not null;

    /// <summary>Phase 5.8.2 — open the cue list settings dialog (pre-roll, default trigger
    /// mode, auto-renumber). Replaces the inline pre-roll spinner that used to live on the
    /// toolbar; gear icon on the toolbar opens this.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenCueListSettings))]
    private async Task OpenCueListSettingsAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.CueListSettingsDialogViewModel(
            SelectedCueList.PreRollCount,
            SelectedCueList.DefaultTriggerMode,
            SelectedCueList.AutoRenumberOnInsert);
        var dialog = new Views.Dialogs.CueListSettingsDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.CueListSettingsDialogResult?>(owner);
        if (result is null) return;

        SelectedCueList.PreRollCount = Math.Max(0, result.PreRollCount);
        SelectedCueList.DefaultTriggerMode = result.DefaultTriggerMode;
        SelectedCueList.AutoRenumberOnInsert = result.AutoRenumberOnInsert;
        StatusMessage = Strings.CueListSettingsAppliedStatus;
    }

    private bool CanOpenCueListSettings() => SelectedCueList is not null;

    /// <summary>Move the selected cue up one slot within its parent collection. Ctrl+↑ binds
    /// here. No-op at the top of the parent (operator's expected behaviour — they get to feel
    /// the boundary).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueUp() => MoveSelectedCue(-1);

    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueDown() => MoveSelectedCue(+1);

    private bool CanMoveSelectedCue() => SelectedCueNode is not null && SelectedCueList is not null;

    private void MoveSelectedCue(int delta)
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;
        if (FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) is not IList<CueNodeViewModel> parent)
            return;
        var idx = parent.IndexOf(SelectedCueNode);
        var next = idx + delta;
        if (next < 0 || next >= parent.Count) return;
        var node = SelectedCueNode;
        parent.RemoveAt(idx);
        parent.Insert(next, node);
        SelectedCueNode = node;
    }

    /// <summary>Deep-copy the selected cue with a fresh id and insert immediately after the
    /// original. Routes, placements, and group-children all clone. Bound to Ctrl+D.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateSelectedCue))]
    private void DuplicateSelectedCue()
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;
        if (FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) is not IList<CueNodeViewModel> parent)
            return;

        // Round-trip through the model layer to deep-copy reliably (the model records are
        // immutable so cloning is just a fresh `with { Id = NewGuid() }` cascade).
        var snapshot = SelectedCueNode.ToModel();
        var copy = CloneCueNodeWithNewIds(snapshot);
        var copyVm = CueNodeViewModel.FromModel(copy);

        var idx = parent.IndexOf(SelectedCueNode);
        parent.Insert(idx + 1, copyVm);
        SelectedCueNode = copyVm;
    }

    private bool CanDuplicateSelectedCue() => SelectedCueNode is not null && SelectedCueList is not null;

    /// <summary>Phase 5.8.1 — clicking a color swatch sets the tag on every selected cue
    /// (so multi-select tagging works out of the box). Tag 0 clears.</summary>
    [RelayCommand(CanExecute = nameof(CanSetSelectedCueColorTag))]
    private void SetSelectedCueColorTag(int tag)
    {
        var clamped = Math.Clamp(tag, 0, CueColorTagPalette.MaxIndex);
        var targets = SelectedCueNodes.Count > 0
            ? SelectedCueNodes
            : (SelectedCueNode is null ? Array.Empty<CueNodeViewModel>() : new[] { SelectedCueNode });
        foreach (var node in targets)
            node.ColorTag = clamped;
    }

    private bool CanSetSelectedCueColorTag() => SelectedCueNode is not null;

    /// <summary>Swatch row bound by the drawer's General tab. Index 0 is "no tag" (transparent
    /// fill, slightly thicker border so it's clickable). Indexes 1..7 match
    /// <see cref="CueColorTagPalette"/>.</summary>
    public IReadOnlyList<CueColorSwatchViewModel> ColorTagSwatches { get; } =
        Enumerable.Range(0, CueColorTagPalette.MaxIndex + 1)
            .Select(i => new CueColorSwatchViewModel(i))
            .ToList();

    private static CueNode CloneCueNodeWithNewIds(CueNode src) => src switch
    {
        CueGroupNode g => g with
        {
            Id = Guid.NewGuid(),
            Children = g.Children.Select(CloneCueNodeWithNewIds).ToList(),
        },
        MediaCueNode m => m with { Id = Guid.NewGuid() },
        ActionCueNode a => a with { Id = Guid.NewGuid() },
        CommentCueNode c => c with { Id = Guid.NewGuid() },
        _ => src,
    };

    /// <summary>Bulk renumber. Walks the chosen scope (all / root only / current selection) in
    /// tree order, assigning <c>start</c>, <c>start+step</c>, … Nested groups recurse with a
    /// sub-numbering scheme — `1`, `1.1`, `1.2`, `2`, … — preserving the visible cue hierarchy.</summary>
    [RelayCommand(CanExecute = nameof(CanRenumber))]
    private async Task RenumberAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.RenumberSelectionDialogViewModel();
        if (_selectedCueNodes.Count <= 1)
            dialogVm.Scope = Dialogs.RenumberScope.All;
        var dialog = new Views.Dialogs.RenumberSelectionDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.RenumberSelectionDialogResult?>(owner);
        if (result is null) return;

        var renumbered = 0;
        switch (result.Scope)
        {
            case Dialogs.RenumberScope.All:
                renumbered = RenumberSubtree(SelectedCueList.Nodes, result.Start, result.Step, recurseIntoGroups: true);
                break;
            case Dialogs.RenumberScope.RootLevelOnly:
                renumbered = RenumberSubtree(SelectedCueList.Nodes, result.Start, result.Step, recurseIntoGroups: false);
                break;
            case Dialogs.RenumberScope.SelectionOnly:
                renumbered = RenumberFlat(_selectedCueNodes, result.Start, result.Step);
                break;
        }

        StatusMessage = Strings.Format(nameof(Strings.RenumberedStatusFormat), renumbered);
    }

    private bool CanRenumber() => SelectedCueList is not null && SelectedCueList.Nodes.Count > 0;

    /// <summary>Renumbers the rows in <paramref name="nodes"/> in tree order. When
    /// <paramref name="recurseIntoGroups"/> is true, group children get sub-numbers
    /// (parent="1" → children "1.1", "1.2", ...).</summary>
    private static int RenumberSubtree(IReadOnlyList<CueNodeViewModel> nodes, double start, double step, bool recurseIntoGroups)
    {
        var count = 0;
        var n = start;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            node.Number = FormatCueNumber(n);
            count++;
            if (recurseIntoGroups && node.Kind == CueNodeKind.Group && node.Children.Count > 0)
                count += RenumberSubtreePrefixed(node.Children, node.Number, 1.0, 1.0);
            n += step;
        }
        return count;
    }

    private static int RenumberSubtreePrefixed(IReadOnlyList<CueNodeViewModel> children, string prefix, double start, double step)
    {
        var count = 0;
        var n = start;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Number = $"{prefix}.{FormatCueNumber(n)}";
            count++;
            if (child.Kind == CueNodeKind.Group && child.Children.Count > 0)
                count += RenumberSubtreePrefixed(child.Children, child.Number, 1.0, 1.0);
            n += step;
        }
        return count;
    }

    private static int RenumberFlat(IReadOnlyList<CueNodeViewModel> nodes, double start, double step)
    {
        var count = 0;
        var n = start;
        foreach (var node in nodes)
        {
            node.Number = FormatCueNumber(n);
            count++;
            n += step;
        }
        return count;
    }

    private static string FormatCueNumber(double n) =>
        // Drop trailing zero for whole numbers (`1` not `1.0`); keep up to 2 decimals otherwise.
        n == Math.Truncate(n)
            ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

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
            _ = SetPlaybackPausedCallback?.Invoke(false);
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
            return;
        }

        // Resolution order: explicit Standby (operator pressed the Standby button) → currently
        // selected cue (the operator's cursor — natural intent when they pressed Go directly) →
        // first cue in the list. Without the SelectedCueNode tier, pressing Go after clicking
        // anywhere in the tree fires cue 1, which is surprising.
        var fire = StandbyCueNode
                   ?? (SelectedCueNode is not null && ordered.Contains(ResolveFireableCue(SelectedCueNode)!)
                       ? SelectedCueNode
                       : ordered.FirstOrDefault());
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
        _ = SetPlaybackPausedCallback?.Invoke(IsTransportPaused);
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

            // Dispatch without awaiting — for FireAllSimultaneously the per-cue executor takes
            // hundreds of ms (decoder open + MediaPlayer build + router wiring) and awaiting
            // sequentially turns "simultaneously" into "one after another". Each cue's status
            // message lands as its executor completes.
            DispatchCueExecution(step.Cue, ct);
        }
    }

    private void DispatchCueExecution(CueNodeViewModel cue, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                StatusMessage = string.IsNullOrWhiteSpace(exec)
                    ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplay(cue))
                    : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(cue), exec);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                StatusMessage = Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat),
                    CueDisplay(cue), ex.Message);
            }
        }, ct);
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
