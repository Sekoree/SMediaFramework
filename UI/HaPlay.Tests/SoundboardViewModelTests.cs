using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Soundboard workspace: tap state machine, board defaults, edit-mode visibility,
/// grid reconciliation and persistence round-trip.</summary>
public sealed class SoundboardViewModelTests
{
    private static (SoundboardWorkspaceViewModel Vm, SoundboardViewModel Board) CreateWorkspace()
    {
        var vm = new SoundboardWorkspaceViewModel();
        return (vm, vm.Boards[0]);
    }

    private static SoundboardTileViewModel BindTile(
        SoundboardWorkspaceViewModel vm,
        SoundboardViewModel board,
        int index = 0,
        string path = "/tmp/Stinger One.wav")
    {
        var tile = board.Tiles[index];
        board.BindTile(tile, path);
        return tile;
    }

    [Fact]
    public void NewWorkspace_HasOneBoardWithFullGrid()
    {
        var (vm, board) = CreateWorkspace();

        Assert.Single(vm.Boards);
        Assert.Equal(board.Rows * board.Columns, board.Tiles.Count);
        Assert.All(board.Tiles, t => Assert.False(t.IsBound));
    }

    [Fact]
    public async Task Tap_OnIdleBoundTile_PlaysWithBoardDefaultOutput()
    {
        var (vm, board) = CreateWorkspace();
        board.DefaultOutputLineId = Guid.NewGuid();
        board.DefaultVolume = 0.5;
        board.DefaultFadeOutMs = 1500;
        board.DefaultLoop = true;
        var tile = BindTile(vm, board);

        SoundboardPlayRequest? request = null;
        vm.PlaySoundCallback = r =>
        {
            request = r;
            return Task.FromResult<string?>(null);
        };

        await vm.TapTileAsync(tile);

        Assert.NotNull(request);
        Assert.Equal(tile.Id, request.Value.TileId);
        Assert.Equal(board.DefaultOutputLineId, request.Value.OutputLineId);
        Assert.Equal(0.5, request.Value.Volume);
        Assert.Equal(1500, request.Value.FadeOutMs);
        Assert.True(request.Value.Loop);
    }

    [Fact]
    public async Task Tap_TileWithOwnOutput_OverridesBoardDefault()
    {
        var (vm, board) = CreateWorkspace();
        board.DefaultOutputLineId = Guid.NewGuid();
        var tile = BindTile(vm, board);
        tile.OutputLineId = Guid.NewGuid();

        SoundboardPlayRequest? request = null;
        vm.PlaySoundCallback = r =>
        {
            request = r;
            return Task.FromResult<string?>(null);
        };

        await vm.TapTileAsync(tile);

        Assert.Equal(tile.OutputLineId, request!.Value.OutputLineId);
    }

    [Fact]
    public async Task Tap_WhilePlayingWithFade_StartsFade_ThenSecondTapStopsNow()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        tile.FadeOutMs = 2000;
        var faded = new List<Guid>();
        var stopped = new List<Guid>();
        vm.FadeOutSoundCallback = id => { faded.Add(id); return Task.CompletedTask; };
        vm.StopSoundCallback = id => { stopped.Add(id); return Task.CompletedTask; };

        vm.OnSoundStarted(tile.Id);
        Assert.True(tile.IsPlaying);

        await vm.TapTileAsync(tile);
        Assert.Equal([tile.Id], faded);
        Assert.True(tile.IsFading); // optimistic countdown state on the tap itself
        Assert.Equal(tile.FadeOutMs, tile.FadeRemainingMs);

        await vm.TapTileAsync(tile); // tap during fade = stop now
        Assert.Equal([tile.Id], stopped);
    }

    /// <summary>The "tile with a fade-out never plays again" regression: a fade-out releases its
    /// engine voice WITHOUT a VoiceEnded event, so the host must reset the tile (the coordinator's
    /// voice-poll reconciliation / stop wrappers call OnSoundEnded). Once reset, a tap PLAYS again -
    /// before the fix the tile sat in the fading state forever and every tap tried to stop it.</summary>
    [Fact]
    public async Task FadedOutTile_AfterHostReset_PlaysAgain()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        tile.FadeOutMs = 500;
        var played = new List<Guid>();
        vm.PlaySoundCallback = req => { played.Add(req.TileId); return Task.FromResult<string?>(null); };
        vm.FadeOutSoundCallback = _ => Task.CompletedTask;

        vm.OnSoundStarted(tile.Id);
        await vm.TapTileAsync(tile); // second tap: starts the fade
        Assert.True(tile.IsFading);

        vm.OnSoundEnded(tile.Id); // the fade ramp released the voice - host reconciliation resets the tile
        Assert.False(tile.IsPlaying);
        Assert.False(tile.IsFading);

        await vm.TapTileAsync(tile); // next tap must PLAY, not try to stop a dead voice
        Assert.Equal([tile.Id], played);
    }

    [Fact]
    public async Task StopAll_ResetsEveryTilesPlaybackState()
    {
        var (vm, board) = CreateWorkspace();
        var a = BindTile(vm, board, index: 0);
        var b = BindTile(vm, board, index: 1, path: "/tmp/Stinger Two.wav");
        vm.OnSoundStarted(a.Id);
        vm.OnSoundStarted(b.Id);

        vm.OnAllSoundsEnded();

        Assert.False(a.IsPlaying);
        Assert.False(b.IsPlaying);
        await Task.CompletedTask;
    }

    /// <summary>The "board settings not retained" regression: RefreshOutputOptions used to Clear()
    /// the live ComboBox ItemsSources, which dropped the selection and wrote Guid.Empty back through
    /// the TwoWay SelectedValue binding - wiping the board default output on every edit-mode entry
    /// and output-list change. The merge keeps unchanged entries in place.</summary>
    [Fact]
    public void RefreshOutputOptions_DoesNotChurnUnchangedEntries()
    {
        var (vm, _) = CreateWorkspace();
        vm.RefreshOutputOptions();
        var tileBefore = vm.TileOutputOptions.ToList();
        var boardBefore = vm.BoardOutputOptions.ToList();

        var resets = 0;
        vm.BoardOutputOptions.CollectionChanged += (_, e) =>
        {
            if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                or System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                resets++;
        };

        vm.RefreshOutputOptions(); // unchanged output set - must not remove/reset anything

        Assert.Equal(0, resets);
        Assert.Equal(tileBefore, vm.TileOutputOptions);
        Assert.Equal(boardBefore, vm.BoardOutputOptions);
    }

    /// <summary>Board-level settings (grid size + tile defaults) must survive the project JSON
    /// round-trip even when the board has no bound tiles and was never saved to a .haplayboards file.</summary>
    [Fact]
    public void BoardSettings_RoundTrip_ThroughProjectJson()
    {
        var (vm, board) = CreateWorkspace();
        var defaultOutput = Guid.NewGuid();
        board.Name = "FX board";
        board.Rows = 3;
        board.Columns = 5;
        board.DefaultOutputLineId = defaultOutput;
        board.DefaultVolume = 0.42;
        board.DefaultFadeOutMs = 1250;
        board.DefaultLoop = true;

        var project = new HaPlayProject { Soundboards = { vm.BuildSnapshot()[0] } };
        var loadedProject = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var restored = new SoundboardWorkspaceViewModel();
        restored.ApplySnapshot(loadedProject.Soundboards);
        var loaded = Assert.Single(restored.Boards);
        Assert.Equal("FX board", loaded.Name);
        Assert.Equal(3, loaded.Rows);
        Assert.Equal(5, loaded.Columns);
        Assert.Equal(defaultOutput, loaded.DefaultOutputLineId);
        Assert.Equal(0.42, loaded.DefaultVolume, precision: 6);
        Assert.Equal(1250, loaded.DefaultFadeOutMs);
        Assert.True(loaded.DefaultLoop);
    }

    [Fact]
    public async Task Tap_WhilePlayingWithoutFade_StopsImmediately()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        var stopped = new List<Guid>();
        vm.StopSoundCallback = id => { stopped.Add(id); return Task.CompletedTask; };

        vm.OnSoundStarted(tile.Id);
        await vm.TapTileAsync(tile);

        Assert.Equal([tile.Id], stopped);
    }

    [Fact]
    public async Task Tap_InEditMode_SelectsInsteadOfPlaying()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        var played = false;
        vm.PlaySoundCallback = _ => { played = true; return Task.FromResult<string?>(null); };

        vm.IsEditMode = true;
        await vm.TapTileAsync(tile);

        Assert.False(played);
        Assert.Same(tile, vm.SelectedTile);
    }

    [Fact]
    public async Task Tap_UnboundTile_OutsideEditMode_IsNoOp()
    {
        var (vm, board) = CreateWorkspace();
        var played = false;
        vm.PlaySoundCallback = _ => { played = true; return Task.FromResult<string?>(null); };

        await vm.TapTileAsync(board.Tiles[0]);

        Assert.False(played);
        Assert.Null(vm.SelectedTile);
    }

    [Fact]
    public async Task BindFile_AppliesBoardDefaults_AndProbesDuration()
    {
        var (vm, board) = CreateWorkspace();
        board.DefaultVolume = 0.7;
        board.DefaultFadeOutMs = 800;
        board.DefaultLoop = true;
        vm.ProbeDurationCallback = _ => Task.FromResult<int?>(123_456);

        var tile = board.Tiles[0];
        await vm.BindFileToTileAsync(tile, "/tmp/Big Hit.flac");

        Assert.True(tile.IsBound);
        Assert.Equal("Big Hit", tile.DisplayName); // filename without extension
        Assert.Equal(0.7, tile.Volume);
        Assert.Equal(800, tile.FadeOutMs);
        Assert.True(tile.Loop);
        Assert.Equal(Guid.Empty, tile.OutputLineId); // board default
        Assert.Equal(123_456, tile.DurationMs);
    }

    [Fact]
    public async Task Rebind_KeepsTileSettings_ResetsDuration()
    {
        var (vm, board) = CreateWorkspace();
        vm.ProbeDurationCallback = _ => Task.FromResult<int?>(null);
        var tile = BindTile(vm, board);
        tile.Volume = 0.3;
        tile.FadeOutMs = 4000;
        tile.Loop = true;
        tile.DurationMs = 9999;

        await vm.BindFileToTileAsync(tile, "/tmp/other.wav");

        Assert.Equal(0.3, tile.Volume);
        Assert.Equal(4000, tile.FadeOutMs);
        Assert.True(tile.Loop);
        Assert.Equal(0, tile.DurationMs);
    }

    [Fact]
    public void EditMode_TogglesDropHints_OnEveryBoardIncludingNewOnes()
    {
        var (vm, board) = CreateWorkspace();
        var unbound = board.Tiles[0];

        Assert.False(unbound.ShowsDropHint); // hidden outside edit mode

        vm.IsEditMode = true;
        Assert.True(unbound.ShowsDropHint);

        vm.AddBoardCommand.Execute(null);
        Assert.All(vm.Boards[1].Tiles, t => Assert.True(t.ShowsDropHint));

        vm.IsEditMode = false;
        Assert.False(unbound.ShowsDropHint);
        Assert.All(vm.Boards[1].Tiles, t => Assert.False(t.ShowsDropHint));
        Assert.Null(vm.SelectedTile);
    }

    [Fact]
    public void GridResize_KeepsBoundTilesInTheirCells()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board, index: 1); // row 0, col 1

        board.Columns = 4; // shrink from 6
        Assert.Equal(board.Rows * 4, board.Tiles.Count);
        Assert.Same(tile, board.Tiles[1]); // still row 0 col 1

        board.Columns = 6; // grow back
        Assert.Same(tile, board.Tiles[1]);
        Assert.Equal(board.Rows * 6, board.Tiles.Count);
    }

    [Fact]
    public void RemoveSelectedBoard_AlwaysLeavesOneBoard()
    {
        var (vm, _) = CreateWorkspace();

        vm.RemoveSelectedBoardCommand.Execute(null);

        Assert.Single(vm.Boards);
        Assert.NotNull(vm.SelectedBoard);
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesBoardsTilesAndDefaults()
    {
        var (vm, board) = CreateWorkspace();
        board.Name = "FX";
        board.Rows = 3;
        board.Columns = 5;
        board.DefaultOutputLineId = Guid.NewGuid();
        board.DefaultVolume = 0.6;
        board.DefaultFadeOutMs = 1200;
        board.DefaultLoop = true;
        var tile = board.Tiles[2 * 5 + 3]; // row 2, col 3
        board.BindTile(tile, "/tmp/Walk On.mp3");
        tile.OutputLineId = Guid.NewGuid();
        tile.Volume = 0.4;
        tile.FadeOutMs = 900;
        tile.Loop = true;
        tile.DurationMs = 42_000;
        vm.AddBoardCommand.Execute(null);
        vm.Boards[1].Name = "Music";

        var snapshot = vm.BuildSnapshot();
        // Only bound tiles persist.
        Assert.Single(snapshot[0].Tiles);

        var restored = new SoundboardWorkspaceViewModel();
        restored.ApplySnapshot(snapshot);

        Assert.Equal(2, restored.Boards.Count);
        var restoredBoard = restored.Boards[0];
        Assert.Equal("FX", restoredBoard.Name);
        Assert.Equal(3, restoredBoard.Rows);
        Assert.Equal(5, restoredBoard.Columns);
        Assert.Equal(board.DefaultOutputLineId, restoredBoard.DefaultOutputLineId);
        Assert.Equal(0.6, restoredBoard.DefaultVolume);
        Assert.Equal(1200, restoredBoard.DefaultFadeOutMs);
        Assert.True(restoredBoard.DefaultLoop);
        Assert.Equal(15, restoredBoard.Tiles.Count);

        var restoredTile = restoredBoard.Tiles[2 * 5 + 3];
        Assert.Equal(tile.Id, restoredTile.Id);
        Assert.Equal("Walk On", restoredTile.DisplayName);
        Assert.Equal(tile.OutputLineId, restoredTile.OutputLineId);
        Assert.Equal(0.4, restoredTile.Volume);
        Assert.Equal(900, restoredTile.FadeOutMs);
        Assert.True(restoredTile.Loop);
        Assert.Equal(42_000, restoredTile.DurationMs);
        Assert.Equal("Music", restored.Boards[1].Name);
    }

    [Fact]
    public void ApplySnapshot_Empty_CreatesOneDefaultBoard()
    {
        var (vm, _) = CreateWorkspace();
        vm.AddBoardCommand.Execute(null);

        vm.ApplySnapshot(Array.Empty<SoundboardConfig>());

        Assert.Single(vm.Boards);
        Assert.NotNull(vm.SelectedBoard);
    }

    [Fact]
    public void ProgressEvents_DriveRemainingTimeAndFadeCountdown()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        tile.FadeOutMs = 2000;

        vm.OnSoundStarted(tile.Id);
        vm.OnSoundProgress(new SoundboardSoundProgress(
            tile.Id, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), null));

        Assert.Equal(10_000, tile.PositionMs);
        Assert.Equal(60_000, tile.DurationMs);
        Assert.Equal("-00:50", tile.TimeLabel); // remaining while playing
        Assert.False(tile.IsFading);

        vm.OnSoundProgress(new SoundboardSoundProgress(
            tile.Id, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500)));
        Assert.True(tile.IsFading);
        Assert.Equal(500, tile.FadeRemainingMs);
        Assert.Equal(25, tile.FadeProgressPercent); // 500/2000

        vm.OnSoundEnded(tile.Id);
        Assert.False(tile.IsPlaying);
        Assert.False(tile.IsFading);
        Assert.Equal(0, tile.PositionMs);
        Assert.Equal("01:00", tile.TimeLabel); // back to showing the clip length
    }

    [Fact]
    public void TimeLabel_ShowsClipLengthWhenIdle()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        tile.DurationMs = 95_000;

        Assert.Equal("01:35", tile.TimeLabel);
    }

    [Fact]
    public void DisplayName_PrefersAlias_FallsBackToFilename()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board, path: "/tmp/Stinger One.wav");

        Assert.Equal("Stinger One", tile.DisplayName);
        Assert.Equal("Stinger One.wav", tile.FileNameDisplay);

        tile.Label = "Goal Horn";
        Assert.Equal("Goal Horn", tile.DisplayName);
        Assert.Equal("Stinger One.wav", tile.FileNameDisplay); // file row stays truthful

        tile.Label = "   "; // blank alias = unset
        Assert.Equal("Stinger One", tile.DisplayName);
    }

    [Fact]
    public void Alias_RoundTripsThroughConfig_AndClearsWithTile()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        tile.Label = "Walk-in";

        var restored = SoundboardTileViewModel.FromConfig(tile.ToConfig());
        Assert.Equal("Walk-in", restored.Label);

        tile.Label = "  ";
        Assert.Null(tile.ToConfig().Label); // whitespace normalizes to null

        tile.Label = "Walk-in";
        board.ClearTile(tile);
        Assert.Null(tile.Label);
    }

    [Fact]
    public void VolumeChange_OnSelectedPlayingTile_PushesLiveVolume()
    {
        var (vm, board) = CreateWorkspace();
        var tile = BindTile(vm, board);
        var live = new List<(Guid TileId, double Volume)>();
        vm.SetSoundVolumeCallback = (id, volume) => live.Add((id, volume));

        vm.IsEditMode = true;
        vm.SelectedTile = tile;

        tile.Volume = 0.4; // idle - config only, no live push
        Assert.Empty(live);

        vm.OnSoundStarted(tile.Id);
        tile.Volume = 0.8;
        Assert.Equal([(tile.Id, 0.8)], live);

        tile.FadeOutMs = 100; // unrelated property - no push
        Assert.Single(live);

        vm.SelectedTile = null;
        tile.Volume = 0.2; // unsubscribed after deselect
        Assert.Single(live);
    }

    [Fact]
    public void ImportBoards_AppendsWithFreshIds_AndSelectsLastImported()
    {
        var (vm, board) = CreateWorkspace();
        BindTile(vm, board);
        vm.IsEditMode = true;

        var source = new SoundboardWorkspaceViewModel();
        var sourceBoard = source.Boards[0];
        sourceBoard.Name = "FX";
        var sourceTile = sourceBoard.Tiles[0];
        sourceBoard.BindTile(sourceTile, "/tmp/horn.wav");
        sourceTile.Label = "Horn";
        var exported = source.BuildSnapshot();

        vm.ImportBoards(exported);

        Assert.Equal(2, vm.Boards.Count);
        var imported = vm.Boards[1];
        Assert.Same(imported, vm.SelectedBoard);
        Assert.Equal("FX", imported.Name);
        Assert.NotEqual(sourceBoard.Id, imported.Id); // fresh ids - live tile lookups must not alias
        var importedTile = imported.Tiles[0];
        Assert.NotEqual(sourceTile.Id, importedTile.Id);
        Assert.Equal("Horn", importedTile.Label);
        Assert.Equal("/tmp/horn.wav", importedTile.FilePath);
        Assert.True(importedTile.IsEditing); // picks up the live edit mode
    }

    [Fact]
    public async Task SoundboardsFile_RoundTrips_SingleBoardAndCollection()
    {
        var (vm, board) = CreateWorkspace();
        board.Name = "Stage L";
        BindTile(vm, board, path: "/tmp/sting.wav").Label = "Sting";

        var path = Path.Combine(Path.GetTempPath(), $"sb_{Guid.NewGuid():N}.{SoundboardsIO.FileExtension}");
        try
        {
            await SoundboardsIO.SaveAsync([board.ToConfig()], path, "HaPlay.Tests");
            var loaded = await SoundboardsIO.LoadAsync(path);

            var loadedBoard = Assert.Single(loaded);
            Assert.Equal("Stage L", loadedBoard.Name);
            var tile = Assert.Single(loadedBoard.Tiles);
            Assert.Equal("Sting", tile.Label);
            Assert.Equal("/tmp/sting.wav", tile.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SoundboardsFile_RejectsNewerSchemaVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb_{Guid.NewGuid():N}.{SoundboardsIO.FileExtension}");
        try
        {
            await File.WriteAllTextAsync(path, """{"schemaVersion": 999, "soundboards": []}""");
            await Assert.ThrowsAsync<UnsupportedSoundboardsSchemaVersionException>(
                () => SoundboardsIO.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProjectJson_RoundTripsSoundboardSection()
    {
        var project = new HaPlayProject
        {
            Soundboards =
            [
                new SoundboardConfig
                {
                    Name = "FX",
                    Rows = 2,
                    Columns = 3,
                    DefaultOutputLineId = Guid.NewGuid(),
                    DefaultVolume = 0.8,
                    DefaultFadeOutMs = 1000,
                    DefaultLoop = true,
                    Tiles =
                    [
                        new SoundboardTileConfig
                        {
                            Row = 1,
                            Column = 2,
                            FilePath = "/tmp/hit.wav",
                            OutputLineId = Guid.NewGuid(),
                            Volume = 0.5,
                            FadeOutMs = 750,
                            Loop = true,
                            DurationMs = 1234,
                        },
                    ],
                },
            ],
        };

        var loaded = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var board = Assert.Single(loaded.Soundboards);
        Assert.Equal("FX", board.Name);
        Assert.Equal(project.Soundboards[0].DefaultOutputLineId, board.DefaultOutputLineId);
        Assert.True(board.DefaultLoop);
        var tile = Assert.Single(board.Tiles);
        Assert.Equal("/tmp/hit.wav", tile.FilePath);
        Assert.Equal(project.Soundboards[0].Tiles[0].OutputLineId, tile.OutputLineId);
        Assert.Equal(0.5, tile.Volume);
        Assert.Equal(750, tile.FadeOutMs);
        Assert.True(tile.Loop);
        Assert.Equal(1234, tile.DurationMs);
    }
}
