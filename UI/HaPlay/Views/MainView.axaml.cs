using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(PlayersQuickPlayHost, true);
        PlayersQuickPlayHost.AddHandler(DragDrop.DragOverEvent, OnPlayersQuickPlayDragOver, RoutingStrategies.Bubble);
        PlayersQuickPlayHost.AddHandler(DragDrop.DropEvent, OnPlayersQuickPlayDrop, RoutingStrategies.Bubble);
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
