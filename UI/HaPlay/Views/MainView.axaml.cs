using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views.Dialogs;

namespace HaPlay.Views;

public partial class MainView : UserControl
{
    private readonly PopoutRegion _playersPopout = new();
    private readonly PopoutRegion _cuesPopout = new();

    public MainView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(PlayersQuickPlayHost, true);
        PlayersQuickPlayHost.AddHandler(DragDrop.DragOverEvent, OnPlayersQuickPlayDragOver, RoutingStrategies.Bubble);
        PlayersQuickPlayHost.AddHandler(DragDrop.DropEvent, OnPlayersQuickPlayDrop, RoutingStrategies.Bubble);
    }

    private void OnPopOutPlayersClick(object? sender, RoutedEventArgs e) =>
        _playersPopout.OpenOrActivate(PlayersPopoutHost, WorkspaceItem.Players.Label, TopLevel.GetTopLevel(this) as Window);

    private void OnPopOutCuesClick(object? sender, RoutedEventArgs e) =>
        _cuesPopout.OpenOrActivate(CuesPopoutHost, WorkspaceItem.Cues.Label, TopLevel.GetTopLevel(this) as Window);

    /// <summary>Toast body click = pin/unpin (stops the auto-dismiss); the ✕ button closes.</summary>
    private void OnToastBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ToastViewModel toast })
            toast.TogglePinCommand.Execute(null);
    }

    /// <summary>P5 deck grid: clicking anywhere in a deck makes it the selected player (quick-play
    /// drop target, cue routing default).</summary>
    private void OnDeckPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: MediaPlayerViewModel player } && DataContext is MainViewModel main)
            main.SelectedPlayer = player;
    }

    /// <summary>P5 focus mode: double-tap toggles a deck between grid cell and full workspace.
    /// Ignored when the gesture lands on an editing control (text boxes, sliders) so it can't fight
    /// inline editors like the player-name rename.</summary>
    private void OnDeckDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source &&
            (source is TextBox || source is Slider || source.FindAncestorOfType<TextBox>() is not null ||
             source.FindAncestorOfType<Slider>() is not null))
            return;
        if (DataContext is not MainViewModel main)
            return;

        if (sender is Control { DataContext: MediaPlayerViewModel player })
            main.ToggleFocusPlayerCommand.Execute(player);
        else
            main.ExitPlayerFocusCommand.Execute(null);
    }

    private void OnPlayersQuickPlayDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnPlayersQuickPlayDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is not MainViewModel main)
            return;

        var player = main.SelectedPlayer;
        if (player is null)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || !files.Any())
            return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (paths.Count == 0)
            return;

        _ = player.QuickPlayDroppedFilesAsync(paths);
    }
}
