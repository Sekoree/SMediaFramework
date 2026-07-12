using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Views.Dialogs;

/// <summary>projectM visualizer configuration for one deck: preset folder + render resolution/fps.
/// Binds directly to the player view-model; the resolution buttons write through
/// <see cref="MediaPlayerViewModel.SetVisualizerResolution"/> so a running visualizer re-applies.</summary>
public partial class VisualizerSettingsDialog : Window
{
    public VisualizerSettingsDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(VisualizerSettingsDialog), MinWidth, MinHeight);
    }

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        // Persist whatever's in the numeric fields (the buttons persist immediately; direct edits on close).
        if (DataContext is MediaPlayerViewModel vm)
            vm.SetVisualizerResolution(vm.VisualizerWidth, vm.VisualizerHeight, vm.VisualizerFps);
        Close();
    }

    private async void BrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm)
            return;
        var start = vm.VisualizerPresetDirectory is { Length: > 0 } current && System.IO.Directory.Exists(current)
            ? await StorageProvider.TryGetFolderFromPathAsync(current)
            : null;
        var picks = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.VisualizerPresetPickerTooltip,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picks.Count > 0 && picks[0].TryGetLocalPath() is { } path)
            vm.SetVisualizerPresetDirectory(path);
    }

    private void MatchOutputClick(object? sender, RoutedEventArgs e) => Apply(0, 0, 0);
    private void Preset720Click(object? sender, RoutedEventArgs e) => Apply(1280, 720, 60);
    private void Preset1080Click(object? sender, RoutedEventArgs e) => Apply(1920, 1080, 60);
    private void Preset1440Click(object? sender, RoutedEventArgs e) => Apply(2560, 1440, 60);
    private void Preset4kClick(object? sender, RoutedEventArgs e) => Apply(3840, 2160, 60);

    private void Apply(int w, int h, int fps)
    {
        if (DataContext is MediaPlayerViewModel vm)
            vm.SetVisualizerResolution(w, h, fps);
    }
}
