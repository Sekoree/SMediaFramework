using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddMMDDialog : Window
{
    public AddMMDDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(AddMMDDialog), MinWidth, MinHeight);
        Closing += (_, _) => (DataContext as AddMMDDialogViewModel)?.CancelPending();
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddMMDDialogViewModel vm)
            return;
        var result = vm.TryCommit();
        if (result is null)
            return;
        Close(result);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void BrowseModelClick(object? sender, RoutedEventArgs e) =>
        _ = BrowseAsync("PMX model", ["*.pmx"], path =>
        {
            if (DataContext is AddMMDDialogViewModel vm)
                vm.ModelPath = path;
        });

    private void BrowseMotionClick(object? sender, RoutedEventArgs e) =>
        _ = BrowseAsync("VMD motion", ["*.vmd"], path =>
        {
            if (DataContext is AddMMDDialogViewModel vm)
                vm.MotionPath = path;
        });

    private void BrowseCameraClick(object? sender, RoutedEventArgs e) =>
        _ = BrowseAsync("VMD camera motion", ["*.vmd"], path =>
        {
            if (DataContext is AddMMDDialogViewModel vm)
                vm.CameraMotionPath = path;
        });

    private async Task BrowseAsync(string label, string[] patterns, Action<string> apply)
    {
        var picks = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = label,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(label) { Patterns = patterns }],
        });
        var path = picks.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            apply(path);
    }
}
