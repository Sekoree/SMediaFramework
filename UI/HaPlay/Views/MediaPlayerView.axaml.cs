using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MediaPlayerView : UserControl
{
    public MediaPlayerView()
    {
        InitializeComponent();
        // PointerReleased on the slider — TwoWay binding tracks live, but we only commit the seek
        // once the user lets go. Avoids decoding 1000 intermediate positions while they drag.
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekSliderPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        SeekSlider.AddHandler(KeyUpEvent, OnSeekSliderKeyUp,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private void OnPlaylistItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not string path) return;
        _ = vm.PlayPlaylistItemAsync(path);
    }

    private void OnSeekSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (vm.SeekToSliderCommand.CanExecute(null))
            vm.SeekToSliderCommand.Execute(null);
    }

    private void OnSeekSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (vm.SeekToSliderCommand.CanExecute(null))
                vm.SeekToSliderCommand.Execute(null);
        }
    }
}
