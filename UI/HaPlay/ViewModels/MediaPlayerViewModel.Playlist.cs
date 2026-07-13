using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.NDI;
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel
{
    [RelayCommand(CanExecute = nameof(CanRemovePlayer))]
    private async Task RemovePlayer()
    {
        if (_requestRemove is null) return;
        await _requestRemove(this);
    }

    private bool CanRemovePlayer() => _requestRemove is not null;

    partial void OnPreferLiveUyvyPassthroughChanged(bool value)
    {
        PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = value;
        try
        {
            AppSettings.Update(settings => settings.PreferLiveUyvyPassthrough = value);
        }
        catch
        {
            /* best effort */
        }
    }

    partial void OnHoldFallbackVideoChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(HoldImageSummary));
        // Apply/clear the hold image as the composition's held top layer while playing; the idle slate
        // covers the not-playing (and ShowSession audio-only) cases.
        if (ShowSessionActive)
            _ = ApplyShowSessionHoldImageAsync();
        SyncIdleSlate();
    }

    /// <summary>Clears the HOLD idle image (the dialog's Clear button).</summary>
    [RelayCommand]
    private void ClearFallbackImage() => FallbackImagePath = null;

    partial void OnFallbackImagePathChanged(string? value)
    {
        _ = value;
        OnPropertyChanged(nameof(HoldImageSummary));
        // A new image while HOLD is engaged re-renders the held top layer in place.
        if (ShowSessionActive && HoldFallbackVideo)
            _ = ApplyShowSessionHoldImageAsync();
        SyncIdleSlate();
    }

    [RelayCommand]
    private void AddPlaylistTab()
    {
        var tab = new PlaylistTabViewModel($"Set {PlaylistTabs.Count + 1}");
        PlaylistTabs.Add(tab);
        SelectedPlaylistTab = tab;
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistTab))]
    private void RemovePlaylistTab()
    {
        if (SelectedPlaylistTab is null || PlaylistTabs.Count <= 1)
            return;
        if (IsPlayingFromTab(SelectedPlaylistTab))
        {
            StatusMessage = "Stop playback before removing the active Set.";
            return;
        }
        var idx = PlaylistTabs.IndexOf(SelectedPlaylistTab);
        if (idx < 0)
            return;
        PlaylistTabs.RemoveAt(idx);
        SelectedPlaylistTab = PlaylistTabs[Math.Clamp(idx, 0, PlaylistTabs.Count - 1)];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistTab() => PlaylistTabs.Count > 1;

    /// <summary>Removes a SPECIFIC Set (the rename-mode X + the right-click context menu), as opposed to
    /// <see cref="RemovePlaylistTabCommand"/> which removes the selected Set. Never removes the last Set.
    /// Re-selects a neighbour only when the removed Set was the selected one.</summary>
    [RelayCommand]
    private void RemovePlaylistTabItem(PlaylistTabViewModel? tab)
    {
        if (tab is null || PlaylistTabs.Count <= 1)
            return;
        if (IsPlayingFromTab(tab))
        {
            StatusMessage = "Stop playback before removing the active Set.";
            return;
        }
        var idx = PlaylistTabs.IndexOf(tab);
        if (idx < 0)
            return;
        var wasSelected = ReferenceEquals(SelectedPlaylistTab, tab);
        PlaylistTabs.RemoveAt(idx);
        if (wasSelected)
            SelectedPlaylistTab = PlaylistTabs[Math.Clamp(idx, 0, PlaylistTabs.Count - 1)];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    private bool IsPlayingFromTab(PlaylistTabViewModel tab) =>
        CurrentPlayingItem is not null && ReferenceEquals(_activePlaybackTab, tab);

    /// <summary>Duplicates a Set (right-click context menu): a deep copy of its items (each with a fresh id so
    /// selection/now-playing tracking stays independent) and its per-Set flags, inserted right after the
    /// original and selected.</summary>
    [RelayCommand]
    private void DuplicatePlaylistTab(PlaylistTabViewModel? tab)
    {
        if (tab is null)
            return;
        var copy = new PlaylistTabViewModel(BuildDuplicateTabName(tab.Name))
        {
            IsLooping = tab.IsLooping,
            AutoAdvance = tab.AutoAdvance,
            Shuffle = tab.Shuffle,
            RepeatAll = tab.RepeatAll,
        };
        foreach (var item in tab.Items)
            copy.Items.Add(item with { Id = Guid.NewGuid() });

        var idx = PlaylistTabs.IndexOf(tab);
        if (idx >= 0)
            PlaylistTabs.Insert(idx + 1, copy);
        else
            PlaylistTabs.Add(copy);
        SelectedPlaylistTab = copy;
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Pure: "Set 1" → "Set 1 copy".</summary>
    internal static string BuildDuplicateTabName(string? name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Set" : name.Trim();
        return $"{baseName} copy";
    }

    [RelayCommand]
    private async Task SavePlaylistTabAsync()
    {
        var top = TryGetMainWindow();
        var tab = SelectedPlaylistTab;
        if (top is null || tab is null)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save playlist tab",
            DefaultExtension = PlaylistIO.FileExtension,
            SuggestedFileName = SanitizeFileName(tab.Name, PlaylistIO.FileExtension),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay playlist") { Patterns = ["*." + PlaylistIO.FileExtension] },
            ],
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(opts);
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await PlaylistIO.SaveAsync(tab.ToConfig(), path).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = null);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Save playlist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadPlaylistTabAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Load playlist tab",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay playlist") { Patterns = ["*." + PlaylistIO.FileExtension, "*.json"] },
                new FilePickerFileType("M3U playlist") { Patterns = ["*.m3u", "*.m3u8"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            var config = await PlaylistIO.LoadAsync(path).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var tab = PlaylistTabViewModel.FromConfig(config);
                if (string.IsNullOrWhiteSpace(tab.Name))
                    tab.Name = Path.GetFileNameWithoutExtension(path);
                PlaylistTabs.Add(tab);
                SelectedPlaylistTab = tab;
                RemovePlaylistTabCommand.NotifyCanExecuteChanged();
                StatusMessage = null;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Load playlist failed: {ex.Message}");
        }
    }

    /// <summary>Phase C.5 (§6.4) - open the PortAudio-input dialog and, on commit, add the produced
    /// <see cref="PortAudioInputPlaylistItem"/> to the active playlist tab. Live items sit alongside
    /// file items in the same list and round-trip through the project file (§6.8).</summary>
    [RelayCommand]
    private async Task AddPortAudioInputAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddPortAudioInputDialogViewModel();
        dialogVm.ReloadHostApis();
        var dialog = new Views.Dialogs.AddPortAudioInputDialog { DataContext = dialogVm };

        var result = await dialog.ShowDialog<PortAudioInputPlaylistItem?>(top);
        if (result is null) return;

        PlaylistItems.Add(result);
        SelectedPlaylistItem = result;
    }

    /// <summary>Phase C.5 (§6.3) - open the NDI-input dialog and add the produced
    /// <see cref="NDIInputPlaylistItem"/>. The discovery list + manual-name path land alongside the
    /// dialog VM in task #3; until then the menu entry surfaces a banner so users know the data
    /// model is ready but the dialog isn't wired yet.</summary>
    [RelayCommand(CanExecute = nameof(CanAddNDIInput))]
    private async Task AddNDIInputAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddNDIInputDialogViewModel();
        await dialogVm.StartDiscoveryAsync();
        var dialog = new Views.Dialogs.AddNDIInputDialog { DataContext = dialogVm };

        try
        {
            var result = await dialog.ShowDialog<NDIInputPlaylistItem?>(top);
            if (result is null) return;
            PlaylistItems.Add(result);
            SelectedPlaylistItem = result;
        }
        finally
        {
            dialogVm.StopDiscovery();
        }
    }

    private bool CanAddNDIInput() => IsNDIAvailable;

    /// <summary>Gate 6 - opens the MMD scene dialog (model/motion pickers + the rudimentary 3D
    /// camera-placement preview) and adds the produced item to the playlist.</summary>
    [RelayCommand]
    private async Task AddMMDAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddMMDDialogViewModel();
        var dialog = new Views.Dialogs.AddMMDDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<MMDPlaylistItem?>(top);
        if (result is null) return;
        PlaylistItems.Add(result);
        SelectedPlaylistItem = result;
        HaPlayPlaybackHelpers.StartBackgroundPhysicsBake(result);
    }

    /// <summary>Gate 5 - opens the YouTube stream-selection dialog (resolve → pick video/audio/subtitle
    /// streams → download &amp; cache) and adds the produced item. Muxed streams are rarely offered, so the
    /// dialog selects a separate video-only + audio-only pair; playback later runs from the local cache.</summary>
    [RelayCommand]
    private async Task AddYouTubeAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddYouTubeDialogViewModel();
        var dialog = new Views.Dialogs.AddYouTubeDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<YouTubePlaylistItem?>(top);
        if (result is null) return;
        PlaylistItems.Add(result);
        SelectedPlaylistItem = result;
    }

    /// <summary>Opens the common per-item properties dialog (details / tracks / MMD scene / YouTube
    /// streams) for the selected playlist item and applies its edits. Edits keep the item's
    /// <see cref="PlaylistItem.Id"/>, so cue references and the current-item pointer stay valid.</summary>
    [RelayCommand(CanExecute = nameof(CanShowItemProperties))]
    private async Task ShowItemPropertiesAsync()
    {
        var item = SelectedPlaylistItem;
        var top = TryGetMainWindow();
        if (item is null || top is null) return;

        var dialogVm = new Dialogs.MediaPropertiesDialogViewModel(item);
        var dialog = new Views.Dialogs.MediaPropertiesDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<PlaylistItem?>(top);
        if (result is null || result.Equals(item))
            return;

        var idx = PlaylistItems.IndexOf(item);
        if (idx < 0) return;
        var wasActive = ReferenceEquals(_currentPlaylistItem, item);
        PlaylistItems[idx] = result;
        if (ReferenceEquals(_currentPlaylistItem, item))
            _currentPlaylistItem = result;
        if (ReferenceEquals(CurrentPlayingItem, item)) // keep the now-playing marker on the edited item
            CurrentPlayingItem = result;
        SelectedPlaylistItem = result;
        StatusMessage = Strings.Format(nameof(Strings.MediaPropertiesAppliedStatusFormat), result.DisplayName);
        HaPlayPlaybackHelpers.StartBackgroundPhysicsBake(result);

        // An edit that changes HOW the running clip decodes - audio-track or subtitle selection - only takes
        // effect on (re)open: the live clip was opened with the old selection. When the edited item is the one
        // loaded in the deck, re-open it and restore the playhead so a movie doesn't jump back to the start.
        if (wasActive && RequiresReopenForPlayback(item, result))
        {
            var resumeAt = CurrentPosition;
            await OpenOrReloadAsync().ConfigureAwait(false);
            if (resumeAt > TimeSpan.Zero)
                await ShowSessionSeekAsync(resumeAt).ConfigureAwait(false);
        }
    }

    /// <summary>True when an edit changed a field the decoder open consumes (audio-track index or the subtitle
    /// selection) - the only edits that need the running clip re-opened to take effect.</summary>
    private static bool RequiresReopenForPlayback(PlaylistItem before, PlaylistItem after) =>
        before is FilePlaylistItem o && after is FilePlaylistItem n
        && (o.AudioTrackIndex != n.AudioTrackIndex || !o.Subtitles.SequenceEqual(n.Subtitles));

    private bool CanShowItemProperties() => SelectedPlaylistItem is not null;

    [RelayCommand]
    public void AddDroppedFilesToPlaylist(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            if (PlaylistItems.OfType<FilePlaylistItem>().Any(f =>
                    string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            PlaylistItems.Add(new FilePlaylistItem(path));
            added++;
        }

        if (added > 0 && SelectedPlaylistItem is null)
            SelectedPlaylistItem = PlaylistItems[0];
        if (added > 0)
            StatusMessage = $"Added {added} file(s) to playlist.";
    }

    [RelayCommand]
    private async Task AddFilesToPlaylistAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;

        var opts = new FilePickerOpenOptions { Title = "Add files to playlist", AllowMultiple = true };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm", "*.m4v", "*.avi", "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(opts);

            int added = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                // Dedup against existing file items (same-path live items don't make sense and don't exist here).
                if (PlaylistItems.OfType<FilePlaylistItem>().Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)))
                    continue;
                PlaylistItems.Add(new FilePlaylistItem(path));
                added++;
            }

            if (SelectedPlaylistItem is null && PlaylistItems.Count > 0)
                SelectedPlaylistItem = PlaylistItems[0];

            if (added > 0)
                StatusMessage = $"Added {added} file(s) to playlist.";
        }
        catch (Exception ex)
        {
            // Never let a picker/add failure escape the command. An unhandled exception flowing out of an
            // AsyncRelayCommand runs as an async-void throw on the UI thread, which can pre-empt the
            // continuation that re-raises CanExecuteChanged - leaving the "Add files" entry stuck greyed
            // out even though the app survives. Surface it as a status message instead (matches the other
            // picker commands, e.g. LoadPlaylistTabAsync).
            StatusMessage = $"Add files failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistItem))]
    private void RemoveFromPlaylist()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i < 0) return;
        PlaylistItems.RemoveAt(i);
        SelectedPlaylistItem = PlaylistItems.Count > 0
            ? PlaylistItems[Math.Min(i, PlaylistItems.Count - 1)]
            : null;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistItem() =>
        SelectedPlaylistItem is not null && PlaylistItems.Contains(SelectedPlaylistItem);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemUp))]
    private void MovePlaylistItemUp()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i <= 0) return;
        (PlaylistItems[i - 1], PlaylistItems[i]) = (PlaylistItems[i], PlaylistItems[i - 1]);
        SelectedPlaylistItem = item;
    }

    private bool CanMovePlaylistItemUp() =>
        SelectedPlaylistItem is not null && PlaylistItems.IndexOf(SelectedPlaylistItem) > 0;

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemDown))]
    private void MovePlaylistItemDown()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i < 0 || i >= PlaylistItems.Count - 1) return;
        (PlaylistItems[i + 1], PlaylistItems[i]) = (PlaylistItems[i], PlaylistItems[i + 1]);
        SelectedPlaylistItem = item;
    }

    private bool CanMovePlaylistItemDown()
    {
        if (SelectedPlaylistItem is null) return false;
        var idx = PlaylistItems.IndexOf(SelectedPlaylistItem);
        return idx >= 0 && idx < PlaylistItems.Count - 1;
    }

    public void MovePlaylistItem(PlaylistItem item, int targetIndex)
    {
        var sourceIndex = PlaylistItems.IndexOf(item);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= PlaylistItems.Count || sourceIndex == targetIndex)
            return;
        PlaylistItems.Move(sourceIndex, targetIndex);
        SelectedPlaylistItem = item;
    }

    /// <summary>Invoked from the view when the user double-clicks a playlist item - load it and start playing
    /// (the ShowSession open fires immediately for file and live items alike).</summary>
    public async Task PlayPlaylistItemAsync(PlaylistItem? item)
    {
        if (!SDebug.ChangeTrace.IsActive)
            SDebug.ChangeTrace.Begin("PlayPlaylistItem");
        SDebug.ChangeTrace.Step("PlayPlaylistItemAsync entered");
        if (item is null)
        {
            SDebug.ChangeTrace.End("cancelled (null item)");
            return;
        }

        // Callable from pool threads (cue executors) as well as the view - marshal the observable
        // property sets (SelectedPlaylistItem fires transport CanExecuteChanged into the buttons).
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _activePlaybackTab = SelectedPlaylistTab;
            SelectedPlaylistItem = item;
            return PrepareCurrentItemAsync(item);
        });
        SDebug.ChangeTrace.Step("PrepareCurrentItemAsync");
        await OpenOrReloadAsync().ConfigureAwait(false);
        SDebug.ChangeTrace.Step("OpenOrReloadAsync");
        SDebug.ChangeTrace.End("PlayPlaylistItemAsync");
    }

    /// <summary>Phase C.5 - sets <see cref="_currentPlaylistItem"/> and the file-item path projection that
    /// the existing file-based open path consumes. Live items leave <see cref="MediaFilePath"/> null and are
    /// short-circuited by <see cref="OpenOrReloadAsync"/> until live wiring lands (task #4).</summary>
    private Task PrepareCurrentItemAsync(PlaylistItem? item)
    {
        _currentPlaylistItem = item;
        CurrentPlayingItem = item; // "now playing" marker; cleared when the deck returns to idle.
        MediaFilePath = item is FilePlaylistItem f ? f.Path : null;
        OnPropertyChanged(nameof(CurrentMediaDisplay));
        return Task.CompletedTask;
    }
}
