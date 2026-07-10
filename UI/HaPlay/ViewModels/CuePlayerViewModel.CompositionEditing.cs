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

public partial class CuePlayerViewModel
{
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
        comp.CompositionFrameRateChanged += OnCompositionFrameRateChanged;
        SelectedComposition = comp;
        RefreshVideoFrameRateMismatchWarning();
        SuggestPreRollRefresh();
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
        SuggestPreRollRefresh();
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
        binding.SetLineResolver(ResolveOutputLine);
        SelectedCueList.VideoOutputs.Add(binding);
        SelectedVideoOutput = binding;
        SuggestPreRollRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVideoOutput))]
    private void RemoveVideoOutput()
    {
        if (SelectedCueList is null || SelectedVideoOutput is null) return;
        if (!SelectedCueList.VideoOutputs.Remove(SelectedVideoOutput)) return;
        SelectedVideoOutput = SelectedCueList.VideoOutputs.FirstOrDefault();
        SuggestPreRollRefresh();
    }

    private bool CanRemoveVideoOutput() => SelectedVideoOutput is not null;

    partial void OnSelectedCompositionChanged(CueCompositionViewModel? value)
    {
        _ = value;
        RemoveCompositionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
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
            route.SetLineResolver(ResolveOutputLine);
            media.AudioRoutes.Add(route);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = route;
        }
        if (lastOnPrimary is not null)
            SelectedAudioRoute = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        SuggestPreRollRefresh();
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
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            SuggestPreRollRefresh();
        }
    }

    private bool CanRemoveAudioRoute() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedAudioRoute is not null;

    /// <summary>Quick-apply a multichannel downmix preset to the selected cue's audio routes for one
    /// output line (the selected route's line, else the first available audio output). Replaces that
    /// line's routes; other lines are untouched. Shares <see cref="AudioDownmixPresets"/> with the
    /// media player's audio matrix.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCueDownmix))]
    private void ApplyCueDownmixPreset(AudioDownmixPreset preset)
    {
        var targets = MediaCuesInSelection();
        if (targets.Count == 0)
            return;

        var line = SelectedAudioRoute?.OutputLineId is { } selId && selId != Guid.Empty
            ? AvailableAudioOutputs.FirstOrDefault(l => l.Definition.Id == selId) ?? AvailableAudioOutputs.FirstOrDefault()
            : AvailableAudioOutputs.FirstOrDefault();
        if (line is null)
        {
            StatusMessage = Strings.DownmixNoOutputStatus;
            return;
        }

        var outChannels = GetAudioOutputChannelCount(line);
        var lineId = line.Definition.Id;
        var applied = 0;
        string? firstNotApplicable = null;

        foreach (var media in targets)
        {
            var srcChannels = Math.Max(1, media.SourceAudioChannels);
            if (!AudioDownmixPresets.IsApplicable(preset, srcChannels, outChannels))
            {
                firstNotApplicable ??= Strings.Format(nameof(Strings.DownmixNotApplicableStatusFormat),
                    AudioDownmixPresets.DisplayName(preset), srcChannels, outChannels);
                continue;
            }

            for (var i = media.AudioRoutes.Count - 1; i >= 0; i--)
                if (media.AudioRoutes[i].OutputLineId == lineId)
                    media.AudioRoutes.RemoveAt(i);

            CueAudioRouteViewModel? first = null;
            foreach (var contrib in AudioDownmixPresets.Contributions(preset, srcChannels, outChannels))
            {
                var route = new CueAudioRouteViewModel
                {
                    SourceChannel = contrib.InputChannel,
                    OutputLineId = lineId,
                    OutputChannel = contrib.OutputChannel + 1, // cue routes are 1-based
                    GainDb = contrib.GainDb,
                };
                route.SetLineResolver(ResolveOutputLine);
                media.AudioRoutes.Add(route);
                first ??= route;
            }

            if (ReferenceEquals(media, SelectedCueNode) && first is not null)
                SelectedAudioRoute = first;
            applied++;
        }

        if (applied == 0)
        {
            StatusMessage = firstNotApplicable;
            return;
        }

        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        StatusMessage = Strings.Format(nameof(Strings.DownmixAppliedStatusFormat),
            AudioDownmixPresets.DisplayName(preset),
            applied == 1 ? line.Definition.EffectiveName : $"{applied} cues on {line.Definition.EffectiveName}");
        SuggestPreRollRefresh();
    }

    private bool CanApplyCueDownmix() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && AvailableAudioOutputs.Count > 0;

    /// <summary>P5c follow-through - load a framework <c>.mfmix</c> preset file into the selected
    /// cues' routes on the chosen target line (same replace semantics as the enum quick-applies;
    /// non-zero cells become 1-based cue routes with dB gains).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCueDownmix))]
    private async Task LoadCueMixPresetAsync()
    {
        var targets = MediaCuesInSelection();
        if (targets.Count == 0)
            return;
        var line = SelectedAudioRoute?.OutputLineId is { } selId && selId != Guid.Empty
            ? AvailableAudioOutputs.FirstOrDefault(l => l.Definition.Id == selId) ?? AvailableAudioOutputs.FirstOrDefault()
            : AvailableAudioOutputs.FirstOrDefault();
        if (line is null)
        {
            StatusMessage = Strings.DownmixNoOutputStatus;
            return;
        }

        var owner = TryGetMainWindow();
        if (owner is null) return;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.MatrixPresetLoadTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.MatrixPresetFileTypeLabel)
                    { Patterns = ["*." + S.Media.Core.Audio.AudioMixPreset.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        float[,] gains;
        string presetName;
        try
        {
            var preset = S.Media.Core.Audio.AudioMixPreset.Load(path);
            gains = preset.ToMatrix();
            presetName = preset.Name;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        var lineId = line.Definition.Id;
        var outChannels = GetAudioOutputChannelCount(line);
        var applied = 0;
        foreach (var media in targets)
        {
            for (var i = media.AudioRoutes.Count - 1; i >= 0; i--)
                if (media.AudioRoutes[i].OutputLineId == lineId)
                    media.AudioRoutes.RemoveAt(i);

            CueAudioRouteViewModel? first = null;
            for (var src = 0; src < gains.GetLength(0); src++)
            {
                for (var dst = 0; dst < Math.Min(gains.GetLength(1), outChannels); dst++)
                {
                    var linear = gains[src, dst];
                    if (linear <= 0f) continue;
                    var route = new CueAudioRouteViewModel
                    {
                        SourceChannel = src,
                        OutputLineId = lineId,
                        OutputChannel = dst + 1, // cue routes are 1-based
                        GainDb = 20.0 * Math.Log10(linear),
                    };
                    route.SetLineResolver(ResolveOutputLine);
                    media.AudioRoutes.Add(route);
                    first ??= route;
                }
            }

            if (ReferenceEquals(media, SelectedCueNode) && first is not null)
                SelectedAudioRoute = first;
            applied++;
        }

        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        StatusMessage = Strings.Format(nameof(Strings.DownmixAppliedStatusFormat), presetName,
            applied == 1 ? line.Definition.EffectiveName : $"{applied} cues on {line.Definition.EffectiveName}");
        SuggestPreRollRefresh();
    }

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
            // Default the box to the source's size (actual size, scaled down to fit the canvas),
            // centered - so a new layer lands at the video's aspect instead of stretched full-frame.
            var (fx, fy, fw, fh) = SourceFitRect(
                media.SourceVideoWidth, media.SourceVideoHeight, firstComp?.Width ?? 0, firstComp?.Height ?? 0);
            var placement = new CueVideoPlacementViewModel
            {
                CompositionId = firstComp?.Id ?? Guid.Empty,
                LayerIndex = media.VideoPlacements.Count,
            };
            placement.SetDestRect(fx, fy, fw, fh);
            media.VideoPlacements.Add(placement);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = placement;
        }
        if (lastOnPrimary is not null)
            SelectedVideoPlacement = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        RefreshVideoFrameRateMismatchWarning();
        SuggestPreRollRefresh();
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
            SuggestPreRollRefresh();
        }
    }

    private bool CanRemoveVideoPlacement() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedVideoPlacement is not null;

    [RelayCommand(CanExecute = nameof(CanEditSelectedPlacement))]
    private void EditSelectedPlacementVideoFx()
    {
        if (SelectedVideoPlacement is not { } placement)
            return;
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var sourceWidth = SelectedCueNode?.SourceVideoWidth ?? 0;
        var sourceHeight = SelectedCueNode?.SourceVideoHeight ?? 0;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            sourceWidth = 1920;
            sourceHeight = 1080;
        }
        else
        {
            sourceWidth = Math.Max(16, sourceWidth);
            sourceHeight = Math.Max(16, sourceHeight);
        }

        var targetName = string.IsNullOrWhiteSpace(SelectedCueNode?.Label)
            ? "Selected placement"
            : SelectedCueNode.Label;
        var vm = new Dialogs.MappingEditorViewModel(
            targetName,
            sourceWidth,
            sourceHeight,
            placement.VideoFx,
            apply: (mapping, enabled) =>
            {
                placement.VideoFx = mapping;
                placement.VideoFxEnabled = enabled;
            },
            initialEnabled: placement.VideoFx is not null && placement.VideoFxEnabled,
            dialogTitlePrefix: "Video FX",
            enableLabel: "Enable video FX",
            sizeLabel: "FX size");

        var dialog = new Views.Dialogs.MappingEditorDialog { DataContext = vm };
        dialog.Show(owner);
    }

    /// <summary>Quick destination-rect layouts for the selected placement (full / halves / quadrants).</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlacement))]
    private void ApplyPlacementLayout(string? preset)
    {
        if (SelectedVideoPlacement is not { } p) return;
        switch (preset)
        {
            case "fit":
            {
                var comp = SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                    ?? SelectedCueList?.Compositions.FirstOrDefault();
                var node = SelectedCueNode;
                var (fx, fy, fw, fh) = SourceFitRect(
                    node?.SourceVideoWidth ?? 0, node?.SourceVideoHeight ?? 0, comp?.Width ?? 0, comp?.Height ?? 0);
                p.SetDestRect(fx, fy, fw, fh);
                break;
            }
            case "full": p.SetDestRect(0, 0, 1, 1); break;
            case "left": p.SetDestRect(0, 0, 0.5, 1); break;
            case "right": p.SetDestRect(0.5, 0, 0.5, 1); break;
            case "top": p.SetDestRect(0, 0, 1, 0.5); break;
            case "bottom": p.SetDestRect(0, 0.5, 1, 0.5); break;
            case "tl": p.SetDestRect(0, 0, 0.5, 0.5); break;
            case "tr": p.SetDestRect(0.5, 0, 0.5, 0.5); break;
            case "bl": p.SetDestRect(0, 0.5, 0.5, 0.5); break;
            case "br": p.SetDestRect(0.5, 0.5, 0.5, 0.5); break;
            default: return;
        }
        SuggestPreRollRefresh();
    }

    /// <summary>Normalized destination rect that places a <paramref name="srcW"/>×<paramref name="srcH"/>
    /// source on a <paramref name="canvasW"/>×<paramref name="canvasH"/> canvas at its own size, centered -
    /// scaled down (aspect preserved) only when the source is larger than the canvas, never scaled up.
    /// Falls back to the full frame when any dimension is unknown.</summary>
    internal static (double X, double Y, double W, double H) SourceFitRect(int srcW, int srcH, int canvasW, int canvasH)
    {
        if (srcW <= 0 || srcH <= 0 || canvasW <= 0 || canvasH <= 0)
            return (0.0, 0.0, 1.0, 1.0);

        var scale = Math.Min(1.0, Math.Min((double)canvasW / srcW, (double)canvasH / srcH));
        var w = Math.Clamp(srcW * scale / canvasW, 0.02, 1.0);
        var h = Math.Clamp(srcH * scale / canvasH, 0.02, 1.0);
        var x = Math.Clamp((1.0 - w) / 2.0, 0.0, 1.0 - w);
        var y = Math.Clamp((1.0 - h) / 2.0, 0.0, 1.0 - h);
        return (x, y, w, h);
    }

    /// <summary>Quick source-crop presets for the selected placement.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlacement))]
    private void ApplyCropPreset(string? preset)
    {
        if (SelectedVideoPlacement is not { } p) return;
        switch (preset)
        {
            case "none": p.CropLeft = p.CropTop = p.CropRight = p.CropBottom = 0; break;
            case "centerH": p.CropTop = p.CropBottom = 0; p.CropLeft = p.CropRight = 0.25; break; // centre 50% wide
            case "centerV": p.CropLeft = p.CropRight = 0; p.CropTop = p.CropBottom = 0.25; break; // centre 50% tall
            case "center": p.CropLeft = p.CropTop = p.CropRight = p.CropBottom = 0.25; break;      // centre 50% box
            default: return;
        }
        SuggestPreRollRefresh();
    }

    private bool CanEditSelectedPlacement() => SelectedVideoPlacement is not null;
}
