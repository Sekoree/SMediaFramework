using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public partial class CuePlayerViewModel : ViewModelBase
{
    public CueHotkeyProfile Hotkeys { get; set; } = new();

    public bool IsNDIAvailable => RuntimeModules.IsNDIAvailable;

    /// <summary>Whether the projectM visualizer is available - gates the per-composition VIZ toggle.</summary>
    public bool IsProjectMAvailable => RuntimeModules.IsProjectMAvailable;

    private CancellationTokenSource? _transportRunCts;

    /// <summary>
    /// Host-provided media execution callback. When null, media cues only update transport state.
    /// </summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueExecutor { get; set; }

    /// <summary>Manual fire for a media cue nested inside a Group. The host assigns a per-cue runtime
    /// transport slot so it can play alongside other children instead of replacing the group's active cue.</summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueIndependentExecutor { get; set; }

    /// <summary>
    /// Host-provided coordinated group execution callback. Opens all cues in parallel, then starts
    /// them in sync. When null, falls back to dispatching each cue independently.
    /// </summary>
    public Func<IReadOnlyList<MediaCueNode>, CancellationToken, Task<string?>>? MediaCueGroupExecutor { get; set; }

    /// <summary>
    /// Host-provided action execution callback. When null, action cues only update transport state.
    /// </summary>
    public Func<ActionCueNode, CancellationToken, Task<string?>>? ActionCueExecutor { get; set; }

    /// <summary>Host-provided visualizer-cue executor (#26): start/stop the projectM layer on a
    /// composition with placement. Wired by <see cref="CueShowSessionCoordinator"/>.</summary>
    public Func<VisualizerCueNode, CancellationToken, Task<string?>>? VisualizerCueExecutor { get; set; }

    /// <summary>Hot-updates the placement of a running visualizer surface. Visualizers are not session
    /// clips, so they need a separate callback from <see cref="UpdateActiveCueVideoPlacementCallback"/>.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveVisualizerPlacementCallback { get; set; }

    /// <summary>Requests a preset advance on the visualizer currently attached to a composition.</summary>
    public Func<Guid, Task<bool>>? NextVisualizerPresetCallback { get; set; }

    /// <summary>Host-provided stop callback - Stop / Panic forwards to this so the playback
    /// engine can tear down its session. Optional; null in tests.</summary>
    public Func<Task>? StopPlaybackCallback { get; set; }

    /// <summary>Host-provided pause callback - Pause/Resume forwards to this so the playback
    /// engine freezes active media instead of only deferring pending cue delays.</summary>
    public Func<bool, Task>? SetPlaybackPausedCallback { get; set; }

    /// <summary>Host-provided preview callbacks (Phase 5.5). Null in tests.</summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? PreviewCueCallback { get; set; }
    public Func<Task>? StopPreviewCallback { get; set; }
    public Func<Guid, TimeSpan, Task>? SeekCueCallback { get; set; }

    private bool MediaExecutionConfigured => MediaCueExecutor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewingSelectedCue))]
    [NotifyPropertyChangedFor(nameof(IsCueScrubberVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(TogglePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekActiveCueFromScrubberCommand))]
    private Guid? _previewingCueId;

    public bool IsPreviewing => PreviewingCueId is not null;

    public bool IsPreviewingSelectedCue =>
        PreviewingCueId is { } id && SelectedCueNode?.Id == id;

    public string PreviewButtonLabel =>
        IsPreviewingSelectedCue ? Strings.StopPreviewCueButton : Strings.PreviewCueButton;

    public ObservableCollection<PreviewAudioDeviceOption> PreviewAudioDevices { get; } = new();

    [ObservableProperty]
    private PreviewAudioDeviceOption? _selectedPreviewAudioDevice;

    partial void OnSelectedPreviewAudioDeviceChanged(PreviewAudioDeviceOption? value) =>
        OnPropertyChanged(nameof(PreviewAudioDeviceIndex));

    public int? PreviewAudioDeviceIndex => SelectedPreviewAudioDevice?.DeviceIndex;

    public void RefreshPreviewAudioDevices()
    {
        PreviewAudioDevices.Clear();
        PreviewAudioDevices.Add(new PreviewAudioDeviceOption(null, Strings.Format(nameof(Strings.DefaultDeviceLabel))));
        // Runs in the MainViewModel ctor - on a machine without the portaudio native library the
        // enumeration throws DllNotFoundException and takes the whole process down before the first
        // frame. MediaRuntime already degrades to other backends; the preview picker must too.
        if (RuntimeModules.IsPortAudioAvailable)
        {
            foreach (var dev in S.Media.Audio.PortAudio.PortAudioDeviceCatalog.EnumerateOutputDevices())
                PreviewAudioDevices.Add(new PreviewAudioDeviceOption(dev.GlobalDeviceIndex, dev.Name));
        }
        SelectedPreviewAudioDevice ??= PreviewAudioDevices.FirstOrDefault();
    }

    private float[]? _selectedCueWaveform;
    private int _selectedCueWaveformRevision;
    private CancellationTokenSource? _waveformCts;

    public float[]? SelectedCueWaveform
    {
        get => _selectedCueWaveform;
        private set { _selectedCueWaveform = value; OnPropertyChanged(); }
    }

    public int SelectedCueWaveformRevision
    {
        get => _selectedCueWaveformRevision;
        private set { _selectedCueWaveformRevision = value; OnPropertyChanged(); }
    }

    public bool HasSelectedCueWaveform =>
        HasSelectedMediaCueWithAudio && SelectedCueWaveform is { Length: > 0 };

    private void ExtractCueWaveform(CueNodeViewModel? cue)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;

        if (cue is not { Kind: CueNodeKind.Media } || !cue.SourceHasAudio)
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        var source = cue.MediaSourceItem;
        var path = source is FilePlaylistItem f ? f.Path : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;
        _ = Task.Run(async () =>
        {
            // Progressive display: throttled partial snapshots fill the editor waveform in left-to-right.
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, ct, partial =>
            {
                if (ct.IsCancellationRequested)
                    return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    SelectedCueWaveform = partial;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            });
            if (!ct.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SelectedCueWaveform = peaks;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            }
        }, ct);
    }

    /// <summary>Visible when the selected cue is active in the Now Playing panel (Phase 5.5.2).</summary>
    public bool IsCueScrubberVisible =>
        SelectedCueNode is not null
        && (ActiveCues.Any(a => a.CueId == SelectedCueNode.Id) || IsPreviewingSelectedCue);

    [ObservableProperty]
    private double _cueScrubberValue;

    public CuePlayerViewModel()
    {
        var initial = new CueListEditorViewModel(Strings.DefaultCueListName);
        CueLists.Add(initial);
        SelectedCueList = initial;
    }

    /// <summary>Wire the cue player to the shared output registry. Audio routes and video output
    /// bindings pick lines from this list directly - no per-cue-list device config.</summary>
    // Video output lines we've hooked for window-resize → PlacementCanvasAspect refresh (see
    // WatchVideoOutputLinesForResize). Held so the handlers can be detached before a rebucket.
    private readonly List<OutputLineViewModel> _resizeWatchedOutputLines = new();

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
            else if (line.Definition is Models.FileOutputDefinition file)
            {
                // Encode lines route like NDI carriers: cues bind video and matrix-route audio onto the
                // pre-defined tracks (the combined sink's concatenated channels). Frames/samples only
                // flow while the line is ARMED - a disarmed line is a silent target, not an error.
                var mode = file.EffectiveEncode.OutputMode;
                if (mode != "VideoOnly")
                    AvailableAudioOutputs.Add(line);
                if (mode != "AudioOnly")
                    AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.LiveStreamOutputDefinition stream)
            {
                var mode = stream.EffectiveEncode.OutputMode;
                if (mode != "VideoOnly")
                    AvailableAudioOutputs.Add(line);
                if (mode != "AudioOnly")
                    AvailableVideoOutputs.Add(line);
            }
        }
        WatchVideoOutputLinesForResize();
        ResolveAllBindingLineRefs();
    }

    // A local output's window resize replaces the line's Definition (OutputManagementViewModel
    // .NotifyLocalPreviewResized); watch each video line so PlacementCanvasAspect re-reads the new window
    // size and the placement canvas re-lays-out to match. Re-subscribed whenever the bucket is rebuilt.
    private void WatchVideoOutputLinesForResize()
    {
        foreach (var line in _resizeWatchedOutputLines)
            line.PropertyChanged -= OnAvailableOutputLineChanged;
        _resizeWatchedOutputLines.Clear();
        foreach (var line in AvailableVideoOutputs)
        {
            line.PropertyChanged += OnAvailableOutputLineChanged;
            _resizeWatchedOutputLines.Add(line);
        }
    }

    private void OnAvailableOutputLineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputLineViewModel.Definition)
            or nameof(OutputLineViewModel.LiveVideoWidth)
            or nameof(OutputLineViewModel.LiveVideoHeight))
            OnPropertyChanged(nameof(PlacementCanvasAspect));
    }

    private OutputLineViewModel? ResolveOutputLine(Guid lineId) =>
        AvailableOutputs.FirstOrDefault(l => l.Definition.Id == lineId);

    /// <summary>Walks every loaded cue list and refreshes the resolved <c>LineRef</c> on each
    /// audio route + video output binding. Called when the available output set changes (lines
    /// added/removed/swapped) so the row dots and tooltips stay accurate.</summary>
    private void ResolveAllBindingLineRefs()
    {
        foreach (var list in CueLists)
        {
            foreach (var binding in list.VideoOutputs)
                binding.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(list.Nodes);
        }
    }

    private void ResolveLineRefsInNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var route in node.AudioRoutes)
                route.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(node.Children);
        }
    }

    public ObservableCollection<CueListEditorViewModel> CueLists { get; } = new();

    public IReadOnlyList<CueEndBehavior> CueEndBehaviors { get; } = Enum.GetValues<CueEndBehavior>();
    public IReadOnlyList<CueTriggerMode> CueTriggerModes { get; } = Enum.GetValues<CueTriggerMode>();
    public IReadOnlyList<CueGroupFireMode> GroupFireModes { get; } = Enum.GetValues<CueGroupFireMode>();
    public IReadOnlyList<CueLayerPosition> LayerPositions { get; } = Enum.GetValues<CueLayerPosition>();

    public IReadOnlyList<TextAlignH> TextHAlignOptions { get; } = Enum.GetValues<TextAlignH>();

    public IReadOnlyList<TextAlignV> TextVAlignOptions { get; } = Enum.GetValues<TextAlignV>();

    /// <summary>Installed system font family names for the text-cue font dropdown. The dropdown actually binds to
    /// the selected node's <c>FontFamilyOptions</c> (which also pins the cue's current family, e.g. the embedded
    /// "Inter" default); this stays for anything that wants the plain system list.</summary>
    public IReadOnlyList<string> AvailableFontFamilies => FontCatalog.SystemFamilies;

    [ObservableProperty]
    private CueListEditorViewModel? _selectedCueList;

    [ObservableProperty]
    private CueNodeViewModel? _selectedCueNode;

    /// <summary>All cue nodes the operator currently has highlighted in the tree (multi-select).
    /// The drawer still shows fields from the singular <see cref="SelectedCueNode"/>, but
    /// "+ Route" / "+ Placement" fan their action out across every media cue in this list - so
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
        ResubscribeMultiEditSelection();
        RefreshMultiEditSelectionState();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    /// <summary>Records an explicit row press even when the operator clicks the already-selected row and the
    /// grid therefore emits no SelectionChanged event.</summary>
    public void MarkCueForNextGo(CueNodeViewModel cue)
    {
        if (IsInCurrentCueTree(cue))
            _selectedCuePendingForGo = true;
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
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private CueNodeViewModel? _standbyCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private CueNodeViewModel? _currentCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private bool _isTransportPaused;

    [ObservableProperty]
    private string? _statusMessage;

    private static readonly TimeSpan StatusMessageAutoClearDelay = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _statusMessageClearCts;

    [ObservableProperty]
    private bool _isCueEditMode = true;

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
    /// <see cref="SetAvailableOutputs"/>. Updates are live - adding/removing in OutputManagement
    /// flows through to the cue player's dropdowns immediately.</summary>
    public ObservableCollection<OutputLineViewModel> AvailableOutputs { get; private set; } = new();

    public ObservableCollection<OutputLineViewModel> AvailableAudioOutputs { get; } = new();
    public ObservableCollection<OutputLineViewModel> AvailableVideoOutputs { get; } = new();

    public ObservableCollection<CueCompositionViewModel> VisibleCompositions =>
        SelectedCueList?.Compositions ?? _emptyCompositions;

    public ObservableCollection<CueVideoOutputBindingViewModel> VisibleVideoOutputs =>
        SelectedCueList?.VideoOutputs ?? _emptyVideoOutputs;

    public ObservableCollection<CueAudioRouteViewModel> VisibleAudioRoutes =>
        SelectedAudioCue?.AudioRoutes ?? _emptyAudioRoutes;

    public ObservableCollection<CueVideoPlacementViewModel> VisibleVideoPlacements =>
        SelectedVideoCue?.VideoPlacements ?? _emptyVideoPlacements;

    /// <summary>Aspect ratio (w/h) of the composition the placement editor canvas should mirror.</summary>
    public double PlacementCanvasAspect
    {
        get
        {
            var comp = SelectedVideoPlacement is { } p
                ? SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                : null;
            comp ??= SelectedComposition ?? SelectedCueList?.Compositions.FirstOrDefault();
            if (comp is null)
                return 16.0 / 9.0;
            // A composition wired to a resizable local window follows that window's live aspect, so the
            // placement canvas matches what the operator actually sees on that output while they drag a
            // resize handle. Re-raised from OnAvailableOutputLineChanged when the window reports a resize.
            return TryGetBoundLocalWindowAspect(comp)
                ?? (comp is { Width: > 0, Height: > 0 } ? (double)comp.Width / comp.Height : 16.0 / 9.0);
        }
    }

    /// <summary>Live w/h of a resizable local window output the composition feeds, or null when the
    /// composition has no bound windowed output (then the canvas uses the composition's own resolution).</summary>
    private double? TryGetBoundLocalWindowAspect(CueCompositionViewModel comp)
    {
        if (SelectedCueList is null)
            return null;
        foreach (var binding in SelectedCueList.VideoOutputs)
        {
            if (binding.CompositionId != comp.Id)
                continue;
            if (binding.LineRef is { LiveVideoWidth: > 0, LiveVideoHeight: > 0 } live)
                return (double)live.LiveVideoWidth.Value / live.LiveVideoHeight.Value;
            if (binding.LineRef?.Definition is Models.LocalVideoOutputDefinition
                { WindowWidth: > 0, WindowHeight: > 0 } lv)
                return (double)lv.WindowWidth.Value / lv.WindowHeight.Value;
        }
        return null;
    }

    public bool HasSelectedMediaCue => SelectedMediaCue is not null;
    public bool HasSelectedTextCue => SelectedTextCue is not null;

    /// <summary>Image/text cues have no inherent length, so the operator sets the hold duration directly.</summary>
    public bool HasSelectedStaticCue =>
        SelectedStaticCue is not null;
    public bool HasSelectedActionCue => SelectedActionCue is not null;
    public bool HasSelectedCommentCue => SelectedCommentCue is not null;
    public bool HasSelectedGroupCue => SelectedGroupCue is not null;
    public bool HasSelectedCue => SelectedCueNode is not null;

    /// <summary>Video tab visibility: media cue AND the source actually has a video stream
    /// (decodable - covers regular video files and audio files with attached picture cover art).</summary>
    public bool HasSelectedMediaCueWithVideo =>
        SelectedVideoCue is not null;

    /// <summary>Audio tab visibility: media cue AND (the probe found audio OR the cue already
    /// has routes wired). The "has routes" branch keeps the tab editable for pre-Phase-5.1 cues
    /// that never went through the audio-stream probe but already have routes saved on disk.</summary>
    public bool HasSelectedMediaCueWithAudio =>
        SelectedAudioCue is not null;

    /// <summary>Operator hint banner - true when the only "video" the source offers is an
    /// attached picture (e.g. MP3 album art). The Video tab still works (the still frame can be
    /// placed into a composition for a now-playing slate) but it's worth flagging.</summary>
    public bool HasSelectedMediaCueWithAttachedPictureOnly =>
        SelectedVideoCue is { Kind: CueNodeKind.Media, SourceVideoIsAttachedPicture: true };

    /// <summary>Non-null when the selected media cue's probed frame rate doesn't divide evenly
    /// into at least one wired composition's canvas rate (Phase 5.9.2).</summary>
    public string? VideoFrameRateMismatchWarning => BuildVideoFrameRateMismatchWarning();

    public bool HasVideoFrameRateMismatchWarning =>
        !string.IsNullOrWhiteSpace(VideoFrameRateMismatchWarning);

    /// <summary>How many cues the operator currently has highlighted in the tree. The drawer
    /// shows a banner above the routes/placements lists when this is > 1 so the operator knows
    /// that "+ Route" / "+ Placement" applies to all of them, not just the primary.</summary>
    public int SelectedCueCount => _selectedCueNodes.Count;

    /// <summary>True iff <see cref="SelectedCueCount"/> > 1. Bound as the banner visibility flag -
    /// Avalonia's <c>ObjectConverters</c> doesn't ship a <c>GreaterThan</c>, so we expose a
    /// dedicated boolean rather than wire a per-view converter.</summary>
    public bool IsMultiSelected => _selectedCueNodes.Count > 1;

    public string SelectedCueDrawerTitle => SelectedCueNode is null
        ? Strings.SelectACueDrawerHint
        : string.IsNullOrWhiteSpace(SelectedCueNode.Number)
            ? $"{SelectedCueNode.Label} - {SelectedCueNode.KindLabel}"
            : $"{SelectedCueNode.Number} {SelectedCueNode.Label} - {SelectedCueNode.KindLabel}";
    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();

    public string SelectedActionEndpointSummary
    {
        get
        {
            if (SelectedActionCue is not { } actionCue)
                return string.Empty;

            if (!Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
                return Strings.NoActionTargetSelected;

            return SelectedActionEndpoint is null
                ? Strings.Format(nameof(Strings.ActionTargetMissingFormat), endpointId)
                : Strings.Format(
                    nameof(Strings.SelectedActionTargetFormat),
                    SelectedActionEndpoint.Name,
                    SelectedActionEndpoint.KindLabel,
                    SelectedActionEndpoint.Summary);
        }
    }

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

    /// <summary>Fill for the transport-state chip - same palette as the media deck's state pill:
    /// green running, amber paused or standby-armed (ready), translucent gray idle.</summary>
    public Avalonia.Media.ISolidColorBrush TransportStateColor =>
        CurrentCueNode is null
            ? StandbyCueNode is null
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#44808080"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"))
            : IsTransportPaused
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2E7D32"));

    /// <summary>UX-10: true when the selected list has no cues (or none selected) - drives the empty-state
    /// call-to-action over the cue tree instead of a blank grid.</summary>
    public bool HasNoCues => SelectedCueList is null || SelectedCueList.Nodes.Count == 0;

    private CueListEditorViewModel? _watchedCueListForNodes;

    private void ResubscribeCueNodesWatch(CueListEditorViewModel? value)
    {
        if (_watchedCueListForNodes is not null)
            _watchedCueListForNodes.Nodes.CollectionChanged -= OnCueNodesCollectionChanged;
        _watchedCueListForNodes = value;
        if (value is not null)
            value.Nodes.CollectionChanged += OnCueNodesCollectionChanged;
    }

    private void OnCueNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(HasNoCues));
        RefreshCueTargetDisplays();
    }

    partial void OnSelectedCueListChanged(CueListEditorViewModel? value)
    {
        CancelTransportRun();
        ResubscribeCueNodesWatch(value);
        OnPropertyChanged(nameof(HasNoCues));
        OnPropertyChanged(nameof(VisibleNodes));
        OnPropertyChanged(nameof(VisibleCompositions));
        OnPropertyChanged(nameof(VisibleVideoOutputs));
        SelectedComposition = value?.Compositions.FirstOrDefault();
        SelectedVideoOutput = value?.VideoOutputs.FirstOrDefault();
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RemoveCueListCommand.NotifyCanExecuteChanged();
        OpenCueOutputSetupCommand.NotifyCanExecuteChanged();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        ResubscribeCompositionFpsWatch(value);
        RefreshCueTargetDisplays();
    }

    private void RefreshCueTargetDisplays()
    {
        var nodes = EnumerateAllCueNodes().ToList();
        var byId = nodes.ToDictionary(node => node.Id);
        static string CueReference(CueNodeViewModel cue) =>
            string.IsNullOrWhiteSpace(cue.Number) ? cue.Label : $"#{cue.Number}";

        foreach (var node in nodes)
        {
            if (node.EndTargetCueId is { } endId)
            {
                node.TargetDisplay = byId.TryGetValue(endId, out var target)
                    ? $"End → {CueReference(target)}"
                    : "End → ?";
                continue;
            }

            if (node.Kind == CueNodeKind.Jump && node.JumpTargetIds.Count > 0)
            {
                var targets = string.Join(", ", node.JumpTargetIds.Select(id =>
                {
                    if (!byId.TryGetValue(id, out var target))
                        return "?";
                    var immediateCycle = ReferenceEquals(ResolveFireableCue(target), node);
                    return $"{CueReference(target)}{(immediateCycle ? " ⚠ cycle" : string.Empty)}";
                }));
                var mode = node.JumpRandom
                    ? node.JumpAvoidImmediateRepeat ? "Random (no repeat)" : "Random"
                    : string.Equals(node.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase)
                        ? "Standby"
                        : "Jump";
                node.TargetDisplay = $"{mode} → {targets}";
                continue;
            }

            node.TargetDisplay = string.Empty;
        }
    }

    private CueListEditorViewModel? _watchedCueListForFps;

    private void ResubscribeCompositionFpsWatch(CueListEditorViewModel? value)
    {
        if (_watchedCueListForFps is not null)
        {
            foreach (var comp in _watchedCueListForFps.Compositions)
                comp.CompositionFrameRateChanged -= OnCompositionFrameRateChanged;
        }

        _watchedCueListForFps = value;
        if (value is null)
            return;

        foreach (var comp in value.Compositions)
            comp.CompositionFrameRateChanged += OnCompositionFrameRateChanged;
        RefreshVideoFrameRateMismatchWarning();
    }

    private void OnCompositionFrameRateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshVideoFrameRateMismatchWarning();
    }

    private CueNodeViewModel? _watchedSelectedCueForProbe;

    private void OnSelectedCueProbeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem)
            or nameof(CueNodeViewModel.SourceCapabilitiesKnown)
            or nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture)
            or nameof(CueNodeViewModel.SourceFrameRateNum)
            or nameof(CueNodeViewModel.SourceFrameRateDen))
        {
            OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
            OnPropertyChanged(nameof(HasSelectedTextCue));
            OnPropertyChanged(nameof(HasSelectedStaticCue));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
            OnPropertyChanged(nameof(IsPreviewingSelectedCue));
            OnPropertyChanged(nameof(PreviewButtonLabel));
            OnPropertyChanged(nameof(IsCueScrubberVisible));
            RefreshVideoFrameRateMismatchWarning();
            SyncCueScrubberFromActiveSelection();
            TogglePreviewCommand.NotifyCanExecuteChanged();
            SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(CueNodeViewModel.SourceHasAudio))
                ExtractCueWaveform(_watchedSelectedCueForProbe);
            RefreshMultiEditSelectionState(resetSelectedItems:
                e.PropertyName is not nameof(CueNodeViewModel.MediaSourceItem));
        }
        else if (e.PropertyName is nameof(CueNodeViewModel.HasSubtitleTracks))
        {
            RefreshMultiEditSelectionState();
        }
    }

    private CueNodeViewModel? _preRollWatchedCue;

    /// <summary>Tracks the selected media/visualizer cue so that in-place edits to routes and placements
    /// can reach the live runtime. Media edits also re-warm standby pre-roll.</summary>
    private void WatchSelectedCueForPreRoll(CueNodeViewModel? value)
    {
        var next = value is { Kind: CueNodeKind.Media or CueNodeKind.Visualizer } ? value : null;
        if (ReferenceEquals(_preRollWatchedCue, next))
            return;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged -= OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged -= OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged -= OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        }

        _preRollWatchedCue = next;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged += OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged += OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged += OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
        }
    }

    private void OnWatchedCuePreRollPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.StartOffsetMs)
            or nameof(CueNodeViewModel.EndOffsetMs)
            or nameof(CueNodeViewModel.Loop)
            or nameof(CueNodeViewModel.EndBehavior)
            or nameof(CueNodeViewModel.DurationMs)        // image/text duration drives the hold window
            or nameof(CueNodeViewModel.MediaSourceItem)   // text restyle replaces the source -> re-render
            or nameof(CueNodeViewModel.AudioTrackIndex)   // track change is part of the prepared-cue key
            or nameof(CueNodeViewModel.VideoTrackIndex))  // ditto for the video stream selection
            OnWatchedCueEdited();

        // A text/style edit replaces the TextPlaylistItem source; if that cue is playing, re-render its frame in
        // place so the change shows immediately (the deferred document rebuild otherwise only lands on the next
        // fire - see MainViewModel's reload deferral, which keeps the running cue from being torn down mid-edit).
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem))
            PushActiveTextUpdate();
    }

    private static readonly Microsoft.Extensions.Logging.ILogger LiveTextTrace =
        S.Media.Core.Diagnostics.MediaDiagnostics.CreateLogger("HaPlay.LiveText");

    private void PushActiveTextUpdate()
    {
        var watched = _preRollWatchedCue;
        var isText = watched?.MediaSourceItem is TextPlaylistItem;
        var isActive = watched is not null && _activeCueIds.Contains(watched.Id);
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(LiveTextTrace,
            "PushActiveTextUpdate: watched={Watched} isText={IsText} isActive={IsActive} hasCallback={HasCb} activeCount={Count}",
            watched?.Id, isText, isActive, UpdateActiveCueTextCallback is not null, _activeCueIds.Count);

        if (watched is { } cue
            && isText
            && UpdateActiveCueTextCallback is { } callback
            && isActive
            && cue.ToModel() is MediaCueNode model)
            _ = callback(cue.Id, model);
    }

    private void OnWatchedCueRouteCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        PushActiveAudioRoutesUpdate();
        // Add/Remove route commands already suggest a refresh, but a programmatic edit might not.
        OnWatchedCueEdited();
    }

    private void OnWatchedCuePlacementCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        OnWatchedCueEdited();
    }

    private void RebindItemSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var item in e.OldItems.OfType<ObservableObject>())
                item.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<ObservableObject>())
                item.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
    }

    private void OnWatchedRouteOrPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // LineRef is a resolved UI reference, not part of the cue's cache key - ignore it so a mere
        // output-line resolution doesn't churn pre-roll.
        if (e.PropertyName is nameof(CueAudioRouteViewModel.SourceChannel)
            or nameof(CueAudioRouteViewModel.OutputLineId)
            or nameof(CueAudioRouteViewModel.OutputChannel)
            or nameof(CueAudioRouteViewModel.GainDb)
            or nameof(CueAudioRouteViewModel.Muted))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (sender is CueVideoPlacementViewModel placement
            && IsVideoPlacementProperty(e.PropertyName))
        {
            if (IsLiveEditableVideoPlacementProperty(e.PropertyName))
                PushActiveVideoPlacementUpdate(placement);
            RefreshVideoFrameRateMismatchWarning();
        }
    }

    private static bool IsVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.CompositionId)
            or nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled);

    private static bool IsLiveEditableVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled);

    /// <summary>Maps a cue-wide placement index to the placement's index AMONG THE CUE'S PLACEMENTS ON
    /// THE SAME COMPOSITION - the order the visualizer executor attached that composition's surface
    /// layers in, and therefore the index the live hot-update API addresses (#26 multi-placement).</summary>
    private static int VisualizerPlacementIndexOnComposition(CueNodeViewModel cue, int placementIndex)
    {
        var compositionId = cue.VideoPlacements[placementIndex].CompositionId;
        var indexOnComposition = 0;
        for (var i = 0; i < placementIndex; i++)
            if (cue.VideoPlacements[i].CompositionId == compositionId)
                indexOnComposition++;
        return indexOnComposition;
    }

    private void PushActiveVideoPlacementUpdate(CueVideoPlacementViewModel placement)
    {
        if (_preRollWatchedCue is not { } cue)
            return;

        var index = cue.VideoPlacements.IndexOf(placement);
        if (index < 0)
            return;

        // A visualizer is a persistent composition surface, not an active ShowSession clip. Its persistent
        // runtime latch outlives a finite Now-Playing row, and its layer has its own hot-update API.
        if (cue.Kind == CueNodeKind.Visualizer)
        {
            if (_runningVisualizers.ContainsKey(cue.Id)
                && UpdateActiveVisualizerPlacementCallback is { } visualizerCallback)
                _ = visualizerCallback(cue.Id, VisualizerPlacementIndexOnComposition(cue, index), placement.ToModel());
            return;
        }

        // Not running yet: the edited placement lives only in the cue model, and the backing ShowSession
        // document a GO fires from is NOT rebuilt on placement edits (only structural changes reload it).
        // Flag it stale so the next fire reloads with the current placement - otherwise the cue fires with
        // the placement captured at the last reload and the new geometry only takes hold once the operator
        // nudges it again (which then takes the live path below). A running cue is updated live instead.
        if (!_activeCueIds.Contains(cue.Id))
        {
            CueClipModelStaleCallback?.Invoke();
            return;
        }

        if (UpdateActiveCueVideoPlacementCallback is not { } callback)
            return;

        _ = callback(cue.Id, index, placement.ToModel());
    }

    private void PushActiveAudioRoutesUpdate()
    {
        if (_preRollWatchedCue is not { } cue
            || UpdateActiveCueAudioRoutesCallback is not { } callback
            || !_activeCueIds.Contains(cue.Id))
            return;

        var routes = cue.AudioRoutes.Select(route => route.ToModel()).ToArray();
        _ = callback(cue.Id, routes);
    }

    /// <summary>An edit-relevant change to the watched (selected) cue: immediately flag its warm
    /// standby <see cref="PreparedCueState.Stale"/> so the badge reflects the drift, then request a
    /// debounced pre-roll refresh that re-prepares it.</summary>
    private void OnWatchedCueEdited()
    {
        if (_preRollWatchedCue is { } cue)
            CueStandbyInvalidated?.Invoke(this, cue.Id);
        SuggestPreRollRefresh();
    }

    /// <summary>Raised with a cue id when an in-place edit drifts that cue's warm standby out of date.
    /// The host marks the engine's prepared entry stale; the following refresh re-prepares it.</summary>
    public event EventHandler<Guid>? CueStandbyInvalidated;

    private void RefreshVideoFrameRateMismatchWarning()
    {
        OnPropertyChanged(nameof(VideoFrameRateMismatchWarning));
        OnPropertyChanged(nameof(HasVideoFrameRateMismatchWarning));
    }

    private string? BuildVideoFrameRateMismatchWarning()
    {
        if (SelectedVideoCue is not { Kind: CueNodeKind.Media } node || !node.SourceHasVideo)
            return null;
        if (!CueFrameRatePolicy.IsKnown(node.SourceFrameRateNum, node.SourceFrameRateDen))
            return null;
        if (SelectedCueList is null)
            return null;

        foreach (var placement in node.VideoPlacements)
        {
            var comp = SelectedCueList.Compositions.FirstOrDefault(c => c.Id == placement.CompositionId);
            if (comp is null)
                continue;
            if (!CueFrameRatePolicy.RatesMismatch(
                    node.SourceFrameRateNum, node.SourceFrameRateDen,
                    comp.FrameRateNum, comp.FrameRateDen))
                continue;

            var srcFps = FormatProbeFps(node.SourceFrameRateNum, node.SourceFrameRateDen);
            var canvasFps = FormatProbeFps(comp.FrameRateNum, comp.FrameRateDen);
            return Strings.Format(
                nameof(Strings.VideoFrameRateMismatchWarningFormat),
                srcFps,
                canvasFps,
                comp.DisplayName);
        }

        return null;
    }

    private static string FormatProbeFps(int num, int den)
    {
        if (den <= 0)
            return "?";
        var fps = num / (double)den;
        return fps >= 100 ? fps.ToString("0.#") : fps.ToString("0.###");
    }

    /// <summary>Drawer-editable cue number with the same uniqueness rule as the rename dialog: a
    /// duplicate anywhere in the list is rejected (number-based jump/feed references need it).</summary>
    public string SelectedCueNumber
    {
        get => SelectedCueNode?.Number ?? string.Empty;
        set
        {
            if (SelectedCueNode is not { } cue)
                return;
            var trimmed = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && EnumerateAllCueNodes().Any(c => !ReferenceEquals(c, cue)
                    && string.Equals(c.Number?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = Strings.Format(nameof(Strings.CueNumberDuplicateFormat), trimmed);
                OnPropertyChanged(nameof(SelectedCueNumber));
                OnPropertyChanged(nameof(SelectedEndTargetText)); // revert the editor to the old value
                return;
            }

            cue.Number = trimmed;
            StatusMessage = null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedCueNumber));
            OnPropertyChanged(nameof(SelectedEndTargetText));
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    public string SelectedCueLabel
    {
        get => SelectedCueNode?.Label ?? string.Empty;
        set
        {
            if (SelectedCueNode is { } cue)
                cue.Label = value ?? string.Empty;
            OnPropertyChanged(nameof(SelectedCueLabel));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    /// <summary>Visualizer-cue feed sources as cue numbers (same pattern as jump targets).</summary>
    public string SelectedVisualizerFeedText
    {
        get
        {
            if (SelectedVisualizerCue is not { } viz)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", viz.VisualizerFeedCueIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedVisualizerCue is not { } viz)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var token in tokens)
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    c.Kind == CueNodeKind.Media
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !resolved.Contains(match.Id))
                    resolved.Add(match.Id);
                else if (match is null)
                    unknown.Add(token);
            }

            viz.VisualizerFeedCueIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Visualizer))
                if (!ReferenceEquals(target, viz))
                    target.VisualizerFeedCueIds = [.. resolved];
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        }
    }

    public bool SelectedVisualizerFeedAll
    {
        get => SelectedVisualizerCue is not { } v || v.VisualizerFeedAll;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerFeedAll = value;
                OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
            }
        }
    }

    /// <summary>Media end-trigger target (on natural end), entered by number and stored as a stable id.</summary>
    public string SelectedEndTargetText
    {
        get
        {
            if (SelectedMediaCue is not { } m || m.EndTargetCueId is not { } id)
                return string.Empty;
            var target = EnumerateAllCueNodes().FirstOrDefault(c => c.Id == id);
            return target is null ? "?" : (string.IsNullOrWhiteSpace(target.Number) ? target.Label : target.Number);
        }
        set
        {
            if (SelectedMediaCue is not { } m)
                return;
            var token = (value ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                m.EndTargetCueId = null;
            }
            else
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    !ReferenceEquals(c, m) && c.Kind is not CueNodeKind.Comment
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    StatusMessage = Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), token);
                }
                else
                {
                    m.EndTargetCueId = match.Id;
                    StatusMessage = null;
                }
            }

            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedEndTargetText));
        }
    }

    /// <summary>Visualizer timeline duration in seconds (0 = infinite).</summary>
    public double SelectedVisualizerDurationSeconds
    {
        get => SelectedVisualizerCue is { } v ? v.VisualizerDurationMs / 1000.0 : 0;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerDurationMs = (int)Math.Clamp(value * 1000.0, 0, int.MaxValue);
                OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
            }
        }
    }

    /// <summary>Visualizer-cue resolution preset ("WxH@F") - the media player dialog's buttons.</summary>
    [RelayCommand]
    private void SetVisualizerCueResolution(string spec)
    {
        if (SelectedVisualizerCue is not { } viz)
            return;
        var parts = spec.Split(['x', '@']);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var w)
            || !int.TryParse(parts[1], out var h)
            || !int.TryParse(parts[2], out var f))
            return;
        viz.VisualizerRenderWidth = w;
        viz.VisualizerRenderHeight = h;
        viz.VisualizerRenderFps = f;
        OnPropertyChanged(nameof(SelectedVisualizerCue)); // refresh the numerics
    }

    [RelayCommand(CanExecute = nameof(CanNextVisualizerPreset))]
    private async Task NextVisualizerPresetAsync()
    {
        if (SelectedVisualizerCue is not { } cue
            || NextVisualizerPresetCallback is not { } callback)
            return;

        var compositionId = VisualizerTargetComposition(cue);
        if (compositionId != Guid.Empty)
            await callback(compositionId);
    }

    private bool CanNextVisualizerPreset() =>
        SelectedVisualizerCue is not null
        && NextVisualizerPresetCallback is not null;

    /// <summary>Drawer gate for the jump-cue section.</summary>
    public bool IsJumpCueSelected => SelectedJumpCue is not null;

    /// <summary>Drawer gate for the visualizer-cue section (#26).</summary>
    public bool IsVisualizerCueSelected => SelectedVisualizerCue is not null;

    public Guid SelectedVisualizerCompositionId
    {
        get => SelectedVisualizerCue?.VisualizerCompositionId ?? Guid.Empty;
        set
        {
            if (SelectedVisualizerCue is { } v)
                v.VisualizerCompositionId = value;
        }
    }

    public bool SelectedVisualizerStarts
    {
        get => SelectedVisualizerCue is { } v
               && !string.Equals(v.Extra, "Stop", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.Extra = value ? "Start" : "Stop";
                OnPropertyChanged(nameof(SelectedVisualizerStarts));
            }
        }
    }

    /// <summary>Jump targets as CUE NUMBERS (display/entry); stored as stable cue IDs (renumber-safe).
    /// Setting parses a comma/space separated list (including inclusive hierarchical ranges such as
    /// 2.2-2.4), resolves each number against the list, keeps the resolvable ones, and reports unknowns
    /// in the status bar.</summary>
    public string SelectedJumpTargetsText
    {
        get
        {
            if (SelectedJumpCue is not { } jump)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", jump.JumpTargetIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedJumpCue is not { } jump)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var enteredToken in tokens)
            {
                foreach (var token in ExpandCueNumberToken(enteredToken))
                {
                    var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                        !ReferenceEquals(c, jump)
                        && c.Kind is not CueNodeKind.Comment
                        && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !resolved.Contains(match.Id))
                        resolved.Add(match.Id);
                    else if (match is null)
                        unknown.Add(token);
                }
            }

            jump.JumpTargetIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                if (!ReferenceEquals(target, jump))
                    target.JumpTargetIds = [.. resolved];
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                _lastRandomJumpTargetIds.Remove(target.Id);
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
        }
    }

    private const int MaximumCueNumberRangeSize = 10_000;

    /// <summary>
    /// Expands only unambiguous numeric, dot-hierarchical ranges. Other hyphenated values remain
    /// literal cue numbers, so a custom number such as "intro-1" retains its existing meaning.
    /// </summary>
    private static IReadOnlyList<string> ExpandCueNumberToken(string token)
    {
        var separator = token.IndexOf('-');
        if (separator <= 0
            || separator != token.LastIndexOf('-')
            || separator >= token.Length - 1
            || !TryParseHierarchicalCueNumber(token[..separator], out var start)
            || !TryParseHierarchicalCueNumber(token[(separator + 1)..], out var end)
            || start.Length != end.Length)
            return [token];

        for (var i = 0; i < start.Length - 1; i++)
        {
            if (start[i] != end[i])
                return [token];
        }

        var distance = Math.Abs((long)end[^1] - start[^1]);
        if (distance >= MaximumCueNumberRangeSize)
            return [token];

        var prefix = start.Length == 1
            ? string.Empty
            : $"{string.Join('.', start[..^1])}.";
        var step = start[^1] <= end[^1] ? 1 : -1;
        var expanded = new List<string>((int)distance + 1);
        for (var value = start[^1];; value += step)
        {
            expanded.Add($"{prefix}{value.ToString(CultureInfo.InvariantCulture)}");
            if (value == end[^1])
                break;
        }

        return expanded;
    }

    private static bool TryParseHierarchicalCueNumber(string text, out int[] components)
    {
        var parts = text.Split('.');
        components = new int[parts.Length];
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        return true;
    }

    public bool SelectedJumpRandom
    {
        get => SelectedJumpCue is { } j && j.JumpRandom;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpRandom = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpRandom));
            }
        }
    }

    public bool SelectedJumpAvoidImmediateRepeat
    {
        get => SelectedJumpCue is { } j && j.JumpAvoidImmediateRepeat;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpAvoidImmediateRepeat = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
            }
        }
    }

    public bool SelectedJumpFiresTarget
    {
        get => SelectedJumpCue is { } j
               && !string.Equals(j.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.SourceOrAction = value ? "fire" : "standby";
                RefreshCueTargetDisplays();
            }
        }
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        // Programmatic selections (add/duplicate/load) do not necessarily pass through
        // UpdateSelection. Keep the multi-edit subscriptions aligned in that path too.
        ResubscribeMultiEditSelection();
        _selectedCuePendingForGo = value is not null;
        OnPropertyChanged(nameof(SelectedCueNumber));
        OnPropertyChanged(nameof(SelectedEndTargetText));
        OnPropertyChanged(nameof(SelectedCueLabel));
        OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
        OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
        OnPropertyChanged(nameof(IsJumpCueSelected));
        OnPropertyChanged(nameof(IsVisualizerCueSelected));
        OnPropertyChanged(nameof(SelectedVisualizerCompositionId));
        OnPropertyChanged(nameof(SelectedVisualizerStarts));
        OnPropertyChanged(nameof(SelectedJumpTargetsText));
        OnPropertyChanged(nameof(SelectedJumpRandom));
        OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
        OnPropertyChanged(nameof(SelectedJumpFiresTarget));
        // The selected cue's probe fields can land AFTER selection (when the operator picks a
        // file via "Browse media…"; the probe is async). Re-subscribe so the Video tab visibility
        // re-evaluates when the probe finishes.
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged -= OnSelectedCueProbeChanged;
        _watchedSelectedCueForProbe = value;
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged += OnSelectedCueProbeChanged;

        // In-place edits to the selected cue's routes/placements/offsets don't go through the
        // add/remove commands (those already suggest a refresh), so watch the node directly to keep
        // its standby pre-roll warm after gain/channel/opacity/offset tweaks. Debounced downstream.
        WatchSelectedCueForPreRoll(value);

        // Cues loaded from disk have no probed track list yet - fill the audio-track picker lazily
        // on first selection (stream-table probe only, no decoder build).
        if (value is { Kind: CueNodeKind.Media })
            _ = EnsureAudioTrackChoicesAsync(value);

        SelectedAudioRoute = SelectedAudioCue?.AudioRoutes.FirstOrDefault();
        SelectedVideoPlacement = SelectedVideoCue?.VideoPlacements.FirstOrDefault();
        RemoveNodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedTextCue));
        OnPropertyChanged(nameof(HasSelectedStaticCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(HasSelectedCue));
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        ApplyCueDownmixPresetCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        FireSelectedVisualizerCommand.NotifyCanExecuteChanged();
        FireSelectedCueNowCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
        NextVisualizerPresetCommand.NotifyCanExecuteChanged();
        BrowseMediaSourceCommand.NotifyCanExecuteChanged();
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
        EditActionCueCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPreviewingSelectedCue));
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        RefreshVideoFrameRateMismatchWarning();
        ExtractCueWaveform(value);
        RefreshMultiEditSelectionState(resetSelectedItems: false);

        if (SelectedActionCue is { } actionCue && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        else
            SelectedActionEndpoint = null;
    }

    partial void OnSelectedAudioRouteChanged(CueAudioRouteViewModel? value)
    {
        WatchSelectedAudioRouteForMultiEdit(value);
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoPlacementChanged(CueVideoPlacementViewModel? value)
    {
        WatchSelectedVideoPlacementForMultiEdit(value);
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        ApplyPlacementLayoutCommand.NotifyCanExecuteChanged();
        EditSelectedPlacementVideoFxCommand.NotifyCanExecuteChanged();
        ApplyCropPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
        RefreshVideoFrameRateMismatchWarning();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCueEditModeChanged(bool value)
    {
        _ = value;
        MoveSelectedCueUpCommand.NotifyCanExecuteChanged();
        MoveSelectedCueDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnStandbyCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        RebuildUpcomingCues();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        if (!_suppressStandbyPreRollRefresh)
            SuggestPreRollRefresh();
    }

    /// <summary>Host subscribes to warm the selected player's pre-roll cache (§5.7).</summary>
    public event EventHandler? PreRollRefreshSuggested;

    private void SuggestPreRollRefresh() => PreRollRefreshSuggested?.Invoke(this, EventArgs.Empty);

    private bool _suppressStandbyPreRollRefresh;

    /// <summary>The fireable cue order starting at standby (or list start) - the window each
    /// pre-roll query pulls its targets from. Callers apply a per-source-type filter, so a
    /// non-matching cue (e.g. an NDI cue while scanning for files) is skipped without changing
    /// the file-media target set.</summary>
    private IEnumerable<CueNodeViewModel> EnumeratePreRollWindow()
    {
        if (SelectedCueList is null)
            yield break;

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            yield break;

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        for (var i = startIdx; i < ordered.Count; i++)
            yield return ordered[i];
    }

    private IReadOnlyList<CueNodeViewModel> GetStandbySimultaneousGroupTargets()
    {
        if (StandbyCueNode is not { Kind: CueNodeKind.Group } group
            || ParseGroupFireMode(group) != CueGroupFireMode.FireAllSimultaneously)
            return [];

        return BuildTriggerPlan(group).Select(step => step.Cue).ToList();
    }

    /// <summary>Next file media cues from standby for the cue engine's own opened/routed cache.</summary>
    public IReadOnlyList<MediaCueNode> GetPreparedMediaCueTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<MediaCueNode>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not FilePlaylistItem
                    || cue.ToModel() is not MediaCueNode media)
                    continue;
                groupTargets.Add(media);
            }

            return groupTargets;
        }

        var targets = new List<MediaCueNode>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not FilePlaylistItem
                || cue.ToModel() is not MediaCueNode media)
                continue;
            targets.Add(media);
        }

        return targets;
    }

    /// <summary>NDI media cues in the pre-roll window (§6.11).</summary>
    public IReadOnlyList<(Guid CueId, NDIInputPlaylistItem Item)> GetNDIPreConnectTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<(Guid, NDIInputPlaylistItem)>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not NDIInputPlaylistItem ndi
                    || !ndi.SupportsPreRoll())
                    continue;
                groupTargets.Add((cue.Id, ndi));
            }

            return groupTargets;
        }

        var targets = new List<(Guid, NDIInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
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
    /// cue lights up - the singular <see cref="CurrentCueNode"/> only tracks the last-started
    /// one for AutoFollow / transport-state purposes.</summary>
    private readonly HashSet<Guid> _activeCueIds = new();

    /// <summary>True while any cue is currently playing (fired via <see cref="OnCueStarted"/>, not yet
    /// <see cref="OnCueEnded"/>). The authoritative "is something playing" signal - used to defer the ShowSession
    /// document rebuild on an in-place edit so it never stops a running cue (unlike a clock's <c>IsRunning</c>,
    /// which is unreliable for a video-only held/text clip).</summary>
    public bool HasActiveCues => _activeCueIds.Count > 0;

    /// <summary>Rows visible in the right-side Now Playing panel. Maintained by
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/>; their progress fields update via
    /// <see cref="OnCueProgress"/>.</summary>
    public ObservableCollection<ActiveCueViewModel> ActiveCues { get; } = new();

    /// <summary>
    /// P4 (plan §3.2) - what the Now Playing panel renders: <see cref="ActiveCueViewModel"/> for
    /// standalone cues, <see cref="ActiveGroupViewModel"/> aggregating active cues that share a
    /// parent group node. <see cref="ActiveCues"/> stays the flat source of truth.
    /// </summary>
    public ObservableCollection<object> NowPlayingRows { get; } = new();

    /// <summary>Host-provided coordinated multi-cue seek (engine.SeekCuesAsync): all targets pause,
    /// seek in parallel and resume through one barrier so group children stay aligned. When null
    /// (tests), group seeks fall back to sequential per-cue <see cref="SeekCueCallback"/> calls.</summary>
    public Func<IReadOnlyList<(Guid CueId, TimeSpan Position)>, Task>? SeekCuesCallback { get; set; }

    /// <summary>Group-row seek: every child seeks to the same fraction of ITS OWN duration (keeps
    /// proportional alignment for staggered-length children). Same padlock gate as single rows.</summary>
    public async Task SeekActiveGroupToFractionAsync(ActiveGroupViewModel group, double fraction)
    {
        if (!NowPlayingSeekUnlocked)
            return;

        if (SeekCuesCallback is { } batched)
        {
            var clamped = Math.Clamp(fraction, 0.0, 1.0);
            var targets = group.Children
                .Where(child => child.DurationMs > 0)
                .Select(child => (child.CueId, TimeSpan.FromMilliseconds(child.DurationMs * clamped)))
                .ToList();
            if (targets.Count > 0)
                await batched(targets).ConfigureAwait(false);
            return;
        }

        foreach (var child in group.Children.ToArray())
            await SeekActiveCueToFractionAsync(child, fraction).ConfigureAwait(false);
    }

    /// <summary>The group node a cue sits under, or null for top-level cues. Searches the selected
    /// cue list's tree (active cues always come from the visible list).</summary>
    private CueNodeViewModel? FindParentGroupOf(CueNodeViewModel node)
    {
        if (SelectedCueList is null)
            return null;
        return Search(SelectedCueList.Nodes, null);

        CueNodeViewModel? Search(IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel? parent)
        {
            foreach (var candidate in nodes)
            {
                if (ReferenceEquals(candidate, node))
                    return parent is { IsGroup: true } ? parent : null;
                if (Search(candidate.Children, candidate) is { } found)
                    return found;
            }

            return null;
        }
    }

    private void AddNowPlayingRow(ActiveCueViewModel entry)
    {
        if (FindParentGroupOf(entry.Node) is { } groupNode)
        {
            var group = NowPlayingRows.OfType<ActiveGroupViewModel>()
                .FirstOrDefault(g => g.GroupId == groupNode.Id);
            if (group is null)
            {
                group = new ActiveGroupViewModel(groupNode);
                NowPlayingRows.Add(group);
            }

            group.Children.Add(entry);
            return;
        }

        NowPlayingRows.Add(entry);
    }

    private void RemoveNowPlayingRow(Guid cueId)
    {
        for (var i = NowPlayingRows.Count - 1; i >= 0; i--)
        {
            switch (NowPlayingRows[i])
            {
                case ActiveCueViewModel single when single.CueId == cueId:
                    NowPlayingRows.RemoveAt(i);
                    break;
                case ActiveGroupViewModel group:
                    for (var c = group.Children.Count - 1; c >= 0; c--)
                        if (group.Children[c].CueId == cueId)
                            group.Children.RemoveAt(c);
                    if (group.Children.Count == 0)
                        NowPlayingRows.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>Cues that *will* fire once the operator presses Go from the current Standby
    /// position - used by the Now Playing panel's Upcoming section.</summary>
    public ObservableCollection<CueNodeViewModel> UpcomingCues { get; } = new();

    /// <summary>Host-provided per-cue stop callback (engine.StopCueAsync). The Now Playing
    /// panel's per-row ✕ button forwards through this; null in tests.</summary>
    public Func<Guid, Task>? CancelCueCallback { get; set; }

    // ----- UI rewrite P4: Now Playing row seek (with lock) --------------------------------------

    /// <summary>Unlocks dragging/tapping the Now Playing progress bars to seek. Default locked -
    /// the panel sits next to GO, so accidental seeks during a show must be opt-in (plan §3.2).</summary>
    [ObservableProperty]
    private bool _nowPlayingSeekUnlocked;

    /// <summary>Seeks an active cue to a 0..1 fraction of its duration. No-op while locked or when
    /// the cue has no known duration yet.</summary>
    public Task SeekActiveCueToFractionAsync(ActiveCueViewModel cue, double fraction)
    {
        if (!NowPlayingSeekUnlocked || cue.DurationMs <= 0 || SeekCueCallback is null)
            return Task.CompletedTask;
        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        return SeekCueCallback(cue.CueId, TimeSpan.FromMilliseconds(cue.DurationMs * clamped));
    }

    /// <summary>Host callback for mutating a placement's already-running compositor slot while the
    /// selected cue is active. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveCueVideoPlacementCallback { get; set; }

    /// <summary>Host callback raised when an <em>idle</em> (not-yet-fired) cue's clip model is edited in a way
    /// the backing show document captures only at (re)load - e.g. a video placement nudge. The host flags its
    /// document stale so the next GO rebuilds it with the current model, instead of firing stale geometry. A
    /// running cue is updated live via <see cref="UpdateActiveCueVideoPlacementCallback"/> and does not raise this.</summary>
    public Action? CueClipModelStaleCallback { get; set; }

    /// <summary>Host callback for reconciling the selected cue's running audio routes after route
    /// row edits. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, IReadOnlyList<CueAudioRoute>, Task>? UpdateActiveCueAudioRoutesCallback { get; set; }

    /// <summary>Host callback to live-re-render a playing text cue after a text/style edit (so it updates in
    /// place instead of only on the next fire). No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, MediaCueNode, Task>? UpdateActiveCueTextCallback { get; set; }

    /// <summary>Host callback - live-applies an output mapping (warp sections) to a running
    /// composition: (compositionId, outputLineId, mapping). No-op when the composition isn't live.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool>? UpdateOutputMappingCallback { get; set; }

    /// <summary>Host callback - live-applies a composition-level video FX mapping to a running composition.</summary>
    public Func<Guid, CueOutputMapping?, bool>? UpdateCompositionVideoFxCallback { get; set; }

    /// <summary>Host callback - shows/hides the mapping calibration grid for one composition output.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool, bool>? SetCompositionTestPatternCallback { get; set; }

    /// <summary>Engine callback - cue began playing. Marks its row Current and pushes a new
    /// <see cref="ActiveCueViewModel"/> into <see cref="ActiveCues"/>.</summary>
    public void OnCueStarted(Guid cueId)
    {
        _activeCueIds.Add(cueId);
        RefreshRowStatuses();

        var node = FindNodeById(cueId);
        if (node is not null && !ActiveCues.Any(a => a.CueId == cueId))
        {
            var entry = new ActiveCueViewModel(node, cueId, id => _ = CancelActiveCueAsync(id))
            {
                DurationMs = Math.Max(0, node.EffectiveDurationMs),
            };
            ActiveCues.Add(entry);
            AddNowPlayingRow(entry);
        }
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - preview stopped. Clears preview state on the VM.</summary>
    public void OnPreviewEnded(Guid cueId)
    {
        _ = cueId;
        if (PreviewingCueId is null) return;
        PreviewingCueId = null;
        StatusMessage = Strings.PreviewStoppedStatus;
    }

    /// <summary>Engine callback - cue stopped (natural end, Stop, or Panic). Clears Current
    /// status and removes the matching <see cref="ActiveCueViewModel"/>.</summary>
    public void OnCueEnded(Guid cueId)
    {
        _activeCueIds.Remove(cueId);
        RefreshRowStatuses();

        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (ActiveCues[i].CueId == cueId)
                ActiveCues.RemoveAt(i);
        RemoveNowPlayingRow(cueId);
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - progress sample for one active cue. Updates the row's
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

        if (SelectedCueNode?.Id == p.CueId && p.Duration > TimeSpan.Zero)
            CueScrubberValue = p.Position.TotalMilliseconds * 1000.0 / p.Duration.TotalMilliseconds;
    }

    private void SyncCueScrubberFromActiveSelection()
    {
        if (SelectedCueNode is null)
            return;
        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;
        var positionMs = active?.PositionMs ?? 0;
        CueScrubberValue = positionMs * 1000.0 / durationMs;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreview))]
    private async Task TogglePreviewAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } node)
            return;

        if (IsPreviewingSelectedCue)
        {
            if (StopPreviewCallback is not null)
                await StopPreviewCallback();
            PreviewingCueId = null;
            StatusMessage = Strings.PreviewStoppedStatus;
            return;
        }

        if (PreviewCueCallback is null)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        if (node.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueInvalidMediaCue;
            return;
        }

        using var cts = new CancellationTokenSource();
        var err = await PreviewCueCallback(media, cts.Token);
        if (!string.IsNullOrWhiteSpace(err))
        {
            StatusMessage = err;
            return;
        }

        PreviewingCueId = node.Id;
        StatusMessage = Strings.Format(nameof(Strings.PreviewingCueStatusFormat), CueDisplay(node));
    }

    private bool CanTogglePreview() =>
        SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanSeekActiveCueFromScrubber))]
    private async Task SeekActiveCueFromScrubberAsync()
    {
        if (SelectedCueNode is null || SeekCueCallback is null)
            return;

        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;

        var position = TimeSpan.FromMilliseconds(CueScrubberValue * durationMs / 1000.0);
        await SeekCueCallback(SelectedCueNode.Id, position);
    }

    private bool CanSeekActiveCueFromScrubber() => IsCueScrubberVisible;

    private CueNodeViewModel? FindNodeById(Guid id)
    {
        foreach (var node in EnumerateAllCueNodes())
            if (node.Id == id)
                return node;
        return null;
    }

    /// <summary>Host callback - the set of warmed (standby-ready) cues changed. Snapshot lists the cue ids
    /// that are currently warmed. Walks every loaded cue node and sets <c>IsPreRollWarm</c>
    /// accordingly so the status badge column can render the warming indicator (Phase 5.7.2).
    /// <para>This method does not marshal threads on its own; the host wiring (MainViewModel)
    /// hops onto the UI dispatcher before invoking, because the underlying
    /// <c>ShowSession.PreparedCuesChanged</c> can fire from any thread.</para>
    /// </summary>
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

    /// <summary>Host callback - richer per-cue standby preparation states changed (Idle/Preparing/
    /// Ready/Failed). Cues absent from the snapshot are Idle. Drives the status badge + tooltip and,
    /// via <see cref="CueNodeViewModel.PreRollState"/>, keeps <c>IsPreRollWarm</c> in sync.</summary>
    public void OnPreparedCueStatesChanged(IReadOnlyList<Playback.CuePreparationStatus> states)
    {
        var byId = states.ToDictionary(s => s.CueId);
        foreach (var node in EnumerateAllCueNodes())
        {
            if (byId.TryGetValue(node.Id, out var status))
            {
                node.PreRollState = status.State;
                node.PreRollError = status.Error;
            }
            else
            {
                node.PreRollState = PreparedCueState.Idle;
                node.PreRollError = null;
            }
        }
    }

    private void RebuildUpcomingCues()
    {
        UpcomingCues.Clear();
        if (SelectedCueList is null) return;
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            foreach (var c in simultaneousGroup)
            {
                if (_activeCueIds.Contains(c.Id)) continue;
                UpcomingCues.Add(c);
            }
            return;
        }

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0) return;

        var anchor = StandbyCueNode ?? ordered.FirstOrDefault();
        if (anchor is null) return;
        var startIdx = ordered.FindIndex(c => ReferenceEquals(c, ResolveFireableCue(anchor) ?? anchor));
        if (startIdx < 0) return;

        for (var i = startIdx; i < ordered.Count; i++)
        {
            var c = ordered[i];
            // Don't list already-active cues as upcoming - they're in the Active section.
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
        OnPropertyChanged(nameof(TransportStateColor));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        _statusMessageClearCts?.Cancel();
        _statusMessageClearCts?.Dispose();
        _statusMessageClearCts = null;

        if (string.IsNullOrWhiteSpace(value))
            return;

        // Status surfaces as a top-right toast (MainView overlay) instead of the old inline banner,
        // which pushed the whole cue list down mid-click. Severity is a keyword heuristic - cue
        // status strings carry no structured level.
        ToastCenter.Post(ClassifyStatusSeverity(value), value);

        var cts = new CancellationTokenSource();
        _statusMessageClearCts = cts;
        _ = ClearStatusMessageLaterAsync(value, cts.Token);
    }

    private static ToastSeverity ClassifyStatusSeverity(string message) =>
        message.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || message.Contains("error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drift", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drop", StringComparison.OrdinalIgnoreCase)
            ? ToastSeverity.Warning
            : ToastSeverity.Info;

    private async Task ClearStatusMessageLaterAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusMessageAutoClearDelay, token).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && string.Equals(StatusMessage, message, StringComparison.Ordinal))
                    StatusMessage = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
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

    private bool IsInCurrentCueTree(CueNodeViewModel node) =>
        SelectedCueList is not null && ContainsNode(SelectedCueList.Nodes, node);

    private void PruneSelectionToCurrentTree()
    {
        var removed = _selectedCueNodes.RemoveAll(n => !IsInCurrentCueTree(n));
        if (removed == 0 && (SelectedCueNode is null || IsInCurrentCueTree(SelectedCueNode)))
            return;

        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    private void ReconcileTransportAfterTreeMutation(int removedFireableIndex)
    {
        var ordered = EnumerateFireableCueOrder().ToList();

        if (CurrentCueNode is not null && !IsInCurrentCueTree(CurrentCueNode))
        {
            CurrentCueNode = null;
            IsTransportPaused = false;
        }

        if (StandbyCueNode is not null && !IsInCurrentCueTree(StandbyCueNode))
        {
            StandbyCueNode = ordered.Count == 0
                ? null
                : ordered[Math.Clamp(removedFireableIndex < 0 ? 0 : removedFireableIndex, 0, ordered.Count - 1)];
        }
        else
        {
            RefreshRowStatuses();
            RebuildUpcomingCues();
        }

        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
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

    /// <summary>Next auto-number: global max numeric + 1 (#27 uniqueness - the old siblings.Count+1
    /// collided across nesting levels, e.g. root "2" vs a group's second child "2").</summary>
    private string NextNumber(ICollection<CueNodeViewModel> siblings)
    {
        _ = siblings; // kept for call-site compatibility
        var max = 0;
        foreach (var c in EnumerateAllCueNodes())
            if (int.TryParse(c.Number, out var n) && n > max)
                max = n;
        return (max + 1).ToString();
    }

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

    /// <summary>
    /// Resolves the trigger policy for a sequential transition. The flat fireable order omits Group rows,
    /// but crossing from outside into a Group must honor that Group's trigger mode; once inside the same
    /// Group, the destination cue's own mode applies. This makes a Manual Group a real boundary gate even
    /// when its first child is Auto-Follow/Auto-Continue.
    /// </summary>
    internal bool SequentialTransitionUsesMode(
        CueNodeViewModel from, CueNodeViewModel to, CueTriggerMode requiredMode)
    {
        var fromPath = FindContainingGroupPath(from);
        var toPath = FindContainingGroupPath(to);
        var shared = 0;
        while (shared < fromPath.Count
               && shared < toPath.Count
               && ReferenceEquals(fromPath[shared], toPath[shared]))
            shared++;

        // Entering nested groups requires every newly-crossed boundary to opt into the same automatic
        // transition. A Manual inner group must not be bypassed merely because its outer group is automatic.
        return shared < toPath.Count
            ? toPath.Skip(shared).All(group => group.TriggerMode == requiredMode)
            : to.TriggerMode == requiredMode;
    }

    private IReadOnlyList<CueNodeViewModel> FindContainingGroupPath(CueNodeViewModel target)
    {
        if (SelectedCueList is null)
            return [];
        var path = new List<CueNodeViewModel>();
        return Find(SelectedCueList.Nodes) ? path : [];

        bool Find(IEnumerable<CueNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, target))
                    return true;
                if (!node.IsGroup)
                    continue;
                path.Add(node);
                if (Find(node.Children))
                    return true;
                path.RemoveAt(path.Count - 1);
            }

            return false;
        }
    }

    private static string CueDisplay(CueNodeViewModel cue) =>
        string.IsNullOrWhiteSpace(cue.Number)
            ? cue.Label
            : $"{cue.Number} {cue.Label}".Trim();

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

        var previous = anchor;
        for (var i = idx + 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (!SequentialTransitionUsesMode(previous, next, CueTriggerMode.AutoContinue))
                break;
            if (plan.Any(p => ReferenceEquals(p.Cue, next)))
            {
                previous = next;
                continue;
            }
            plan.Add((next, Math.Max(0, next.PreWaitMs)));
            previous = next;
        }
    }

    /// <summary>Called when the active player finishes a file naturally during cue-driven playback.</summary>
    public Task OnMediaCueNaturallyEndedAsync() =>
        CurrentCueNode is { Kind: CueNodeKind.Media } current
            ? OnMediaCueNaturallyEndedAsync(current.Id)
            : Task.CompletedTask;

    public async Task OnMediaCueNaturallyEndedAsync(Guid endedCueId)
    {
        if (EnumerateAllCueNodes().FirstOrDefault(cue => cue.Id == endedCueId)
            is not { Kind: CueNodeKind.Media } ended)
            return;

        // End target ("then fire cue #"): an explicit on-end jump wins over the default
        // next-cue-Auto-Follow chain - "after this song, go anywhere".
        if (ended.EndTargetCueId is { } targetId)
        {
            var endTarget = EnumerateAllCueNodes().FirstOrDefault(c => c.Id == targetId);
            if (endTarget is null || ReferenceEquals(endTarget, ended) || endTarget.Kind == CueNodeKind.Comment)
            {
                // An authored end target is an override, not a best-effort hint. If its stable link became
                // invalid after deletion/import, stop here and surface it instead of unexpectedly firing the
                // ordinary next Auto-Follow cue.
                StatusMessage = Strings.CueEndTargetUnavailable;
                return;
            }
            StandbyCueNode = endTarget;
            StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplay(endTarget));
            _immediateJumpChain.Clear();
            await GoCore();
            return;
        }

        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, ended));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;

        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
            return;

        StandbyCueNode = next;
        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplay(next));
        _immediateJumpChain.Clear();
        await GoCore();
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
            var kind = Enum.TryParse<CueActionKind>(node.Extra, out var k) ? k : CueActionKind.OSCOut;
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

    public IReadOnlyList<(Guid CueId, PortAudioInputPlaylistItem Item)> GetPortAudioPreConnectTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<(Guid, PortAudioInputPlaylistItem)>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not PortAudioInputPlaylistItem pa
                    || !pa.SupportsPreRoll())
                    continue;
                groupTargets.Add((cue.Id, pa));
            }

            return groupTargets;
        }

        var targets = new List<(Guid, PortAudioInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
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
            FinalizeAddedCue(row);
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

        // Group steps that share the same delay for coordinated start.
        var groups = plan.GroupBy(s => s.DelayMs).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            await WaitUntilDelayAsync(startedAt, group.Key, ct);
            ct.ThrowIfCancellationRequested();

            var steps = group.ToList();
            // Only the playing pointer follows fired steps. The editor selection is operator-owned and
            // transport never changes it; current/standby row dots show the live playhead instead.
            foreach (var step in steps)
                CurrentCueNode = step.Cue;

            if (steps.Count > 1 && MediaCueGroupExecutor is not null)
            {
                // Coordinated group: open all decoders in parallel, start in sync.
                DispatchCueGroupExecution(steps.Select(s => s.Cue).ToList(), ct);
            }
            else
            {
                foreach (var step in steps)
                    DispatchCueExecution(step.Cue, ct);
            }
        }
    }

    private void DispatchCueExecution(CueNodeViewModel cue, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                await ApplyCueExecutionResultOnUiAsync(cue, exec, MediaExecutionConfigured).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
            }
        }, ct);
    }

    private void DispatchCueGroupExecution(IReadOnlyList<CueNodeViewModel> cues, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var mediaCues = cues
                    .Where(c => c.Kind == CueNodeKind.Media)
                    .Select(c => c.ToModel())
                    .OfType<MediaCueNode>()
                    .ToList();

                if (mediaCues.Count > 0 && MediaCueGroupExecutor is not null)
                {
                    var result = await MediaCueGroupExecutor(mediaCues, ct).ConfigureAwait(false);
                    await SetStatusMessageOnUiAsync(string.IsNullOrWhiteSpace(result)
                        ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), $"{mediaCues.Count} cues")
                        : result);
                }

                // Non-media cues in the group still dispatch individually.
                foreach (var cue in cues.Where(c => c.Kind != CueNodeKind.Media))
                {
                    try
                    {
                        var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(exec))
                            await ApplyCueExecutionResultOnUiAsync(cue, exec, mediaExecutionConfigured: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await SetStatusMessageOnUiAsync(ex.Message);
            }
        }, ct);
    }

    private Task ApplyCueExecutionResultOnUiAsync(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured)).GetTask();
    }

    private Task ApplyCueExecutionFailureOnUiAsync(CueNodeViewModel cue, string detail)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionFailure(cue, detail);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionFailure(cue, detail)).GetTask();
    }

    private void ApplyCueExecutionResult(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (cue.Kind == CueNodeKind.Media
            && mediaExecutionConfigured
            && !_activeCueIds.Contains(cue.Id))
        {
            ApplyCueExecutionFailure(cue, detail);
            return;
        }

        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplay(cue))
            : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(cue), detail);

        // Visualizer rows in Now Playing: a fired Start cue appears with a ticking position (count-up
        // when indefinite, progress toward the set duration otherwise); a Stop cue (or the row's X, or
        // the duration elapsing) ends the row. The projectM LAYER itself keeps rendering unless stopped.
        if (cue.Kind == CueNodeKind.Visualizer && string.IsNullOrWhiteSpace(detail))
        {
            if (string.Equals(cue.Extra, "Stop", StringComparison.OrdinalIgnoreCase))
                EndVisualizerRows(VisualizerTargetComposition(cue));
            else
                StartVisualizerRow(cue);
        }

        // Runtime chaining for INSTANT cues (visualizer/action - jumps redirect themselves): the media
        // end-machinery never fires for them, so a following Auto-Follow cue would otherwise never
        // start. An infinite visualizer (or an action) is non-blocking → the chain advances NOW; a
        // visualizer WITH a duration occupies the timeline like an image slide → advance after it.
        if (string.IsNullOrWhiteSpace(detail)
            && cue.Kind is CueNodeKind.Visualizer or CueNodeKind.Action)
        {
            var delayMs = cue.Kind == CueNodeKind.Visualizer ? Math.Max(0, cue.VisualizerDurationMs) : 0;
            _ = AdvanceAutoFollowAfterInstantCueAsync(cue, delayMs);
        }
    }

    /// <summary>Row-X cancel: a running VISUALIZER row stops the projectM layer (the generic session
    /// stop is a no-op for it - visualizers are not session clips); everything else goes to the host's
    /// cancel callback as before.</summary>
    private async Task CancelActiveCueAsync(Guid cueId)
    {
        if (_runningVisualizers.ContainsKey(cueId))
        {
            await StopVisualizerAsync(cueId);
            return;
        }

        await (CancelCueCallback?.Invoke(cueId) ?? Task.CompletedTask);
    }

    /// <summary>Stops one running visualizer: detaches its layer (synthetic Stop through the executor)
    /// and retires its Now-Playing row.</summary>
    private async Task StopVisualizerAsync(Guid vizCueId)
    {
        if (!_runningVisualizers.TryGetValue(vizCueId, out var info))
            return;
        if (VisualizerCueExecutor is not null)
        {
            try
            {
                await VisualizerCueExecutor(
                    new VisualizerCueNode { CompositionId = info.Composition, StartVisualizer = false },
                    CancellationToken.None);
            }
            catch
            {
                // best effort - the row still retires below
            }
        }

        EndVisualizerRows(info.Composition);
    }

    /// <summary>Panic/Stop: every running visualizer layer detaches and its row retires.</summary>
    public void StopAllVisualizers()
    {
        foreach (var id in _runningVisualizers.Keys.ToList())
            _ = StopVisualizerAsync(id);
    }

    /// <summary>A host rebuild removed one composition visualizer without executing its Stop cue.</summary>
    internal void OnVisualizerLayerCleared(Guid compositionId) => EndVisualizerRows(compositionId);

    /// <summary>The host removed every composition visualizer without executing individual Stop cues.</summary>
    internal void OnVisualizerLayersCleared()
    {
        foreach (var compositionId in _runningVisualizers.Values.Select(info => info.Composition).Distinct().ToList())
            EndVisualizerRows(compositionId);
    }

    // --- Visualizer Now-Playing rows (#29 first slice) --------------------------------------------
    // _runningVisualizers tracks the actual persistent layer lifetime. A finite cue duration only retires
    // its Now-Playing timeline row; it does not stop projectM, so keep that distinction explicit or Panic and
    // later live placement edits would lose the still-running layer after the row timed out.
    private readonly Dictionary<Guid, (Guid Composition, long StartedTicks, int DurationMs)> _runningVisualizers = new();
    private readonly HashSet<Guid> _visibleVisualizerRows = new();
    private Avalonia.Threading.DispatcherTimer? _visualizerRowTimer;

    private static Guid VisualizerTargetComposition(CueNodeViewModel viz) =>
        viz.VideoPlacements.FirstOrDefault()?.CompositionId ?? viz.VisualizerCompositionId;

    internal void StartVisualizerRow(CueNodeViewModel viz)
    {
        // Re-firing replaces the layer on that composition - end any prior rows for it first.
        EndVisualizerRows(VisualizerTargetComposition(viz));
        _runningVisualizers[viz.Id] =
            (VisualizerTargetComposition(viz), System.Diagnostics.Stopwatch.GetTimestamp(), Math.Max(0, viz.VisualizerDurationMs));
        _visibleVisualizerRows.Add(viz.Id);
        OnCueStarted(viz.Id);
        _visualizerRowTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, OnVisualizerRowTick);
        _visualizerRowTimer.Start();
    }

    private void EndVisualizerRows(Guid compositionId)
    {
        foreach (var (id, info) in _runningVisualizers.Where(kv => kv.Value.Composition == compositionId).ToList())
        {
            _runningVisualizers.Remove(id);
            if (_visibleVisualizerRows.Remove(id))
                OnCueEnded(id);
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    private void OnVisualizerRowTick(object? sender, EventArgs e)
    {
        foreach (var id in _visibleVisualizerRows.ToList())
        {
            if (!_runningVisualizers.TryGetValue(id, out var info))
            {
                _visibleVisualizerRows.Remove(id);
                continue;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(info.StartedTicks);
            if (info.DurationMs > 0 && elapsed.TotalMilliseconds >= info.DurationMs)
            {
                // Timeline slot done (the layer keeps rendering; only the row retires).
                _visibleVisualizerRows.Remove(id);
                OnCueEnded(id);
                continue;
            }

            OnCueProgress(new CuePlaybackProgress(
                id, elapsed, TimeSpan.FromMilliseconds(info.DurationMs)));
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    /// <summary>Fires the next Auto-Follow cue after an instant cue (optionally after its timeline
    /// duration). Cancels itself when the operator moved on (another GO changed the standby).</summary>
    private async Task AdvanceAutoFollowAfterInstantCueAsync(CueNodeViewModel fired, int delayMs)
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, fired));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;
        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(fired, next, CueTriggerMode.AutoFollow))
            return;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
            // Stale? The operator fired something else while the visualizer slide ran.
            if (!ReferenceEquals(CurrentCueNode, fired) && CurrentCueNode is not null)
                return;
        }

        StandbyCueNode = next;
        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplay(next));
        _immediateJumpChain.Clear();
        await GoCore();
    }

    private void ApplyCueExecutionFailure(CueNodeViewModel cue, string? detail)
    {
        if (ReferenceEquals(CurrentCueNode, cue))
            CurrentCueNode = null;
        StandbyCueNode = cue;
        IsTransportPaused = false;
        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueExecutionFailedStatusFormat), CueDisplay(cue))
            : Strings.Format(nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplay(cue), detail);
    }

    private async Task SetStatusMessageOnUiAsync(string? message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = message;
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = message);
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
            case CueNodeKind.Visualizer:
                if (VisualizerCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is VisualizerCueNode viz
                    ? await VisualizerCueExecutor(viz, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Jump:
                // Control flow is transport/UI state - marshal to the UI thread (this executor runs on a
                // worker). The jump moves the playhead to a target (loop back / section repeat / random
                // pick) and, by default, fires it through the normal GO machinery.
                return await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => ExecuteJumpCueOnUi(cue));
            case CueNodeKind.Group:
            default:
                return null;
        }
    }

    /// <summary>Executes a Jump cue (UI thread): resolves a live target by stable cue ID (numbers are
    /// display-only, so renumber/reorder never retargets), picks randomly when configured, then either
    /// fires the target (default - the loop actually loops) or arms it as standby for the next GO.</summary>
    internal string? ExecuteJumpCueOnUi(CueNodeViewModel jump)
    {
        if (jump.JumpTargetIds.Count == 0)
            return Strings.CueJumpNoTargets;

        var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
        var live = jump.JumpTargetIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Where(c => c.Kind != CueNodeKind.Comment)
            .DistinctBy(c => c.Id)
            .ToList();
        if (live.Count == 0)
            return Strings.CueJumpTargetMissing;

        if (!_immediateJumpChain.Add(jump.Id))
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        // A group target resolves through its fire mode to a concrete first cue. Exclude any candidate whose
        // next immediate jump was already visited: e.g. Jump #5 → containing Group → first child Jump #5.
        // Valid media/action targets in the same pool remain usable, so one bad group link cannot spin the app.
        var safe = live.Where(candidate =>
        {
            var resolved = ResolveFireableCue(candidate);
            return resolved is null
                   || resolved.Kind != CueNodeKind.Jump
                   || !_immediateJumpChain.Contains(resolved.Id);
        }).ToList();
        if (safe.Count == 0)
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        var candidates = safe;
        if (jump.JumpRandom
            && jump.JumpAvoidImmediateRepeat
            && safe.Count > 1
            && _lastRandomJumpTargetIds.TryGetValue(jump.Id, out var previousTargetId))
        {
            var nonRepeating = safe.Where(candidate => candidate.Id != previousTargetId).ToList();
            if (nonRepeating.Count > 0)
                candidates = nonRepeating;
        }

        var target = jump.JumpRandom && candidates.Count > 1
            ? candidates[Random.Shared.Next(candidates.Count)]
            : candidates[0];
        if (jump.JumpRandom && jump.JumpAvoidImmediateRepeat)
            _lastRandomJumpTargetIds[jump.Id] = target.Id;
        else
            _lastRandomJumpTargetIds.Remove(jump.Id);
        var resolvedTarget = ResolveFireableCue(target);

        StandbyCueNode = target;
        if (!string.Equals(jump.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedTarget?.Kind != CueNodeKind.Jump)
                _immediateJumpChain.Clear();
            _ = GoCore(); // preserve the visited set only across an immediate Jump→Jump continuation
        }
        else
        {
            _immediateJumpChain.Clear();
        }
        return null;
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
        if (SelectedActionCue is { } actionCue && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        RefreshBrokenEndpointFlags();
    }

    private string? _cueListsCollectionPath;

    public string? CueListsCollectionPath => _cueListsCollectionPath;

    public string? DisplayedCueFilePath => _cueListsCollectionPath ?? SelectedCueList?.Path;

    public List<CueList> BuildCueListsSnapshot() => CueLists.Select(c => c.ToModel()).ToList();

    public void ApplyCueLists(IReadOnlyList<CueList> lists, string? collectionPath = null)
    {
        _lastRandomJumpTargetIds.Clear();
        _cueListsCollectionPath = collectionPath;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
        CueLists.Clear();
        foreach (var list in lists)
            CueLists.Add(CueListEditorViewModel.FromModel(list, resolveLine: ResolveOutputLine));
        if (CueLists.Count == 0)
            CueLists.Add(new CueListEditorViewModel(Strings.DefaultCueListName));
        SelectedCueList = CueLists[0];
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RefreshCueTargetDisplays();
    }

    private void ClearCueListsCollectionPath()
    {
        if (_cueListsCollectionPath is null)
            return;

        _cueListsCollectionPath = null;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
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
