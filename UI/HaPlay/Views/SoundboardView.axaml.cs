using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Views;

/// <summary>
/// Soundboard workspace view. Code-behind owns the input plumbing the VM can't: tile taps
/// (touch/mouse), external file drag-drop onto tiles (edit mode), and the file picker.
/// </summary>
public partial class SoundboardView : UserControl
{
    public SoundboardView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(TileGrid, true);
        TileGrid.AddHandler(DragDrop.DragOverEvent, OnTileDragOver, RoutingStrategies.Bubble);
        TileGrid.AddHandler(DragDrop.DropEvent, OnTileDrop, RoutingStrategies.Bubble);
    }

    private SoundboardWorkspaceViewModel? Vm => DataContext as SoundboardWorkspaceViewModel;

    // A11Y-02: the tile is now a Button, so this fires on both pointer taps and keyboard activation
    // (Space/Enter) - the soundboard is fully operable from the keyboard.
    private void OnTileTapped(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not SoundboardTileViewModel tile)
            return;
        e.Handled = true;
        _ = vm.TapTileAsync(tile);
    }

    private void OnTileDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        // Binding sounds is an edit-mode action (plan: accidental drops during a show must not
        // rebind tiles), and the drop has to land on a tile cell.
        e.DragEffects = Vm is { IsEditMode: true }
                        && FindTileFromEvent(e) is not null
                        && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnTileDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (Vm is not { IsEditMode: true } vm || FindTileFromEvent(e) is not { } tile)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (paths.Count == 0)
            return;

        e.Handled = true;
        _ = BindDroppedFilesAsync(vm, tile, paths);
    }

    /// <summary>Multiple dropped files fill the target tile, then following free tiles row-major -
    /// "drop a folder of stingers on the grid" in one gesture.</summary>
    private static async Task BindDroppedFilesAsync(
        SoundboardWorkspaceViewModel vm,
        SoundboardTileViewModel target,
        IReadOnlyList<string> paths)
    {
        await vm.BindFileToTileAsync(target, paths[0]);
        if (paths.Count == 1 || vm.FindBoardOf(target) is not { } board)
            return;

        var startIndex = board.Tiles.IndexOf(target);
        var next = 1;
        for (var i = startIndex + 1; i < board.Tiles.Count && next < paths.Count; i++)
        {
            if (board.Tiles[i].IsBound)
                continue;
            await vm.BindFileToTileAsync(board.Tiles[i], paths[next++]);
        }
    }

    private static SoundboardTileViewModel? FindTileFromEvent(RoutedEventArgs e)
    {
        for (var control = e.Source as Control; control is not null; control = control.Parent as Control)
        {
            if (control.DataContext is SoundboardTileViewModel tile)
                return tile;
        }

        return null;
    }

    private async void OnCopyTileApiUrlClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Vm is not { } vm
            || (sender as Control)?.DataContext is not SoundboardTileViewModel tile
            || vm.FindBoardOf(tile) is not { } board)
            return;

        var boardNumber = vm.Boards.IndexOf(board) + 1;
        var url = Remote.RemoteApi.TileTapUrl(boardNumber, tile.GridIndex);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(url);
        ToastCenter.Info(Strings.Format(nameof(Strings.CopiedToClipboardToastFormat), url));
    }

    private async void OnBrowseTileFileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Vm is not { SelectedTile: { } tile } vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SoundboardPickFileDialogTitle,
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
            await vm.BindFileToTileAsync(tile, path);
    }
}
