using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>One entry in the tile/board output-line pickers.</summary>
public sealed record SoundboardOutputOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Soundboard workspace: multiple boards in tabs, an edit mode for binding/configuring tiles,
/// and the tap state machine (idle → play; playing → fade-out when configured, else stop; fading →
/// stop now). Engine access goes through callbacks so tests can run without audio hardware.
/// </summary>
public sealed partial class SoundboardWorkspaceViewModel : ObservableObject
{
    private readonly OutputManagementViewModel? _outputs;

    public SoundboardWorkspaceViewModel(OutputManagementViewModel? outputs = null)
    {
        _outputs = outputs;
        Boards.Add(new SoundboardViewModel(DefaultBoardName(1)));
        SelectedBoard = Boards[0];
        if (outputs is not null)
            outputs.Outputs.CollectionChanged += (_, _) => RefreshOutputOptions();
        RefreshOutputOptions();
    }

    public ObservableCollection<SoundboardViewModel> Boards { get; } = new();

    [ObservableProperty]
    private SoundboardViewModel? _selectedBoard;

    /// <summary>Edit mode: unbound tiles become visible drop targets, taps select instead of play,
    /// and the side panel exposes tile/board settings.</summary>
    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private SoundboardTileViewModel? _selectedTile;

    partial void OnIsEditModeChanged(bool value)
    {
        foreach (var board in Boards)
            board.IsEditing = value;
        if (!value)
            SelectedTile = null;
        else
            RefreshOutputOptions();
    }

    partial void OnSelectedBoardChanged(SoundboardViewModel? value) => SelectedTile = null;

    /// <summary>Live volume: while the edit panel's slider moves a PLAYING tile, push the change
    /// straight into the engine (it rewrites the routes' base gains, so a later fade still ramps
    /// from the new level). Idle tiles just keep the value for the next trigger.</summary>
    partial void OnSelectedTileChanged(SoundboardTileViewModel? oldValue, SoundboardTileViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedTilePropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedTilePropertyChanged;
    }

    private void OnSelectedTilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundboardTileViewModel.Volume)
            && sender is SoundboardTileViewModel { IsPlaying: true } tile)
        {
            SetSoundVolumeCallback?.Invoke(tile.Id, tile.Volume);
        }
    }

    // ----- Host (engine) callbacks — null in tests ----------------------------------------------

    public Func<SoundboardPlayRequest, Task<string?>>? PlaySoundCallback { get; set; }

    public Func<Guid, Task>? FadeOutSoundCallback { get; set; }

    public Func<Guid, Task>? StopSoundCallback { get; set; }

    public Func<Task>? StopAllSoundsCallback { get; set; }

    /// <summary>Engine live-volume hook (no-op there while the tile is fading or not playing).</summary>
    public Action<Guid, double>? SetSoundVolumeCallback { get; set; }

    /// <summary>Duration probe for newly bound files (CueMediaProbe in the app).</summary>
    public Func<string, Task<int?>>? ProbeDurationCallback { get; set; }

    // ----- Output line pickers --------------------------------------------------------------------

    /// <summary>Audio-capable lines; first entry is the "(board default)" placeholder for tiles.</summary>
    public ObservableCollection<SoundboardOutputOption> TileOutputOptions { get; } = new();

    /// <summary>Audio-capable lines for the board-default picker (no placeholder entry).</summary>
    public ObservableCollection<SoundboardOutputOption> BoardOutputOptions { get; } = new();

    public void RefreshOutputOptions()
    {
        var lines = _outputs?.Outputs
                        .Where(line => HaPlayPlaybackHelpers.IsAudioCapableOutput(line.Definition))
                        .Select(line => new SoundboardOutputOption(line.Definition.Id, line.Definition.DisplayName))
                        .ToList()
                    ?? [];

        TileOutputOptions.Clear();
        TileOutputOptions.Add(new SoundboardOutputOption(Guid.Empty, Strings.SoundboardBoardDefaultOutputLabel));
        BoardOutputOptions.Clear();
        foreach (var option in lines)
        {
            TileOutputOptions.Add(option);
            BoardOutputOptions.Add(option);
        }
    }

    // ----- Tap state machine ----------------------------------------------------------------------

    /// <summary>Single tap surface for grid tiles (touch or mouse).</summary>
    public Task TapTileAsync(SoundboardTileViewModel tile)
    {
        if (IsEditMode)
        {
            SelectedTile = tile;
            return Task.CompletedTask;
        }

        return TriggerTileAsync(tile);
    }

    /// <summary>The tap state machine without the edit-mode select branch — also the remote API
    /// entry point (a remote trigger means "play it", even while the operator is editing).</summary>
    public async Task TriggerTileAsync(SoundboardTileViewModel tile)
    {
        if (!tile.IsBound)
            return;

        if (tile.IsFading)
        {
            // Third tap: cut the running fade short.
            if (StopSoundCallback is { } stop)
                await stop(tile.Id).ConfigureAwait(false);
            return;
        }

        if (tile.IsPlaying)
        {
            if (tile.FadeOutMs > 0)
            {
                // Optimistic fade state so the countdown bar reacts on the tap rather than on the
                // first engine progress event (~100 ms later); the engine confirms/corrects it.
                tile.IsFading = true;
                tile.FadeRemainingMs = tile.FadeOutMs;
                if (FadeOutSoundCallback is { } fade)
                    await fade(tile.Id).ConfigureAwait(false);
            }
            else if (StopSoundCallback is { } stop)
            {
                await stop(tile.Id).ConfigureAwait(false);
            }

            return;
        }

        await PlayTileAsync(tile).ConfigureAwait(false);
    }

    /// <summary>Force-(re)starts a bound tile regardless of its current state (the remote API's
    /// explicit <c>/play</c>; a playing tile restarts from the top).</summary>
    public async Task PlayTileAsync(SoundboardTileViewModel tile)
    {
        if (!tile.IsBound || PlaySoundCallback is not { } play)
            return;

        var board = FindBoardOf(tile) ?? SelectedBoard;
        var outputLineId = tile.OutputLineId != Guid.Empty
            ? tile.OutputLineId
            : board?.DefaultOutputLineId ?? Guid.Empty;

        var error = await play(new SoundboardPlayRequest(
            tile.Id,
            tile.FilePath!,
            outputLineId,
            tile.Volume,
            tile.Loop,
            tile.FadeOutMs)).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(error))
            ToastCenter.Warn(error);
    }

    /// <summary>Binds a dropped/picked file to a tile (board defaults pre-fill an unbound tile)
    /// and probes its duration in the background.</summary>
    public async Task BindFileToTileAsync(SoundboardTileViewModel tile, string filePath)
    {
        var board = FindBoardOf(tile);
        if (board is null)
            return;

        board.BindTile(tile, filePath);
        if (IsEditMode)
            SelectedTile = tile;

        if (ProbeDurationCallback is { } probe && await probe(filePath).ConfigureAwait(false) is { } durationMs)
            tile.DurationMs = durationMs;
    }

    public SoundboardTileViewModel? FindTile(Guid tileId)
    {
        foreach (var board in Boards)
        foreach (var tile in board.Tiles)
        {
            if (tile.Id == tileId)
                return tile;
        }

        return null;
    }

    public SoundboardViewModel? FindBoardOf(SoundboardTileViewModel tile) =>
        Boards.FirstOrDefault(board => board.Tiles.Contains(tile));

    // ----- Engine event sinks (engine raises these on the UI thread) ------------------------------

    public void OnSoundStarted(Guid tileId)
    {
        if (FindTile(tileId) is not { } tile)
            return;
        tile.PositionMs = 0;
        tile.IsFading = false;
        tile.FadeRemainingMs = 0;
        tile.IsPlaying = true;
    }

    public void OnSoundProgress(SoundboardSoundProgress progress)
    {
        if (FindTile(progress.TileId) is not { } tile || !tile.IsPlaying)
            return;

        tile.PositionMs = (long)progress.Position.TotalMilliseconds;
        if (progress.Duration > TimeSpan.Zero)
            tile.DurationMs = (long)progress.Duration.TotalMilliseconds;
        if (progress.FadeRemaining is { } fadeRemaining)
        {
            tile.IsFading = true;
            tile.FadeRemainingMs = (long)fadeRemaining.TotalMilliseconds;
        }
    }

    public void OnSoundEnded(Guid tileId) => FindTile(tileId)?.ResetPlaybackState();

    // ----- Edit commands ----------------------------------------------------------------------------

    [RelayCommand]
    private void AddBoard()
    {
        var board = new SoundboardViewModel(DefaultBoardName(Boards.Count + 1)) { IsEditing = IsEditMode };
        Boards.Add(board);
        SelectedBoard = board;
    }

    /// <summary>Removes the selected board (the last board only resets instead — the workspace
    /// always shows at least one grid).</summary>
    [RelayCommand]
    private void RemoveSelectedBoard()
    {
        if (SelectedBoard is not { } board)
            return;

        var index = Boards.IndexOf(board);
        Boards.Remove(board);
        if (Boards.Count == 0)
            Boards.Add(new SoundboardViewModel(DefaultBoardName(1)));
        SelectedBoard = Boards[Math.Clamp(index, 0, Boards.Count - 1)];
    }

    [RelayCommand]
    private void ClearSelectedTile()
    {
        if (SelectedTile is { } tile)
            FindBoardOf(tile)?.ClearTile(tile);
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (StopAllSoundsCallback is { } stopAll)
            await stopAll().ConfigureAwait(false);
    }

    private static string DefaultBoardName(int number) =>
        Strings.Format(nameof(Strings.SoundboardDefaultBoardNameFormat), number);

    // ----- Standalone soundboard files (.haplayboards) ----------------------------------------------

    /// <summary>Saves just the selected board to a soundboards file.</summary>
    [RelayCommand]
    private Task SaveSelectedBoardAsAsync() =>
        SelectedBoard is { } board
            ? SaveBoardsAsAsync([board.ToConfig()], board.Name)
            : Task.CompletedTask;

    /// <summary>Saves every board (the whole tab collection) to one soundboards file.</summary>
    [RelayCommand]
    private Task SaveAllBoardsAsAsync() =>
        SaveBoardsAsAsync(BuildSnapshot(), Strings.SoundboardsCollectionFileNameFallback);

    private async Task SaveBoardsAsAsync(IReadOnlyList<SoundboardConfig> boards, string suggestedName)
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var picked = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SoundboardSaveDialogTitle,
            DefaultExtension = SoundboardsIO.FileExtension,
            SuggestedFileName = $"{SanitizeFileName(suggestedName)}.{SoundboardsIO.FileExtension}",
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlaySoundboardsFileTypeLabel)
                {
                    Patterns = ["*." + SoundboardsIO.FileExtension],
                },
            ],
        });
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await SoundboardsIO.SaveAsync(boards, path, "HaPlay");
            ToastCenter.Info(Strings.Format(nameof(Strings.SoundboardSavedToastFormat), Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            ToastCenter.Warn(Strings.Format(nameof(Strings.SoundboardSaveFailedToastFormat), ex.Message));
        }
    }

    /// <summary>Loads a soundboards file and appends its boards as new tabs.</summary>
    [RelayCommand]
    private async Task ImportBoardsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var picks = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SoundboardImportDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlaySoundboardsFileTypeLabel)
                {
                    Patterns = ["*." + SoundboardsIO.FileExtension],
                },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        });
        var path = picks.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            var boards = await SoundboardsIO.LoadAsync(path);
            ImportBoards(boards);
            ToastCenter.Info(Strings.Format(
                nameof(Strings.SoundboardImportedToastFormat), boards.Count, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            ToastCenter.Warn(Strings.Format(nameof(Strings.SoundboardImportFailedToastFormat), ex.Message));
        }
    }

    /// <summary>Appends imported boards as new tabs. Board and tile ids are regenerated — tile ids
    /// key active engine sounds and the Now-Playing lookups, so re-importing a file that's already
    /// loaded must not alias the live tiles.</summary>
    public void ImportBoards(IReadOnlyList<SoundboardConfig> configs)
    {
        SoundboardViewModel? last = null;
        foreach (var config in configs)
        {
            var copy = config with
            {
                Id = Guid.NewGuid(),
                Tiles = config.Tiles.Select(tile => tile with { Id = Guid.NewGuid() }).ToList(),
            };
            var board = SoundboardViewModel.FromConfig(copy);
            board.IsEditing = IsEditMode;
            Boards.Add(board);
            last = board;
        }

        if (last is not null)
            SelectedBoard = last;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "soundboard";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "soundboard" : cleaned;
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow;
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime single
            && single.MainView is Window w)
            return w;
        return null;
    }

    // ----- Persistence ------------------------------------------------------------------------------

    public List<SoundboardConfig> BuildSnapshot() => Boards.Select(board => board.ToConfig()).ToList();

    public void ApplySnapshot(IReadOnlyList<SoundboardConfig> configs)
    {
        SelectedTile = null;
        Boards.Clear();
        foreach (var config in configs)
            Boards.Add(SoundboardViewModel.FromConfig(config));
        if (Boards.Count == 0)
            Boards.Add(new SoundboardViewModel(DefaultBoardName(1)));
        foreach (var board in Boards)
            board.IsEditing = IsEditMode;
        SelectedBoard = Boards[0];
    }
}
