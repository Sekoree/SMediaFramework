using System.Text.Json;
using Avalonia.Threading;
using HaPlay.ViewModels;

namespace HaPlay.Remote;

/// <summary>HTTP-status-shaped outcome of one remote command.</summary>
public readonly record struct RemoteApiResult(int Status, string Body, string? Allow = null)
{
    // JsonEncodedText (not JsonSerializer) — the app publishes NativeAOT with source-gen-only
    // serialization, and these tiny payloads don't justify a context type.
    public static RemoteApiResult Ok(string message) =>
        new(200, $"{{\"ok\":true,\"message\":\"{JsonEncodedText.Encode(message)}\"}}");

    public static RemoteApiResult Fail(int status, string error) =>
        new(status, $"{{\"ok\":false,\"error\":\"{JsonEncodedText.Encode(error)}\"}}");

    public static RemoteApiResult MethodNotAllowed(string allow) =>
        new(405, $"{{\"ok\":false,\"error\":\"Use {JsonEncodedText.Encode(allow)}.\"}}", allow);
}

/// <summary>
/// Routes remote API paths onto the view models. Transport-agnostic (the HTTP listener and unit
/// tests both call <see cref="ExecuteAsync"/>); every handler hops to the UI thread, validates its
/// target and *kicks off* the command without awaiting playback — transports can block for seconds
/// on prefill, and a remote controller needs the request to return immediately.
///
/// URL scheme (all indices 1-based, matching the UI labels; status is GET, mutations are POST):
///   /api/v1/status
///   /api/v1/cues/go|pause|resume|stop|panic
///   /api/v1/players/{player}/play|pause|toggle|stop|next|prev
///   /api/v1/players/{player}/volume?db=-10
///   /api/v1/players/{player}/hold[?on=true|false]
///   /api/v1/players/{player}/{playlist}/{item}[/play]
///   /api/v1/soundboards/stop
///   /api/v1/soundboards/{board}/{tile}[/tap|play|stop|fade]
///   /api/v1/control/arm|disarm
/// </summary>
public sealed class RemoteApiDispatcher
{
    private readonly CuePlayerViewModel _cuePlayer;
    private readonly Func<IReadOnlyList<MediaPlayerViewModel>> _players;
    private readonly SoundboardWorkspaceViewModel _soundboard;
    private readonly ControlWorkspaceViewModel? _control;

    public RemoteApiDispatcher(
        CuePlayerViewModel cuePlayer,
        Func<IReadOnlyList<MediaPlayerViewModel>> players,
        SoundboardWorkspaceViewModel soundboard,
        ControlWorkspaceViewModel? control)
    {
        _cuePlayer = cuePlayer;
        _players = players;
        _soundboard = soundboard;
        _control = control;
    }

    public async Task<RemoteApiResult> ExecuteAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken cancellationToken = default)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        if (index < segments.Length && segments[index].Equals("api", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index < segments.Length && segments[index].Equals("v1", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index >= segments.Length)
            return RemoteApiResult.Fail(404, "Unknown endpoint.");

        var domain = segments[index].ToLowerInvariant();
        var rest = segments[(index + 1)..];
        query ??= new Dictionary<string, string>();

        var allow = domain == "status" ? "GET" : "POST";
        if (!string.Equals(method, allow, StringComparison.OrdinalIgnoreCase))
            return RemoteApiResult.MethodNotAllowed(allow);

        cancellationToken.ThrowIfCancellationRequested();

        // VM access has UI-thread affinity; tests already run on the headless UI thread.
        if (Dispatcher.UIThread.CheckAccess())
            return Handle(domain, rest, query);
        return await Dispatcher.UIThread.InvokeAsync(
            () => Handle(domain, rest, query),
            DispatcherPriority.Normal,
            cancellationToken);
    }

    /// <summary>Value for HTTP <c>Allow</c>: the one legal application method plus OPTIONS.</summary>
    public static string AllowedMethodsFor(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        if (index < segments.Length && segments[index].Equals("api", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index < segments.Length && segments[index].Equals("v1", StringComparison.OrdinalIgnoreCase))
            index++;
        var domain = index < segments.Length ? segments[index] : null;
        // Only /api/v1/status is read-only; all command domains are mutations.
        return string.Equals(domain, "status", StringComparison.OrdinalIgnoreCase)
            ? "GET, OPTIONS"
            : "POST, OPTIONS";
    }

    private RemoteApiResult Handle(string domain, string[] rest, IReadOnlyDictionary<string, string> query) =>
        domain switch
        {
            "status" => HandleStatus(),
            "cues" => HandleCues(rest),
            "players" => HandlePlayers(rest, query),
            "soundboards" => HandleSoundboards(rest),
            "control" => HandleControl(rest),
            _ => RemoteApiResult.Fail(404, $"Unknown endpoint '{domain}'."),
        };

    private RemoteApiResult HandleStatus()
    {
        var boards = _soundboard.Boards.Count;
        var players = _players().Count;
        return new RemoteApiResult(200,
            $"{{\"ok\":true,\"app\":\"HaPlay\",\"players\":{players},\"soundboards\":{boards}}}");
    }

    private RemoteApiResult HandleCues(string[] rest)
    {
        if (rest.Length != 1)
            return RemoteApiResult.Fail(404, "Cue endpoint: /cues/go|pause|resume|stop|panic.");

        switch (rest[0].ToLowerInvariant())
        {
            case "go":
                if (!_cuePlayer.GoCommand.CanExecute(null))
                    return RemoteApiResult.Fail(409, "Nothing to fire (no fireable cues).");
                _ = _cuePlayer.GoCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("go");
            case "pause":
                if (_cuePlayer.CurrentCueNode is null)
                    return RemoteApiResult.Fail(409, "No active cue to pause.");
                if (!_cuePlayer.IsTransportPaused)
                    _cuePlayer.PauseCommand.Execute(null);
                return RemoteApiResult.Ok("paused");
            case "resume":
                if (_cuePlayer.CurrentCueNode is null)
                    return RemoteApiResult.Fail(409, "No active cue to resume.");
                if (_cuePlayer.IsTransportPaused)
                    _cuePlayer.PauseCommand.Execute(null);
                return RemoteApiResult.Ok("resumed");
            case "stop":
                _cuePlayer.StopCommand.Execute(null);
                return RemoteApiResult.Ok("stopped");
            case "panic":
                _cuePlayer.PanicCommand.Execute(null);
                return RemoteApiResult.Ok("panic");
            default:
                return RemoteApiResult.Fail(404, $"Unknown cue command '{rest[0]}'.");
        }
    }

    private RemoteApiResult HandlePlayers(string[] rest, IReadOnlyDictionary<string, string> query)
    {
        if (rest.Length == 0)
            return RemoteApiResult.Fail(404, "Player endpoint: /players/{player}/…");

        var players = _players();
        var player = ResolvePlayer(players, rest[0]);
        if (player is null)
            return RemoteApiResult.Fail(404, $"Unknown player '{rest[0]}' ({players.Count} available).");

        if (rest.Length == 1)
            return RemoteApiResult.Fail(404, "Missing player command.");

        // Numeric second segment = playlist addressing: /{player}/{playlist}/{item}[/play]
        if (int.TryParse(rest[1], out var playlistNumber))
            return HandlePlaylistItem(player, playlistNumber, rest);

        switch (rest[1].ToLowerInvariant())
        {
            case "play":
                _ = player.PlayCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("play");
            case "pause":
                _ = player.PauseCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("pause");
            case "toggle":
                _ = player.TogglePlayPauseCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("toggle");
            case "stop":
                _ = player.StopCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("stop");
            case "next":
                _ = player.NextTrackCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("next");
            case "prev" or "previous":
                _ = player.PreviousTrackCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("previous");
            case "volume":
                if (!query.TryGetValue("db", out var dbText)
                    || !double.TryParse(dbText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var db))
                    return RemoteApiResult.Fail(400, "volume requires ?db=<-60..12>.");
                player.MasterVolumeDb = Math.Clamp(db, -60.0, 12.0);
                return RemoteApiResult.Ok($"volume {player.MasterVolumeDb:0.#} dB");
            case "hold":
                player.HoldFallbackVideo = query.TryGetValue("on", out var onText)
                    ? IsTruthy(onText)
                    : !player.HoldFallbackVideo;
                return RemoteApiResult.Ok(player.HoldFallbackVideo ? "hold on" : "hold off");
            default:
                return RemoteApiResult.Fail(404, $"Unknown player command '{rest[1]}'.");
        }
    }

    private static RemoteApiResult HandlePlaylistItem(MediaPlayerViewModel player, int playlistNumber, string[] rest)
    {
        if (rest.Length < 3 || !int.TryParse(rest[2], out var itemNumber))
            return RemoteApiResult.Fail(404, "Playlist endpoint: /players/{player}/{playlist}/{item}[/play].");
        if (rest.Length > 4 || (rest.Length == 4 && !rest[3].Equals("play", StringComparison.OrdinalIgnoreCase)))
            return RemoteApiResult.Fail(404, $"Unknown playlist item command '{rest[^1]}'.");

        if (playlistNumber < 1 || playlistNumber > player.PlaylistTabs.Count)
            return RemoteApiResult.Fail(404, $"Playlist {playlistNumber} not found ({player.PlaylistTabs.Count} available).");
        var tab = player.PlaylistTabs[playlistNumber - 1];
        if (itemNumber < 1 || itemNumber > tab.Items.Count)
            return RemoteApiResult.Fail(404, $"Item {itemNumber} not found ({tab.Items.Count} in playlist).");

        var item = tab.Items[itemNumber - 1];
        player.SelectedPlaylistTab = tab;
        _ = player.PlayPlaylistItemAsync(item);
        return RemoteApiResult.Ok($"playing {item.DisplayName}");
    }

    private RemoteApiResult HandleSoundboards(string[] rest)
    {
        if (rest.Length == 1 && rest[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _ = _soundboard.StopAllCommand.ExecuteAsync(null);
            return RemoteApiResult.Ok("stopping all");
        }

        if (rest.Length is < 2 or > 3
            || !int.TryParse(rest[0], out var boardNumber)
            || !int.TryParse(rest[1], out var tileNumber))
            return RemoteApiResult.Fail(404, "Soundboard endpoint: /soundboards/{board}/{tile}[/tap|play|stop|fade] or /soundboards/stop.");

        if (boardNumber < 1 || boardNumber > _soundboard.Boards.Count)
            return RemoteApiResult.Fail(404, $"Soundboard {boardNumber} not found ({_soundboard.Boards.Count} available).");
        var board = _soundboard.Boards[boardNumber - 1];
        if (tileNumber < 1 || tileNumber > board.Tiles.Count)
            return RemoteApiResult.Fail(404, $"Tile {tileNumber} not found ({board.Tiles.Count} on board).");
        var tile = board.Tiles[tileNumber - 1];
        if (!tile.IsBound)
            return RemoteApiResult.Fail(409, $"Tile {tileNumber} has no sound bound.");

        var verb = rest.Length == 3 ? rest[2].ToLowerInvariant() : "tap";
        switch (verb)
        {
            case "tap":
                _ = _soundboard.TriggerTileAsync(tile);
                return RemoteApiResult.Ok($"tap {tile.DisplayName}");
            case "play":
                _ = _soundboard.PlayTileAsync(tile);
                return RemoteApiResult.Ok($"play {tile.DisplayName}");
            case "stop":
                if (_soundboard.StopSoundCallback is { } stop)
                    _ = stop(tile.Id);
                return RemoteApiResult.Ok($"stop {tile.DisplayName}");
            case "fade":
                if (_soundboard.FadeOutSoundCallback is { } fade)
                    _ = fade(tile.Id);
                return RemoteApiResult.Ok($"fade {tile.DisplayName}");
            default:
                return RemoteApiResult.Fail(404, $"Unknown tile command '{verb}'.");
        }
    }

    private RemoteApiResult HandleControl(string[] rest)
    {
        if (_control is null)
            return RemoteApiResult.Fail(503, "Control system not available.");
        if (rest.Length != 1)
            return RemoteApiResult.Fail(404, "Control endpoint: /control/arm|disarm.");

        switch (rest[0].ToLowerInvariant())
        {
            case "arm" or "enable":
                if (!_control.IsArmed)
                    _ = _control.ToggleArmCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("arming");
            case "disarm" or "disable":
                if (_control.IsArmed)
                    _ = _control.ToggleArmCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("disarming");
            default:
                return RemoteApiResult.Fail(404, $"Unknown control command '{rest[0]}'.");
        }
    }

    /// <summary>Players address by 1-based index or by name (case-insensitive).</summary>
    private static MediaPlayerViewModel? ResolvePlayer(IReadOnlyList<MediaPlayerViewModel> players, string key)
    {
        if (int.TryParse(key, out var number))
            return number >= 1 && number <= players.Count ? players[number - 1] : null;
        return players.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTruthy(string value) =>
        value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
