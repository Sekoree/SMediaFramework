using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaPlay.Core;
using S.Media.Present.Avalonia;

namespace HaPlay.App;

public partial class MainWindow : Window
{
    // VideoOpenGlControl has no parameterless ctor, so XAML can't instantiate it — create it in code and host it in
    // the named Border. The composition preview attaches to it once a show with a composition loads.
    private readonly VideoOpenGlControl _preview = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        if (this.FindControl<Border>("PreviewHost") is { } host)
            host.Child = _preview;
    }

    // "Load show…" — pick a ShowDocument JSON, load it into the bound VM, and attach the preview surface to the
    // show's first composition. An async-void handler must not let an exception escape; failures land in StatusMessage.
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
            await vm.AttachPreviewAsync(_preview);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"load failed: {ex.Message}";
        }
    }
}
