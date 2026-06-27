using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaPlay.Core;

namespace HaPlay.App;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    // "Load show…" — pick a ShowDocument JSON and load it into the bound view model. An async-void handler must not
    // let an exception escape (it would crash the app), so failures land in the VM's StatusMessage.
    private async void OnLoadShowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShowSessionViewModel vm)
            return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load show",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Show JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0)
                return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.LoadShow(await reader.ReadToEndAsync());
            await vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"load failed: {ex.Message}";
        }
    }
}
