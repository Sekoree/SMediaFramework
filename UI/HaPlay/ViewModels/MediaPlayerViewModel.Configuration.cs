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
    [RelayCommand]
    private async Task BrowseFallbackAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerOpenOptions { Title = "Fallback image (PNG / JPEG)", AllowMultiple = false };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FallbackImagePath = path;
    }

    [RelayCommand]
    private async Task OpenPlayerSettingsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var dialog = new Views.Dialogs.PlayerSettingsDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private async Task OpenAudioMatrixAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        ResizeSelectedAudioMatrices();
        RebuildAudioMatrixRows();
        ApplyAllOutputMatricesToSession();
        var dialog = new Views.Dialogs.AudioMatrixDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private void DetachPlayer() => DetachRequested?.Invoke(this);

    [RelayCommand]
    private async Task OpenRoutingAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var dialog = new Views.Dialogs.RoutingDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private async Task SavePlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save player configuration",
            DefaultExtension = "haplay.json",
            SuggestedFileName = SanitizeFileName(Name),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
            ],
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(opts);
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var config = BuildPlayerConfig();
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, config, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = null);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadPlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Load player configuration",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        MediaPlayerConfig? config;
        try
        {
            await using var stream = File.OpenRead(path);
            config = await JsonSerializer
                .DeserializeAsync(stream, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Load failed: {ex.Message}");
            return;
        }

        if (config is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() => ApplyPlayerConfig(config));
    }
}
