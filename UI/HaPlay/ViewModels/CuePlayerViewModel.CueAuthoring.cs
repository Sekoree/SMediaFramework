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

    /// <summary>Phase 5.8.2 - central hook for "just-added" cues. Stamps the cue list's
    /// configured default trigger mode and (if the per-list flag is set) re-runs the renumber
    /// pass so numbering stays sequential.</summary>
    private void FinalizeAddedCue(CueNodeViewModel node)
    {
        if (SelectedCueList is null) return;
        node.TriggerMode = SelectedCueList.DefaultTriggerMode;
        if (SelectedCueList.AutoRenumberOnInsert)
            RenumberFlat(SelectedCueList.Nodes, start: 1, step: 1);
        RefreshCueTargetDisplays();
    }

    /// <summary>"+ Media" - cancel-safe: the picker runs FIRST and a cue row is only created per
    /// successfully picked file. A dismissed picker leaves the cue list untouched (the old flow
    /// seeded an empty placeholder cue that survived the cancel and had to be removed by hand).</summary>
    [RelayCommand]
    private async Task AddMediaCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;

        var picked = await PickMediaFilePathsAsync(allowMultiple: true);
        if (picked.Count == 0)
        {
            StatusMessage = null;
            return;
        }

        CueNodeViewModel? lastAdded = null;
        foreach (var path in picked)
        {
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

    /// <summary>Adds an empty media cue row (no source yet). Kept for programmatic/test fixture use -
    /// the "+ Media" command itself is cancel-safe and never creates placeholder cues.</summary>
    internal CueNodeViewModel? AddEmptyMediaCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return null;
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultMediaLabel,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
        return row;
    }

    [RelayCommand(CanExecute = nameof(CanAddNDIInputCue))]
    private async Task AddNDIInputCueAsync()
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

    private bool CanAddNDIInputCue() => IsNDIAvailable;

    /// <summary>Gate 6 - adds an MMD scene cue through the camera-placement dialog. The scene renders
    /// like any video cue on its composition; audio pairs as a separate cue in the same group. The
    /// cue's duration comes from the motion VMD (0 = bind pose, holds until stopped) and the physics
    /// bake starts in the background immediately ("always pre-bake if possible").</summary>
    [RelayCommand]
    private async Task AddMMDCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddMMDDialogViewModel();
        var dialog = new Views.Dialogs.AddMMDDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<MMDPlaylistItem?>(owner);
        if (result is null) return;
        var row = AddLiveInputCue(result, result.DisplayName);
        HaPlayPlaybackHelpers.StartBackgroundPhysicsBake(result);

        if (row is not null && result.MotionPath is { Length: > 0 } motionPath && File.Exists(motionPath))
        {
            var duration = await Task.Run(() =>
            {
                try { return S.Media.Source.MMD.VMDDocument.Load(motionPath).Duration; }
                catch { return TimeSpan.Zero; }
            });
            if (duration > TimeSpan.Zero)
                row.DurationMs = (int)duration.TotalMilliseconds;
        }
    }

    /// <summary>Gate 5 - adds a YouTube media cue through the same stream-selection dialog the deck uses
    /// (selectable video/audio/subtitle tracks + caching of the selected pair). The produced cue carries
    /// the prepared caption sidecar as its subtitle selection, so it renders like any sidecar subtitle.</summary>
    [RelayCommand]
    private async Task AddYouTubeCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddYouTubeDialogViewModel();
        var dialog = new Views.Dialogs.AddYouTubeDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<YouTubePlaylistItem?>(owner);
        if (result is null) return;

        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = result.DisplayName,
            MediaSourceItem = result,
            SourceOrAction = result.DisplayName,
            // The prepared caption sidecar rides as a normal sidecar subtitle selection.
            PersistedSubtitles = result.Subtitles,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;

        // Reliable mode means the selected streams are already in the local cache - probe that asset
        // like any file so the drawer gets exact duration/channels/fps/resolution (and audio-only
        // items correctly drop the Video tab). The item-metadata duration is the fallback.
        var assetPath = YouTubeRuntime.Preparer.AssetPathFor(
            result.VideoId, result.VideoStreamDescriptor, result.AudioStreamDescriptor, result.IncludeThumbnail);
        if (File.Exists(assetPath))
            await ProbeAndAssignDurationAsync(row, assetPath);
        else if (result.DurationSeconds is { } seconds and > 0)
            row.DurationMs = (int)TimeSpan.FromSeconds(seconds).TotalMilliseconds;
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

    private CueNodeViewModel? AddLiveInputCue(PlaylistItem source, string label)
    {
        var parent = SelectedParentCollection();
        if (parent is null) return null;
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
        return row;
    }

    private const int StaticCueDefaultDurationMs = 5000;

    /// <summary>Adds a still-image cue (held for the cue's custom duration). Default 5 s, editable in the drawer.</summary>
    [RelayCommand]
    private async Task AddImageCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var path = await PickImageFilePathAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        var (imgW, imgH) = await Task.Run(() =>
            FallbackImageLoader.TryGetImageSize(path, out var w, out var h) ? (w, h) : (0, 0));

        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Path.GetFileNameWithoutExtension(path),
            MediaSourceItem = new ImagePlaylistItem(path),
            SourceOrAction = path,
            SourceHasVideo = true,
            SourceVideoWidth = imgW,
            SourceVideoHeight = imgH,
            DurationMs = StaticCueDefaultDurationMs,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    /// <summary>Adds a standalone caption-overlay cue: a sidecar subtitle file rendered as timed captions,
    /// placeable on a composition via the Video tab (no media clip). Held for the cue's custom duration.</summary>
    [RelayCommand]
    private async Task AddSubtitleCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var path = await PickSubtitleFilePathAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Path.GetFileNameWithoutExtension(path),
            MediaSourceItem = new SubtitlePlaylistItem(path),
            SourceOrAction = path,
            SourceHasVideo = true,
            SourceVideoWidth = 1920,
            SourceVideoHeight = 1080,
            DurationMs = StaticCueDefaultDurationMs,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = "Subtitle cue added - place it on a composition in the Video tab.";
    }

    private static async Task<string?> PickSubtitleFilePathAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = "Pick a subtitle file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub", "*.smi", "*.sbv"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    /// <summary>Adds an editable text/title cue (rendered, held for the cue's custom duration). Default 5 s.</summary>
    [RelayCommand]
    private void AddTextCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;

        var text = new TextPlaylistItem { Text = Strings.CueNodeDefaultTextLabel };
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = text.DisplayName,
            MediaSourceItem = text,
            SourceOrAction = text.DisplayName,
            SourceHasVideo = true,
            SourceVideoWidth = text.CanvasWidth,
            SourceVideoHeight = text.CanvasHeight,
            DurationMs = StaticCueDefaultDurationMs,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    private static async Task<string?> PickImageFilePathAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.PickImageFileDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.ImageFileTypeLabel) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tiff"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    [RelayCommand(CanExecute = nameof(CanBrowseMediaSource))]
    private async Task BrowseMediaSourceAsync()
    {
        if (SelectedMediaCue is null)
            return;
        var path = await PickMediaFilePathAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var targets = SelectedMediaTargets();
            _isApplyingMultiEdit = true;
            try
            {
                foreach (var mediaCue in targets)
                {
                    mediaCue.MediaSourceItem = new FilePlaylistItem(path);
                    mediaCue.SourceCapabilitiesKnown = false;
                    mediaCue.SourceOrAction = path;
                    mediaCue.Label = Path.GetFileNameWithoutExtension(path);
                }
            }
            finally
            {
                _isApplyingMultiEdit = false;
            }

            await Task.WhenAll(targets.Select(cue => ProbeAndAssignDurationAsync(cue, path)));
            RefreshMultiEditSelectionState();
        }
    }

    /// <summary>Open the file once, probe duration + audio/video stream info + audio channel
    /// count, and write the lot onto the cue VM. The drawer's Audio + Video tab visibility and
    /// hints depend on these - landing them right away (before <c>StatusMessage</c> resets)
    /// keeps the UI accurate even for the cancel-leaves-empty-cue case.</summary>
    private static async Task ProbeAndAssignDurationAsync(CueNodeViewModel row, string path)
    {
        var probe = await CueMediaProbe.TryProbeAsync(path).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (probe is null)
            {
                row.SourceCapabilitiesKnown = true;
                row.DurationMs = 0;
                row.SourceHasVideo = false;
                row.SourceHasAudio = false;
                row.SourceAudioChannels = 0;
                row.SourceVideoIsAttachedPicture = false;
                row.SourceFrameRateNum = 0;
                row.SourceFrameRateDen = 0;
                row.SourceVideoWidth = 0;
                row.SourceVideoHeight = 0;
                row.SetAudioTrackChoices([]);
                row.SetVideoTrackChoices([]);
                row.SetSubtitleTrackChoices([]);
                return;
            }

            row.SourceCapabilitiesKnown = true;
            row.DurationMs = probe.Value.DurationMs ?? 0;
            row.SourceHasVideo = probe.Value.HasVideo;
            row.SourceHasAudio = probe.Value.HasAudio;
            row.SourceAudioChannels = probe.Value.AudioChannels;
            row.SourceVideoIsAttachedPicture = probe.Value.VideoIsAttachedPicture;
            row.SourceFrameRateNum = probe.Value.SourceFrameRateNum;
            row.SourceFrameRateDen = probe.Value.SourceFrameRateDen;
            row.SourceVideoWidth = probe.Value.SourceVideoWidth;
            row.SourceVideoHeight = probe.Value.SourceVideoHeight;
            row.SetAudioTrackChoices(probe.Value.AudioTracks);
            row.SetVideoTrackChoices(probe.Value.VideoTracks);
            row.SetSubtitleTrackChoices(probe.Value.SubtitleTracks);
        });
    }

    private bool CanBrowseMediaSource() => SelectedMediaCue is not null;

    /// <summary>Fills the audio-track picker for cues that were loaded from disk (no probe yet).
    /// Stream-table probe only - cheap enough to run on first selection.</summary>
    private static async Task EnsureAudioTrackChoicesAsync(CueNodeViewModel node)
    {
        if (node.MediaSourceItem is not FilePlaylistItem file)
            return;

        if (node.AudioTrackChoices.Count == 0)
        {
            var tracks = await CueMediaProbe.TryProbeAudioTracksAsync(file.Path).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A Browse-media probe may have filled the list while we were probing; keep its result.
                if (node.AudioTrackChoices.Count == 0)
                    node.SetAudioTrackChoices(tracks);
            });
        }

        if (node.VideoTrackChoices.Count == 0)
        {
            var tracks = await CueMediaProbe.TryProbeVideoTracksAsync(file.Path).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (node.VideoTrackChoices.Count == 0)
                    node.SetVideoTrackChoices(tracks);
            });
        }

        if (node.SubtitleTrackChoices.Count == 0)
        {
            var subs = await CueMediaProbe.TryProbeSubtitleTracksAsync(file.Path).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (node.SubtitleTrackChoices.Count == 0)
                    node.SetSubtitleTrackChoices(subs);
            });
        }
    }

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
            Extra = CueActionKind.OSCOut.ToString(),
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

    /// <summary>Adds a Jump (control-flow) cue after the selection. When a cue is selected it becomes
    /// the jump's initial target - the natural "loop back to this" gesture: select the section start,
    /// add a jump at the end, set the jump's trigger to auto-follow/continue, and the section loops.
    /// Targets are stable cue IDs, so renumbering/auto-reorder never retargets the jump.</summary>
    [RelayCommand]
    private void AddJumpCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var target = SelectedCueNode is { } sel && sel.Kind != CueNodeKind.Comment
                     // With a Group selected, the new Jump is inserted as that Group's first/last child.
                     // Auto-targeting the same Group would make Group→Jump→Group an immediate cycle.
                     && !(sel.IsGroup && ReferenceEquals(parent, sel.Children))
            ? sel
            : null;
        var row = new CueNodeViewModel(CueNodeKind.Jump)
        {
            Number = NextNumber(parent),
            Label = target is null
                ? Strings.CueKindJumpLabel
                : $"{Strings.CueKindJumpLabel} → {(string.IsNullOrWhiteSpace(target.Number) ? target.Label : target.Number)}",
            Extra = "Always",
            SourceOrAction = "fire",
        };
        if (target is not null)
            row.JumpTargetIds.Add(target.Id);
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    /// <summary>With a Jump cue selected earlier and any other cue selected NOW: adds the current
    /// selection to the most recently selected jump's target pool (multi-target/random authoring).</summary>
    [RelayCommand]
    private void AddSelectedCueAsJumpTarget()
    {
        if (SelectedCueNode is not { } target || target.Kind is CueNodeKind.Group or CueNodeKind.Jump)
            return;
        var jump = EnumerateAllCueNodes().LastOrDefault(c => c.Kind == CueNodeKind.Jump);
        if (jump is null || jump.JumpTargetIds.Contains(target.Id))
            return;
        jump.JumpTargetIds.Add(target.Id);
        RefreshCueTargetDisplays();
        StatusMessage = Strings.Format(nameof(Strings.CueTriggeredStatusFormat), $"{jump.Label}: +{CueDisplay(target)}");
    }

    /// <summary>Adds a Visualizer cue (#26): fire = start (or stop) the projectM layer on a chosen
    /// composition at a configurable section of the frame. Defaults to the selected composition
    /// full-canvas; edit placement in the drawer.</summary>
    [RelayCommand]
    private void AddVisualizerCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Visualizer)
        {
            Number = NextNumber(parent),
            Label = Strings.CueKindVisualizerLabel,
            Extra = "Start",
            VisualizerRenderWidth = 1920,
            VisualizerRenderHeight = 1080,
            VisualizerRenderFps = 60,
        };
        // Seed a full-canvas placement on the selected (or first) composition - edit it on the Video tab
        // exactly like a media cue's placement (#26 v3).
        var seedComp = SelectedComposition?.Id ?? SelectedCueList?.Compositions.FirstOrDefault()?.Id;
        if (seedComp is { } compId && compId != Guid.Empty)
            row.VideoPlacements.Add(CueVideoPlacementViewModel.FromModel(new CueVideoPlacement
            {
                CompositionId = compId,
                // Layer 0 like any other first video placement: surface layers always render above
                // frame-backed media anyway, and LayerIndex only orders surface layers among themselves.
                LayerIndex = 0,
            }));
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

        // Applies to the whole multi-selection. A node whose selected ancestor group is also in
        // the selection is skipped - removing the group removes it.
        var targets = EffectiveSelection().ToList();
        targets.RemoveAll(node => targets.Any(other =>
            !ReferenceEquals(other, node) && ContainsNode(other.Children, node)));

        // Remove has no confirmation dialog (operators mis-click under pressure), so every removal
        // is undoable: snapshot the models + original positions before mutating, and offer the
        // restore through an action toast. Ids are preserved on restore so jump targets re-resolve.
        var listRef = SelectedCueList;
        var undo = new List<RemovedCueEntry>();
        foreach (var node in targets)
        {
            if (FindParentCollection(listRef.Nodes, node) is IList<CueNodeViewModel> parentList)
            {
                undo.Add(new RemovedCueEntry(
                    node.ToModel(),
                    FindParentGroup(listRef.Nodes, node)?.Id,
                    parentList.IndexOf(node)));
            }
        }

        var orderedBefore = EnumerateFireableCueOrder().ToList();
        var removedFireable = ResolveFireableCue(SelectedCueNode) ?? SelectedCueNode;
        var removedFireableIndex = orderedBefore.FindIndex(c => ReferenceEquals(c, removedFireable));
        var removed = 0;
        foreach (var node in targets)
        {
            if (RemoveNodeRecursive(SelectedCueList.Nodes, node))
                removed++;
        }
        if (removed == 0)
            return;
        RefreshCueTargetDisplays();
        PruneSelectionToCurrentTree();
        ReconcileTransportAfterTreeMutation(removedFireableIndex);
        if (removed > 1)
            StatusMessage = $"Removed {removed} cues.";
        if (undo.Count > 0)
        {
            var message = removed == 1
                ? $"Removed cue {CueDisplay(targets[0])}."
                : $"Removed {removed} cues.";
            ToastCenter.PostAction(message, "Undo", () => RestoreRemovedCues(listRef, undo));
        }
    }

    /// <summary>Model + original position of one removed cue, for the undo toast.</summary>
    private readonly record struct RemovedCueEntry(CueNode Model, Guid? ParentGroupId, int Index);

    private static CueNodeViewModel? FindParentGroup(
        IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target))
                return node;
            if (FindParentGroup(node.Children, target) is { } nested)
                return nested;
        }
        return null;
    }

    private static CueNodeViewModel? FindNodeById(IEnumerable<CueNodeViewModel> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
                return node;
            if (FindNodeById(node.Children, id) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>Undo for <see cref="RemoveNode"/>: re-inserts the snapshots at their original
    /// positions (ascending index order rebuilds sibling order exactly). The original parent group
    /// may itself have been removed since - those cues land at the list root rather than vanishing.</summary>
    private void RestoreRemovedCues(CueListEditorViewModel list, List<RemovedCueEntry> entries)
    {
        var restored = new List<CueNodeViewModel>();
        foreach (var entry in entries.OrderBy(e => e.Index))
        {
            IList<CueNodeViewModel> parent = list.Nodes;
            if (entry.ParentGroupId is { } groupId
                && FindNodeById(list.Nodes, groupId) is { IsGroup: true } group)
                parent = group.Children;
            var vm = CueNodeViewModel.FromModel(entry.Model, ResolveOutputLine);
            parent.Insert(Math.Clamp(entry.Index, 0, parent.Count), vm);
            restored.Add(vm);
        }
        if (restored.Count == 0)
            return;

        if (ReferenceEquals(list, SelectedCueList))
        {
            UpdateSelection(restored);
            RefreshCueTargetDisplays();
            RefreshRowStatuses();
            RebuildUpcomingCues();
            GoCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
        }
        StatusMessage = restored.Count == 1 ? "Restored removed cue." : $"Restored {restored.Count} cues.";
    }

    private bool CanRemoveNode() => SelectedCueList is not null && SelectedCueNode is not null;
}
