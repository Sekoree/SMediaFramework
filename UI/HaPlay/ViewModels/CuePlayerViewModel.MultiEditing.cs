using System.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// Selection-aware cue editing. The drawer keeps a primary cue for common fields, while each
/// capability-specific editor uses the first compatible selected cue as its representative.
/// Changes made to that representative are copied to every compatible cue in the selection.
/// </summary>
public partial class CuePlayerViewModel
{
    private readonly List<CueNodeViewModel> _multiEditWatchedNodes = [];
    private CueAudioRouteViewModel? _multiEditWatchedAudioRoute;
    private CueVideoPlacementViewModel? _multiEditWatchedVideoPlacement;
    private readonly List<CueSubtitleTrackChoice> _multiEditWatchedSubtitleTracks = [];
    private bool _isApplyingMultiEdit;

    public CueNodeViewModel? SelectedMediaCue =>
        EffectiveSelection().FirstOrDefault(static cue => cue.Kind == CueNodeKind.Media);

    public CueNodeViewModel? SelectedStaticCue =>
        EffectiveSelection().FirstOrDefault(static cue =>
            cue.Kind == CueNodeKind.Media && (cue.IsImageCue || cue.IsTextCue));

    public CueNodeViewModel? SelectedAudioCue =>
        EffectiveSelection().FirstOrDefault(IsAudioEditableCue);

    public CueNodeViewModel? SelectedVideoCue =>
        EffectiveSelection().FirstOrDefault(IsVideoEditableCue);

    public CueNodeViewModel? SelectedTextCue =>
        EffectiveSelection().FirstOrDefault(static cue => cue.Kind == CueNodeKind.Media && cue.IsTextCue);

    public CueNodeViewModel? SelectedSubtitleCue =>
        EffectiveSelection().FirstOrDefault(static cue =>
            cue.Kind == CueNodeKind.Media && cue.HasSubtitleTracks);

    public CueNodeViewModel? SelectedActionCue => FindSelectedKind(CueNodeKind.Action);
    public CueNodeViewModel? SelectedCommentCue => FindSelectedKind(CueNodeKind.Comment);
    public CueNodeViewModel? SelectedGroupCue => FindSelectedKind(CueNodeKind.Group);
    public CueNodeViewModel? SelectedJumpCue => FindSelectedKind(CueNodeKind.Jump);
    public CueNodeViewModel? SelectedVisualizerCue => FindSelectedKind(CueNodeKind.Visualizer);

    public bool HasSelectedSubtitleCue => SelectedSubtitleCue is not null;

    private CueNodeViewModel? FindSelectedKind(CueNodeKind kind) =>
        EffectiveSelection().FirstOrDefault(cue => cue.Kind == kind);

    private IReadOnlyList<CueNodeViewModel> EffectiveSelection()
    {
        if (SelectedCueNode is not null
            && _selectedCueNodes.Count > 0
            && _selectedCueNodes.Contains(SelectedCueNode))
            return _selectedCueNodes;

        return SelectedCueNode is null ? [] : [SelectedCueNode];
    }

    private List<CueNodeViewModel> SelectedMediaTargets() =>
        EffectiveSelection().Where(static cue => cue.Kind == CueNodeKind.Media).ToList();

    private List<CueNodeViewModel> SelectedStaticTargets() =>
        EffectiveSelection().Where(static cue =>
            cue.Kind == CueNodeKind.Media && (cue.IsImageCue || cue.IsTextCue)).ToList();

    private List<CueNodeViewModel> SelectedAudioTargets() =>
        EffectiveSelection().Where(IsAudioEditableCue).ToList();

    private List<CueNodeViewModel> SelectedVideoTargets() =>
        EffectiveSelection().Where(IsVideoEditableCue).ToList();

    private List<CueNodeViewModel> SelectedTextTargets() =>
        EffectiveSelection().Where(static cue => cue.Kind == CueNodeKind.Media && cue.IsTextCue).ToList();

    private List<CueNodeViewModel> SelectedSubtitleTargets() =>
        EffectiveSelection().Where(static cue =>
            cue.Kind == CueNodeKind.Media && cue.HasSubtitleTracks).ToList();

    private List<CueNodeViewModel> SelectedKindTargets(CueNodeKind kind) =>
        EffectiveSelection().Where(cue => cue.Kind == kind).ToList();

    private static bool IsAudioEditableCue(CueNodeViewModel cue) =>
        cue.Kind == CueNodeKind.Media
        && (cue.SourceHasAudio
            || cue.SourceAudioChannels > 0
            || cue.AudioRoutes.Count > 0
            || !HasKnownSourceCapabilities(cue));

    private static bool IsVideoEditableCue(CueNodeViewModel cue) =>
        cue.Kind == CueNodeKind.Visualizer
        || cue.Kind == CueNodeKind.Media
        && (cue.SourceHasVideo || cue.VideoPlacements.Count > 0 || !HasKnownSourceCapabilities(cue));

    private static bool HasKnownSourceCapabilities(CueNodeViewModel cue) =>
        cue.SourceCapabilitiesKnown
        || cue.SourceHasAudio
        || cue.SourceHasVideo
        || cue.SourceAudioChannels > 0;

    private void ResubscribeMultiEditSelection()
    {
        foreach (var cue in _multiEditWatchedNodes)
            cue.PropertyChanged -= OnMultiEditCuePropertyChanged;
        _multiEditWatchedNodes.Clear();

        foreach (var cue in EffectiveSelection())
        {
            cue.PropertyChanged += OnMultiEditCuePropertyChanged;
            _multiEditWatchedNodes.Add(cue);
        }
    }

    /// <summary>Refreshes all capability representatives after the tree selection or an async media
    /// probe changes. This is intentionally one method so tabs, lists, and command gates cannot drift.</summary>
    private void RefreshMultiEditSelectionState(bool resetSelectedItems = true)
    {
        OnPropertyChanged(nameof(SelectedMediaCue));
        OnPropertyChanged(nameof(SelectedStaticCue));
        OnPropertyChanged(nameof(SelectedAudioCue));
        OnPropertyChanged(nameof(SelectedVideoCue));
        OnPropertyChanged(nameof(SelectedTextCue));
        OnPropertyChanged(nameof(SelectedSubtitleCue));
        OnPropertyChanged(nameof(SelectedActionCue));
        OnPropertyChanged(nameof(SelectedCommentCue));
        OnPropertyChanged(nameof(SelectedGroupCue));
        OnPropertyChanged(nameof(SelectedJumpCue));
        OnPropertyChanged(nameof(SelectedVisualizerCue));
        OnPropertyChanged(nameof(HasSelectedSubtitleCue));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedTextCue));
        OnPropertyChanged(nameof(HasSelectedStaticCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(IsJumpCueSelected));
        OnPropertyChanged(nameof(IsVisualizerCueSelected));
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));

        if (resetSelectedItems)
        {
            SelectedAudioRoute = SelectedAudioCue?.AudioRoutes.FirstOrDefault();
            SelectedVideoPlacement = SelectedVideoCue?.VideoPlacements.FirstOrDefault();
        }

        foreach (var track in _multiEditWatchedSubtitleTracks)
            track.PropertyChanged -= OnMultiEditSubtitleTrackPropertyChanged;
        _multiEditWatchedSubtitleTracks.Clear();
        if (SelectedSubtitleCue is { } subtitleCue)
        {
            foreach (var track in subtitleCue.SubtitleTrackChoices)
            {
                track.PropertyChanged += OnMultiEditSubtitleTrackPropertyChanged;
                _multiEditWatchedSubtitleTracks.Add(track);
            }
        }

        if (SelectedActionCue is { } actionCue
            && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == endpointId);
        else
            SelectedActionEndpoint = null;

        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        ApplyCueDownmixPresetCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
    }

    private void OnMultiEditCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingMultiEdit || EffectiveSelection().Count <= 1 || sender is not CueNodeViewModel source)
            return;

        var propertyName = e.PropertyName;
        if (string.IsNullOrEmpty(propertyName))
            return;

        if (propertyName is nameof(CueNodeViewModel.SourceCapabilitiesKnown)
            or nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture)
            or nameof(CueNodeViewModel.HasSubtitleTracks))
        {
            RefreshMultiEditSelectionState();
            return;
        }

        List<CueNodeViewModel>? targets = null;
        if (ReferenceEquals(source, SelectedCueNode)
            && propertyName is nameof(CueNodeViewModel.Label)
                or nameof(CueNodeViewModel.TriggerMode)
                or nameof(CueNodeViewModel.PreWaitMs)
                or nameof(CueNodeViewModel.Notes))
        {
            targets = EffectiveSelection().ToList();
        }
        else if (ReferenceEquals(source, SelectedMediaCue)
                 && propertyName is nameof(CueNodeViewModel.FadeInMs)
                     or nameof(CueNodeViewModel.FadeOutMs)
                     or nameof(CueNodeViewModel.StartOffsetMs)
                     or nameof(CueNodeViewModel.EndOffsetMs)
                     or nameof(CueNodeViewModel.Loop)
                     or nameof(CueNodeViewModel.EndBehavior)
                     or nameof(CueNodeViewModel.EndTargetCueId))
        {
            targets = SelectedMediaTargets();
        }
        else if (ReferenceEquals(source, SelectedStaticCue)
                 && propertyName == nameof(CueNodeViewModel.DurationMs))
        {
            targets = SelectedStaticTargets();
        }
        else if (ReferenceEquals(source, SelectedAudioCue)
                 && propertyName is nameof(CueNodeViewModel.SendToVisualizer)
                     or nameof(CueNodeViewModel.AudioTrackIndex)
                     or nameof(CueNodeViewModel.AudioTrackSignature))
        {
            targets = SelectedAudioTargets();
        }
        else if (ReferenceEquals(source, SelectedVideoCue)
                 && propertyName is nameof(CueNodeViewModel.VideoTrackIndex)
                     or nameof(CueNodeViewModel.VideoTrackSignature))
        {
            targets = SelectedVideoTargets();
        }
        else if (ReferenceEquals(source, SelectedSubtitleCue)
                 && propertyName is nameof(CueNodeViewModel.SubtitleFontFamily)
                     or nameof(CueNodeViewModel.SubtitleFontScale)
                     or nameof(CueNodeViewModel.SelectedSubtitleAlignment))
        {
            targets = SelectedSubtitleTargets();
        }
        else if (ReferenceEquals(source, SelectedTextCue)
                 && propertyName == nameof(CueNodeViewModel.MediaSourceItem)
                 && source.IsTextCue)
        {
            targets = SelectedTextTargets();
        }
        else if (propertyName is nameof(CueNodeViewModel.SourceOrAction)
                     or nameof(CueNodeViewModel.Extra)
                     or nameof(CueNodeViewModel.EndpointIdText)
                     or nameof(CueNodeViewModel.JumpAvoidImmediateRepeat)
                     or nameof(CueNodeViewModel.VisualizerDurationMs)
                     or nameof(CueNodeViewModel.VisualizerRenderWidth)
                     or nameof(CueNodeViewModel.VisualizerRenderHeight)
                     or nameof(CueNodeViewModel.VisualizerRenderFps)
                     or nameof(CueNodeViewModel.VisualizerPresetDurationSeconds)
                     or nameof(CueNodeViewModel.VisualizerShufflePresets)
                     or nameof(CueNodeViewModel.VisualizerBeatSensitivity)
                     or nameof(CueNodeViewModel.VisualizerTransitionSeconds)
                     or nameof(CueNodeViewModel.VisualizerFeedAll)
                     or nameof(CueNodeViewModel.VisualizerCompositionId))
        {
            var representative = FindSelectedKind(source.Kind);
            if (ReferenceEquals(source, representative))
                targets = SelectedKindTargets(source.Kind);
        }

        if (targets is null)
            return;

        _isApplyingMultiEdit = true;
        try
        {
            foreach (var target in targets)
            {
                if (!ReferenceEquals(target, source))
                    CopyCueProperty(source, target, propertyName);
                NotifyMultiEditedCue(target);
            }
        }
        finally
        {
            _isApplyingMultiEdit = false;
        }

        SuggestPreRollRefresh();
        RefreshCueTargetDisplays();
    }

    private static void CopyCueProperty(CueNodeViewModel source, CueNodeViewModel target, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(CueNodeViewModel.Label): target.Label = source.Label; break;
            case nameof(CueNodeViewModel.TriggerMode): target.TriggerMode = source.TriggerMode; break;
            case nameof(CueNodeViewModel.PreWaitMs): target.PreWaitMs = source.PreWaitMs; break;
            case nameof(CueNodeViewModel.Notes): target.Notes = source.Notes; break;
            case nameof(CueNodeViewModel.FadeInMs): target.FadeInMs = source.FadeInMs; break;
            case nameof(CueNodeViewModel.FadeOutMs): target.FadeOutMs = source.FadeOutMs; break;
            case nameof(CueNodeViewModel.StartOffsetMs): target.StartOffsetMs = source.StartOffsetMs; break;
            case nameof(CueNodeViewModel.EndOffsetMs): target.EndOffsetMs = source.EndOffsetMs; break;
            case nameof(CueNodeViewModel.DurationMs): target.DurationMs = source.DurationMs; break;
            case nameof(CueNodeViewModel.Loop): target.Loop = source.Loop; break;
            case nameof(CueNodeViewModel.EndBehavior): target.EndBehavior = source.EndBehavior; break;
            case nameof(CueNodeViewModel.EndTargetCueId): target.EndTargetCueId = source.EndTargetCueId; break;
            case nameof(CueNodeViewModel.SendToVisualizer): target.SendToVisualizer = source.SendToVisualizer; break;
            case nameof(CueNodeViewModel.AudioTrackIndex): target.AudioTrackIndex = source.AudioTrackIndex; break;
            case nameof(CueNodeViewModel.AudioTrackSignature):
                target.AudioTrackSignature = source.AudioTrackSignature;
                target.SelectedAudioTrackChoice = target.AudioTrackChoices.FirstOrDefault(choice =>
                    choice.Index == source.AudioTrackIndex && choice.Signature == source.AudioTrackSignature)
                    ?? target.AudioTrackChoices.FirstOrDefault(choice =>
                        choice.Signature is not null && choice.Signature == source.AudioTrackSignature)
                    ?? target.AudioTrackChoices.FirstOrDefault();
                break;
            case nameof(CueNodeViewModel.VideoTrackIndex): target.VideoTrackIndex = source.VideoTrackIndex; break;
            case nameof(CueNodeViewModel.VideoTrackSignature):
                target.VideoTrackSignature = source.VideoTrackSignature;
                target.SelectedVideoTrackChoice = target.VideoTrackChoices.FirstOrDefault(choice =>
                    choice.Index == source.VideoTrackIndex && choice.Signature == source.VideoTrackSignature)
                    ?? target.VideoTrackChoices.FirstOrDefault(choice =>
                        choice.Signature is not null && choice.Signature == source.VideoTrackSignature)
                    ?? target.VideoTrackChoices.FirstOrDefault();
                break;
            case nameof(CueNodeViewModel.SubtitleFontFamily): target.SubtitleFontFamily = source.SubtitleFontFamily; break;
            case nameof(CueNodeViewModel.SubtitleFontScale): target.SubtitleFontScale = source.SubtitleFontScale; break;
            case nameof(CueNodeViewModel.SelectedSubtitleAlignment):
                target.SelectedSubtitleAlignment = target.SubtitleAlignmentChoices.FirstOrDefault(
                    choice => choice.Value == source.SelectedSubtitleAlignment?.Value);
                break;
            case nameof(CueNodeViewModel.MediaSourceItem): target.MediaSourceItem = source.MediaSourceItem; break;
            case nameof(CueNodeViewModel.SourceOrAction): target.SourceOrAction = source.SourceOrAction; break;
            case nameof(CueNodeViewModel.Extra): target.Extra = source.Extra; break;
            case nameof(CueNodeViewModel.EndpointIdText): target.EndpointIdText = source.EndpointIdText; break;
            case nameof(CueNodeViewModel.JumpAvoidImmediateRepeat):
                target.JumpAvoidImmediateRepeat = source.JumpAvoidImmediateRepeat;
                break;
            case nameof(CueNodeViewModel.VisualizerDurationMs): target.VisualizerDurationMs = source.VisualizerDurationMs; break;
            case nameof(CueNodeViewModel.VisualizerRenderWidth): target.VisualizerRenderWidth = source.VisualizerRenderWidth; break;
            case nameof(CueNodeViewModel.VisualizerRenderHeight): target.VisualizerRenderHeight = source.VisualizerRenderHeight; break;
            case nameof(CueNodeViewModel.VisualizerRenderFps): target.VisualizerRenderFps = source.VisualizerRenderFps; break;
            case nameof(CueNodeViewModel.VisualizerPresetDurationSeconds):
                target.VisualizerPresetDurationSeconds = source.VisualizerPresetDurationSeconds;
                break;
            case nameof(CueNodeViewModel.VisualizerShufflePresets):
                target.VisualizerShufflePresets = source.VisualizerShufflePresets;
                break;
            case nameof(CueNodeViewModel.VisualizerBeatSensitivity):
                target.VisualizerBeatSensitivity = source.VisualizerBeatSensitivity;
                break;
            case nameof(CueNodeViewModel.VisualizerTransitionSeconds):
                target.VisualizerTransitionSeconds = source.VisualizerTransitionSeconds;
                break;
            case nameof(CueNodeViewModel.VisualizerFeedAll): target.VisualizerFeedAll = source.VisualizerFeedAll; break;
            case nameof(CueNodeViewModel.VisualizerCompositionId):
                target.VisualizerCompositionId = source.VisualizerCompositionId;
                break;
        }
    }

    private void WatchSelectedAudioRouteForMultiEdit(CueAudioRouteViewModel? route)
    {
        if (_multiEditWatchedAudioRoute is not null)
            _multiEditWatchedAudioRoute.PropertyChanged -= OnMultiEditAudioRoutePropertyChanged;
        _multiEditWatchedAudioRoute = route;
        if (_multiEditWatchedAudioRoute is not null)
            _multiEditWatchedAudioRoute.PropertyChanged += OnMultiEditAudioRoutePropertyChanged;
    }

    private void OnMultiEditAudioRoutePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingMultiEdit || EffectiveSelection().Count <= 1
            || sender is not CueAudioRouteViewModel source || SelectedAudioCue is not { } owner)
            return;
        if (e.PropertyName is not (nameof(CueAudioRouteViewModel.SourceChannel)
            or nameof(CueAudioRouteViewModel.OutputLineId)
            or nameof(CueAudioRouteViewModel.OutputChannel)
            or nameof(CueAudioRouteViewModel.GainDb)
            or nameof(CueAudioRouteViewModel.Muted)))
            return;

        var index = owner.AudioRoutes.IndexOf(source);
        if (index < 0)
            return;

        _isApplyingMultiEdit = true;
        try
        {
            foreach (var cue in SelectedAudioTargets())
            {
                if (!ReferenceEquals(cue, owner) && index < cue.AudioRoutes.Count)
                    CopyAudioRouteProperty(source, cue.AudioRoutes[index], e.PropertyName!);
                NotifyMultiEditedAudioCue(cue);
            }
        }
        finally
        {
            _isApplyingMultiEdit = false;
        }
        SuggestPreRollRefresh();
    }

    private static void CopyAudioRouteProperty(
        CueAudioRouteViewModel source, CueAudioRouteViewModel target, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(CueAudioRouteViewModel.SourceChannel): target.SourceChannel = source.SourceChannel; break;
            case nameof(CueAudioRouteViewModel.OutputLineId): target.OutputLineId = source.OutputLineId; break;
            case nameof(CueAudioRouteViewModel.OutputChannel): target.OutputChannel = source.OutputChannel; break;
            case nameof(CueAudioRouteViewModel.GainDb): target.GainDb = source.GainDb; break;
            case nameof(CueAudioRouteViewModel.Muted): target.Muted = source.Muted; break;
        }
    }

    private void OnMultiEditSubtitleTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingMultiEdit || EffectiveSelection().Count <= 1
            || e.PropertyName != nameof(CueSubtitleTrackChoice.IsSelected)
            || sender is not CueSubtitleTrackChoice source)
            return;

        _isApplyingMultiEdit = true;
        try
        {
            foreach (var cue in SelectedSubtitleTargets())
            {
                var target = cue.SubtitleTrackChoices.FirstOrDefault(
                    track => track.StreamIndex == source.StreamIndex);
                if (target is not null && !ReferenceEquals(target, source))
                    target.IsSelected = source.IsSelected;
                NotifyMultiEditedCue(cue);
            }
        }
        finally
        {
            _isApplyingMultiEdit = false;
        }
        SuggestPreRollRefresh();
    }

    private void WatchSelectedVideoPlacementForMultiEdit(CueVideoPlacementViewModel? placement)
    {
        if (_multiEditWatchedVideoPlacement is not null)
            _multiEditWatchedVideoPlacement.PropertyChanged -= OnMultiEditVideoPlacementPropertyChanged;
        _multiEditWatchedVideoPlacement = placement;
        if (_multiEditWatchedVideoPlacement is not null)
            _multiEditWatchedVideoPlacement.PropertyChanged += OnMultiEditVideoPlacementPropertyChanged;
    }

    private void OnMultiEditVideoPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingMultiEdit || EffectiveSelection().Count <= 1
            || sender is not CueVideoPlacementViewModel source || SelectedVideoCue is not { } owner
            || !IsVideoPlacementProperty(e.PropertyName))
            return;

        var index = owner.VideoPlacements.IndexOf(source);
        if (index < 0)
            return;

        _isApplyingMultiEdit = true;
        try
        {
            foreach (var cue in SelectedVideoTargets())
            {
                if (!ReferenceEquals(cue, owner) && index < cue.VideoPlacements.Count)
                    CopyVideoPlacementProperty(source, cue.VideoPlacements[index], e.PropertyName!);
                NotifyMultiEditedVideoCue(cue, index);
            }
        }
        finally
        {
            _isApplyingMultiEdit = false;
        }
        SuggestPreRollRefresh();
    }

    private static void CopyVideoPlacementProperty(
        CueVideoPlacementViewModel source, CueVideoPlacementViewModel target, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(CueVideoPlacementViewModel.CompositionId): target.CompositionId = source.CompositionId; break;
            case nameof(CueVideoPlacementViewModel.LayerIndex): target.LayerIndex = source.LayerIndex; break;
            case nameof(CueVideoPlacementViewModel.Position): target.Position = source.Position; break;
            case nameof(CueVideoPlacementViewModel.Opacity): target.Opacity = source.Opacity; break;
            case nameof(CueVideoPlacementViewModel.DestX): target.DestX = source.DestX; break;
            case nameof(CueVideoPlacementViewModel.DestY): target.DestY = source.DestY; break;
            case nameof(CueVideoPlacementViewModel.DestWidth): target.DestWidth = source.DestWidth; break;
            case nameof(CueVideoPlacementViewModel.DestHeight): target.DestHeight = source.DestHeight; break;
            case nameof(CueVideoPlacementViewModel.CropLeft): target.CropLeft = source.CropLeft; break;
            case nameof(CueVideoPlacementViewModel.CropTop): target.CropTop = source.CropTop; break;
            case nameof(CueVideoPlacementViewModel.CropRight): target.CropRight = source.CropRight; break;
            case nameof(CueVideoPlacementViewModel.CropBottom): target.CropBottom = source.CropBottom; break;
            case nameof(CueVideoPlacementViewModel.RotationDegrees):
                target.RotationDegrees = source.RotationDegrees;
                break;
            case nameof(CueVideoPlacementViewModel.VideoFx): target.VideoFx = source.VideoFx; break;
            case nameof(CueVideoPlacementViewModel.VideoFxEnabled): target.VideoFxEnabled = source.VideoFxEnabled; break;
            case nameof(CueVideoPlacementViewModel.ChromaKeyEnabled): target.ChromaKeyEnabled = source.ChromaKeyEnabled; break;
            case nameof(CueVideoPlacementViewModel.ChromaKeyColorHex): target.ChromaKeyColorHex = source.ChromaKeyColorHex; break;
            case nameof(CueVideoPlacementViewModel.ChromaKeySimilarity): target.ChromaKeySimilarity = source.ChromaKeySimilarity; break;
            case nameof(CueVideoPlacementViewModel.ChromaKeySmoothness): target.ChromaKeySmoothness = source.ChromaKeySmoothness; break;
            case nameof(CueVideoPlacementViewModel.ChromaKeySpill): target.ChromaKeySpill = source.ChromaKeySpill; break;
        }
    }

    private void NotifyMultiEditedCue(CueNodeViewModel cue)
    {
        CueStandbyInvalidated?.Invoke(this, cue.Id);
        // The primary/pre-roll watched cue already takes this path through
        // OnWatchedCuePreRollPropertyChanged. Only push the additional selected cues here.
        if (!ReferenceEquals(cue, _preRollWatchedCue)
            && cue.MediaSourceItem is TextPlaylistItem
            && _activeCueIds.Contains(cue.Id)
            && UpdateActiveCueTextCallback is { } textCallback
            && cue.ToModel() is MediaCueNode model)
            _ = textCallback(cue.Id, model);
    }

    private void NotifyMultiEditedAudioCue(CueNodeViewModel cue)
    {
        NotifyMultiEditedCue(cue);
        if (ReferenceEquals(cue, _preRollWatchedCue))
            return;
        if (_activeCueIds.Contains(cue.Id) && UpdateActiveCueAudioRoutesCallback is { } callback)
            _ = callback(cue.Id, cue.AudioRoutes.Select(route => route.ToModel()).ToArray());
    }

    private void NotifyMultiEditedVideoCue(CueNodeViewModel cue, int placementIndex)
    {
        NotifyMultiEditedCue(cue);
        if (ReferenceEquals(cue, _preRollWatchedCue))
            return;
        if (placementIndex < 0 || placementIndex >= cue.VideoPlacements.Count)
            return;

        var placement = cue.VideoPlacements[placementIndex].ToModel();
        if (cue.Kind == CueNodeKind.Visualizer)
        {
            if (_runningVisualizers.ContainsKey(cue.Id)
                && UpdateActiveVisualizerPlacementCallback is { } visualizerCallback)
                _ = visualizerCallback(cue.Id, VisualizerPlacementIndexOnComposition(cue, placementIndex), placement);
            return;
        }

        if (_activeCueIds.Contains(cue.Id))
        {
            if (UpdateActiveCueVideoPlacementCallback is { } callback)
                _ = callback(cue.Id, placementIndex, placement);
        }
        else
        {
            CueClipModelStaleCallback?.Invoke();
        }
    }
}
