using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaViz.Desktop.ViewModels;

namespace HaViz.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnBrowsePresets(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a projectM preset folder",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            vm.PresetDirectory = path;
    }
}
