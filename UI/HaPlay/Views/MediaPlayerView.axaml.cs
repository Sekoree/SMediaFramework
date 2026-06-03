using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.ViewModels;
using S.Media.Core;

namespace HaPlay.Views;

public partial class MediaPlayerView : UserControl
{
    private const double CompactWidthThreshold = 500;

    private static readonly DataFormat<PlaylistItem> PlaylistItemFormat =
        DataFormat.CreateInProcessFormat<PlaylistItem>("haplay-playlist-item");

    public MediaPlayerView()
    {
        InitializeComponent();
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekSliderPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        SeekSlider.AddHandler(KeyUpEvent, OnSeekSliderKeyUp,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        VolumeSlider.AddHandler(InputElement.DoubleTappedEvent, OnVolumeSliderDoubleTapped,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnUserControlKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(PlaylistListBox, true);
        PlaylistListBox.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver, RoutingStrategies.Bubble);
        PlaylistListBox.AddHandler(DragDrop.DropEvent, OnPlaylistDrop, RoutingStrategies.Bubble);
        PlaylistListBox.AddHandler(PointerPressedEvent, OnPlaylistPointerPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerMovedEvent, OnSeekSliderPointerMoved, RoutingStrategies.Tunnel);
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isCompact = e.NewSize.Width < CompactWidthThreshold;
        if (isCompact && !Classes.Contains("compact"))
            Classes.Add("compact");
        else if (!isCompact && Classes.Contains("compact"))
            Classes.Remove("compact");
    }

    private PointerPressedEventArgs? _dragPressedArgs;
    private PlaylistItem? _dragCandidate;
    private Point _dragStartPoint;

    private void OnPlaylistPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PlaylistListBox).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount >= 2)
            {
                CancelDragCandidate();
                return;
            }
            _dragStartPoint = e.GetPosition(PlaylistListBox);
            _dragCandidate = GetPlaylistItemAtPoint(_dragStartPoint);
            _dragPressedArgs = _dragCandidate is not null ? e : null;
            if (_dragCandidate is not null)
            {
                PlaylistListBox.AddHandler(PointerMovedEvent, OnPlaylistPointerMoved, RoutingStrategies.Tunnel);
                PlaylistListBox.AddHandler(PointerReleasedEvent, OnPlaylistPointerReleased, RoutingStrategies.Tunnel);
            }
        }
    }

    private void OnPlaylistPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CancelDragCandidate();
    }

    private void CancelDragCandidate()
    {
        _dragCandidate = null;
        _dragPressedArgs = null;
        PlaylistListBox.RemoveHandler(PointerMovedEvent, OnPlaylistPointerMoved);
        PlaylistListBox.RemoveHandler(PointerReleasedEvent, OnPlaylistPointerReleased);
    }

    private async void OnPlaylistPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null || _dragPressedArgs is null)
        {
            CancelDragCandidate();
            return;
        }

        var pos = e.GetPosition(PlaylistListBox);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.Y) < 8)
            return;

        var item = _dragCandidate;
        var pressedArgs = _dragPressedArgs;
        CancelDragCandidate();

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PlaylistItemFormat, item));
        await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (e.DataTransfer.Contains(PlaylistItemFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            return;
        }
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is not MediaPlayerViewModel vm)
            return;

        var draggedItem = e.DataTransfer.TryGetValue(PlaylistItemFormat);
        if (draggedItem is not null)
        {
            var targetItem = GetPlaylistItemAtPoint(e.GetPosition(PlaylistListBox));
            if (targetItem is not null && !ReferenceEquals(targetItem, draggedItem))
            {
                var targetIndex = vm.PlaylistItems.IndexOf(targetItem);
                vm.MovePlaylistItem(draggedItem, targetIndex);
            }
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || !files.Any())
            return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (paths.Count > 0)
            vm.AddDroppedFilesToPlaylist(paths);
    }

    private PlaylistItem? GetPlaylistItemAtPoint(Point point)
    {
        var hit = PlaylistListBox.InputHitTest(point);
        if (hit is not Visual visual)
            return null;

        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor is ListBoxItem lbi && lbi.DataContext is PlaylistItem item)
                return item;
        }
        return null;
    }

    private void OnPlayerNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.IsReadOnly = false;
            tb.SelectAll();
        }
    }

    private void OnPlayerNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.IsReadOnly = true;
    }

    private void OnPlayerNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && e.Key is Key.Return or Key.Escape)
        {
            tb.IsReadOnly = true;
            e.Handled = true;
        }
    }

    private void OnSeekSliderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm || vm.Duration <= TimeSpan.Zero)
            return;

        var pos = e.GetPosition(SeekSlider);
        var width = SeekSlider.Bounds.Width;
        if (width <= 0) return;

        var ratio = Math.Clamp(pos.X / width, 0, 1);
        var hoverTime = TimeSpan.FromTicks((long)(vm.Duration.Ticks * ratio));
        var formatted = hoverTime.TotalHours >= 1
            ? hoverTime.ToString(@"hh\:mm\:ss")
            : hoverTime.ToString(@"mm\:ss");
        ToolTip.SetTip(SeekSlider, formatted);
    }

    private void OnVolumeSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MediaPlayerViewModel vm)
            vm.ResetVolume();
    }

    private void OnMiddleTimeTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MediaPlayerViewModel vm)
            vm.ToggleMiddleTimeDisplay();
    }

    private void OnPlaylistItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        SDebug.ChangeTrace.Begin("playlist double-tap");
        if (DataContext is not MediaPlayerViewModel vm)
        {
            SDebug.ChangeTrace.End("cancelled (no VM)");
            return;
        }

        if (sender is not ListBox lb || lb.SelectedItem is not PlaylistItem item)
        {
            SDebug.ChangeTrace.End("cancelled (no item)");
            return;
        }

        SDebug.ChangeTrace.Step("view prechecks done");
        _ = vm.PlayPlaylistItemAsync(item);
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

    private void OnUserControlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not MediaPlayerViewModel vm) return;

        if (e.Source is TextBox || e.Source is NumericUpDown)
            return;
        if (e.Source is Slider)
            return;
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
                if (vm.TogglePlayPauseCommand.CanExecute(null))
                {
                    vm.TogglePlayPauseCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemOpenBrackets:
                if (vm.PreviousTrackCommand.CanExecute(null))
                {
                    vm.PreviousTrackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemCloseBrackets:
                if (vm.NextTrackCommand.CanExecute(null))
                {
                    vm.NextTrackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemComma:
                if (vm.JogBackCommand.CanExecute(null))
                {
                    vm.JogBackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemPeriod:
                if (vm.JogForwardCommand.CanExecute(null))
                {
                    vm.JogForwardCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Home:
                if (vm.SeekToStartCommand.CanExecute(null))
                {
                    vm.SeekToStartCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            // `+` / `-` (and numpad) nudge master volume. Arrow keys are intentionally avoided so
            // they keep navigating the playlist list.
            case Key.OemPlus:
            case Key.Add:
                if (vm.VolumeUpCommand.CanExecute(null))
                {
                    vm.VolumeUpCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemMinus:
            case Key.Subtract:
                if (vm.VolumeDownCommand.CanExecute(null))
                {
                    vm.VolumeDownCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }
}
