using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels;

/// <summary>
/// One soundboard tab: a fixed Rows×Columns grid of <see cref="SoundboardTileViewModel"/> in
/// row-major order (every cell always has a tile VM - unbound ones render as blanks outside edit
/// mode so bound tiles never shift cells), plus the per-board defaults that pre-fill tiles when a
/// sound is bound.
/// </summary>
public sealed partial class SoundboardViewModel : ObservableObject
{
    public const int MaxRows = 12;
    public const int MaxColumns = 12;

    public SoundboardViewModel(string name = "Soundboard")
    {
        _name = name;
        EnsureGrid();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _rows = 4;

    [ObservableProperty]
    private int _columns = 6;

    // ----- Defaults applied when a sound is bound to a tile --------------------------------------

    [ObservableProperty]
    private Guid _defaultOutputLineId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultVolumePercent))]
    private double _defaultVolume = 1.0;

    [ObservableProperty]
    private int _defaultFadeOutMs;

    [ObservableProperty]
    private bool _defaultLoop;

    /// <summary>Workspace edit mode, pushed down so every tile (including ones created by grid
    /// resizes) renders its drop-target state without ancestor bindings.</summary>
    [ObservableProperty]
    private bool _isEditing;

    partial void OnIsEditingChanged(bool value)
    {
        foreach (var tile in Tiles)
            tile.IsEditing = value;
    }

    public double DefaultVolumePercent
    {
        get => Math.Round(DefaultVolume * 100.0);
        set => DefaultVolume = Math.Clamp(value, 0, 100) / 100.0;
    }

    /// <summary>Row-major (row 0 first). The grid view relies on this ordering.</summary>
    public ObservableCollection<SoundboardTileViewModel> Tiles { get; } = new();

    partial void OnRowsChanged(int value) => EnsureGrid();

    partial void OnColumnsChanged(int value) => EnsureGrid();

    /// <summary>Binds a file to a tile. A previously-unbound tile picks up the board defaults;
    /// re-binding keeps the tile's own settings (only the file and cached duration change).</summary>
    public void BindTile(SoundboardTileViewModel tile, string filePath)
    {
        if (!tile.IsBound)
        {
            tile.OutputLineId = Guid.Empty; // board default
            tile.Volume = DefaultVolume;
            tile.FadeOutMs = DefaultFadeOutMs;
            tile.Loop = DefaultLoop;
        }

        tile.FilePath = filePath;
        tile.DurationMs = 0; // until the probe lands
        tile.ResetPlaybackState();
    }

    public void ClearTile(SoundboardTileViewModel tile)
    {
        tile.FilePath = null;
        tile.Label = null;
        tile.DurationMs = 0;
        tile.OutputLineId = Guid.Empty;
        tile.Volume = DefaultVolume;
        tile.FadeOutMs = DefaultFadeOutMs;
        tile.Loop = DefaultLoop;
        tile.ResetPlaybackState();
    }

    /// <summary>Reconciles <see cref="Tiles"/> with the current grid size: keeps existing tiles in
    /// their cells, adds blanks for new cells, drops tiles that fall outside (shrinking is an edit-
    /// mode action, the operator sees the tiles disappear).</summary>
    private void EnsureGrid()
    {
        var rows = Math.Clamp(Rows, 1, MaxRows);
        var columns = Math.Clamp(Columns, 1, MaxColumns);

        var byCell = Tiles.ToDictionary(t => (t.Row, t.Column));
        Tiles.Clear();
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var tile = byCell.TryGetValue((row, column), out var existing)
                ? existing
                : new SoundboardTileViewModel(row, column) { IsEditing = IsEditing };
            tile.GridIndex = Tiles.Count + 1; // 1-based row-major - the remote API tile number
            Tiles.Add(tile);
        }
    }

    public SoundboardConfig ToConfig() => new()
    {
        Id = Id,
        Name = Name,
        Rows = Math.Clamp(Rows, 1, MaxRows),
        Columns = Math.Clamp(Columns, 1, MaxColumns),
        DefaultOutputLineId = DefaultOutputLineId,
        DefaultVolume = Math.Clamp(DefaultVolume, 0, 1),
        DefaultFadeOutMs = Math.Max(0, DefaultFadeOutMs),
        DefaultLoop = DefaultLoop,
        // Only bound tiles persist - blanks are derivable from the grid size.
        Tiles = Tiles.Where(t => t.IsBound).Select(t => t.ToConfig()).ToList(),
    };

    public static SoundboardViewModel FromConfig(SoundboardConfig config)
    {
        var board = new SoundboardViewModel(config.Name)
        {
            Id = config.Id,
            DefaultOutputLineId = config.DefaultOutputLineId,
            DefaultVolume = Math.Clamp(config.DefaultVolume, 0, 1),
            DefaultFadeOutMs = Math.Max(0, config.DefaultFadeOutMs),
            DefaultLoop = config.DefaultLoop,
            Rows = Math.Clamp(config.Rows, 1, MaxRows),
            Columns = Math.Clamp(config.Columns, 1, MaxColumns),
        };

        foreach (var tileConfig in config.Tiles)
        {
            var tile = SoundboardTileViewModel.FromConfig(tileConfig);
            if (tile.Row < board.Rows && tile.Column < board.Columns)
            {
                var index = tile.Row * board.Columns + tile.Column;
                tile.GridIndex = index + 1;
                board.Tiles[index] = tile;
            }
        }

        return board;
    }
}
