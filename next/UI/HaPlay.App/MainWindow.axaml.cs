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

    // "Load show…" — pick a ShowDocument JSON and load it into the bound VM. An async-void handler must not let an
    // exception escape (it would crash the app), so failures land in the VM's StatusMessage.
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

    // "Save show…" — serialize the (possibly edited) show to a chosen JSON file. Local-path write truncates cleanly
    // (IStorageFile.OpenWriteAsync does not truncate, which would corrupt a shorter document).
    private async void OnSaveShowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShowSessionViewModel vm)
            return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save show",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("Show JSON") { Patterns = ["*.json"] }],
            });
            if (file is null)
                return;

            if (file.TryGetLocalPath() is not { } path)
            {
                vm.StatusMessage = "save failed: not a local file";
                return;
            }

            await File.WriteAllTextAsync(path, vm.ToShowJson());
            vm.StatusMessage = $"saved to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"save failed: {ex.Message}";
        }
    }
}
