using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddLocalVideoOutputDialog : Window
{
    public AddLocalVideoOutputDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(AddLocalVideoOutputDialog), MinWidth, MinHeight);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddLocalVideoOutputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void BrowseBackgroundClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddLocalVideoOutputDialogViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.BackgroundImageLabel,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.ImageFilesFilterLabel)
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
                },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            vm.BackgroundImagePath = path;
    }

    private void ClearBackgroundClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddLocalVideoOutputDialogViewModel vm)
            vm.BackgroundImagePath = null;
    }
}
