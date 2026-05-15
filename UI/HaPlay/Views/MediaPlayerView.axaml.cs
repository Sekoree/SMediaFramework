using Avalonia.Controls;
using Avalonia.Input;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MediaPlayerView : UserControl
{
    public MediaPlayerView()
    {
        InitializeComponent();
    }

    private void OnPlaylistItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not string path) return;
        _ = vm.PlayPlaylistItemAsync(path);
    }
}
