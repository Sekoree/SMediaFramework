using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views.Dialogs;

namespace HaPlay.Views;

public partial class MainView : UserControl
{
    private readonly PopoutRegion _playersPopout = new();
    private readonly PopoutRegion _cuesPopout = new();
    private readonly PopoutRegion _soundboardPopout = new();

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

    private void OnPopOutSoundboardClick(object? sender, RoutedEventArgs e) =>
        _soundboardPopout.OpenOrActivate(SoundboardPopoutHost, WorkspaceItem.Soundboard.Label, TopLevel.GetTopLevel(this) as Window);

    /// <summary>Toast body click = pin/unpin (stops the auto-dismiss); the ✕ button closes.</summary>
    private void OnToastBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ToastViewModel toast })
            toast.TogglePinCommand.Execute(null);
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
