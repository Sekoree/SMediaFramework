using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddFileOutputDialog : Window
{
    public AddFileOutputDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(AddFileOutputDialog), MinWidth, MinHeight);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddFileOutputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void CrfModeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddFileOutputDialogViewModel vm)
            vm.UseBitrate = false;
    }

    private async void BrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddFileOutputDialogViewModel vm)
            return;
        var picks = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
        });
        if (picks.Count > 0 && picks[0].TryGetLocalPath() is { } path)
            vm.DirectoryPath = path;
    }
}
