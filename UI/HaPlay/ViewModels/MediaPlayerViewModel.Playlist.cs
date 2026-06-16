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
using S.Media.PortAudio;

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
            var settings = AppSettings.Load();
            settings.PreferLiveUyvyPassthrough = value;
            settings.Save();
        }
        catch
        {
            /* best effort */
        }
    }

    partial void OnHoldFallbackVideoChanged(bool value)
    {
        OnPropertyChanged(nameof(HoldImageSummary));
        if (value && _session is not null && IsMediaLoaded && !string.IsNullOrWhiteSpace(FallbackImagePath))
        {
            // Phase 3 — toggling hold on (with an image path already set) must re-apply the image so
            // outputs reconfigure to the image's native size.
            try { _session.ApplyFallbackImage(FallbackImagePath); }
            catch { /* best effort */ }
        }

        _session?.SetHoldFallback(value);
        if (_session is not null && IsMediaLoaded)
        {
            if (value)
            {
                StartHoldPumpTimer();
            }
            else
            {
                StopHoldPumpTimer();
                // Restore the last real decoded frame at the current playhead so single-frame sources
                // (attached_pic / album cover art) come back instead of leaving receivers stuck on the
                // no-longer-pumped template.
                try
                {
                    var pt = _session.Player.PlayClock.CurrentPosition;
                    _session.ResubmitLastCachedFramesAt(pt);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        SyncIdleSlate();
    }

    partial void OnFallbackImagePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HoldImageSummary));
        if (_session is not null && !string.IsNullOrWhiteSpace(value))
            _session.ApplyFallbackImage(value);
        if (_session is not null && IsMediaLoaded && HoldFallbackVideo && !string.IsNullOrWhiteSpace(value))
        {
            try
            {
                _session.PumpHoldFrames(_session.Player.PlayClock.CurrentPosition);
            }
            catch
            {
                /* best effort */
            }

            StartHoldPumpTimer();
        }

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
        var idx = PlaylistTabs.IndexOf(SelectedPlaylistTab);
        if (idx < 0)
            return;
        PlaylistTabs.RemoveAt(idx);
        SelectedPlaylistTab = PlaylistTabs[Math.Clamp(idx, 0, PlaylistTabs.Count - 1)];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistTab() => PlaylistTabs.Count > 1;

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

    /// <summary>Phase C.5 (§6.4) — open the PortAudio-input dialog and, on commit, add the produced
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

    /// <summary>Phase C.5 (§6.3) — open the NDI-input dialog and add the produced
    /// <see cref="NDIInputPlaylistItem"/>. The discovery list + manual-name path land alongside the
    /// dialog VM in task #3; until then the menu entry surfaces a banner so users know the data
    /// model is ready but the dialog isn't wired yet.</summary>
    [RelayCommand]
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

    /// <summary>§8.5 quick-play — load and play the first dropped file without mutating the playlist.</summary>
    public async Task QuickPlayDroppedFilesAsync(IEnumerable<string> paths)
    {
        var path = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (path is null)
            return;
        await PlayPlaylistItemAsync(new FilePlaylistItem(path)).ConfigureAwait(false);
    }

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
            // continuation that re-raises CanExecuteChanged — leaving the "Add files" entry stuck greyed
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

    /// <summary>Invoked from the view when the user double-clicks a playlist item — load it and start playing.
    /// Routes both file items and live items through the open path; live playback wiring lands in Phase C.5
    /// (currently surfaces "live items not yet supported" on play).</summary>
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

        if (_pendingCueFilePlayback is null)
        {
            CancelCueEnvelope();
            SDebug.ChangeTrace.Step("CancelCueEnvelope");
        }

        // Callable from pool threads (cue executors) as well as the view — marshal the observable
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
        if (_session is not null && !IsPlaying)
        {
            await StartPlaybackAsync().ConfigureAwait(false);
            SDebug.ChangeTrace.Step("StartPlaybackAsync");
        }

        SDebug.ChangeTrace.End("PlayPlaylistItemAsync");
    }

    /// <summary>Phase C.5 — sets <see cref="_currentPlaylistItem"/> and the file-item path projection that
    /// the existing file-based open path consumes. Live items leave <see cref="MediaFilePath"/> null and are
    /// short-circuited by <see cref="OpenOrReloadAsync"/> until live wiring lands (task #4).</summary>
    private Task PrepareCurrentItemAsync(PlaylistItem? item)
    {
        _currentPlaylistItem = item;
        MediaFilePath = item is FilePlaylistItem f ? f.Path : null;
        OnPropertyChanged(nameof(CurrentMediaDisplay));
        return Task.CompletedTask;
    }
}
