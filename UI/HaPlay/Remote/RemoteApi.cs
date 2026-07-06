namespace HaPlay.Remote;

/// <summary>
/// Process-wide pointer to the remote API base URL (e.g. <c>http://192.168.1.20:8990</c>).
/// Maintained by <c>MainViewModel.RestartRestApi</c>; views read it to build Copy-API-URL strings.
/// It stays populated (configured port, best-known host) even while the listener is disabled so a
/// copied URL becomes live the moment the operator enables the API.
/// </summary>
/// <summary>One row of the endpoint cheat-sheet on the Project workspace card.</summary>
public sealed record RemoteApiEndpointDoc(string Path, string Description);

public static class RemoteApi
{
    public static string? BaseUrl { get; set; }

    // API-01: copied URLs never embed the access token. When a token is configured the operator sends it as an
    // `X-HaPlay-Api-Key` (or `Authorization: Bearer`) header, so the long-lived secret is not written to the
    // clipboard, a shell history, or a shared automation config's URL field. The server still ACCEPTS `?key=`
    // for manual/browser use (see RestApiServer.IsAuthorized) — it is simply never generated here.
    public static string TileTapUrl(int boardNumber, int tileNumber) =>
        $"{BaseUrl}/api/v1/soundboards/{boardNumber}/{tileNumber}/tap";

    public static string PlaylistItemPlayUrl(int playerNumber, int playlistNumber, int itemNumber) =>
        $"{BaseUrl}/api/v1/players/{playerNumber}/{playlistNumber}/{itemNumber}/play";

    public static string CueGoUrl() => $"{BaseUrl}/api/v1/cues/go";
}
