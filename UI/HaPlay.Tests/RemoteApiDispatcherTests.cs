using Avalonia.Headless;
using System.Diagnostics;
using HaPlay.Models;
using HaPlay.Remote;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Remote API routing: 1-based addressing, verb dispatch, and failure codes — all without
/// HTTP (the listener is a thin shell over <see cref="RemoteApiDispatcher.ExecuteAsync"/>).</summary>
public sealed class RemoteApiDispatcherTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static (RemoteApiDispatcher Dispatcher, CuePlayerViewModel Cues, SoundboardWorkspaceViewModel Soundboard,
        List<MediaPlayerViewModel> Players) CreateDispatcher()
    {
        var cues = new CuePlayerViewModel();
        var soundboard = new SoundboardWorkspaceViewModel();
        var players = new List<MediaPlayerViewModel>
        {
            new(new OutputManagementViewModel(), "Player 1"),
        };
        return (new RemoteApiDispatcher(cues, () => players, soundboard, control: null), cues, soundboard, players);
    }

    private static RemoteApiResult Execute(RemoteApiDispatcher dispatcher, string path, string method = "POST",
        Dictionary<string, string>? query = null) =>
        dispatcher.ExecuteAsync(method, path, query).GetAwaiter().GetResult();

    [Fact]
    public void UnknownEndpoint_Returns404_AndBadMethod405()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(404, Execute(dispatcher, "/api/v1/nope").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/cues/launch").Status);
            Assert.Equal(405, Execute(dispatcher, "/api/v1/cues/go", method: "DELETE").Status);
            var getMutation = Execute(dispatcher, "/api/v1/cues/go", method: "GET");
            Assert.Equal(405, getMutation.Status);
            Assert.Equal("POST", getMutation.Allow);
            Assert.Equal("GET, OPTIONS", RemoteApiDispatcher.AllowedMethodsFor("/api/v1/status/detail"));
            Assert.Equal("POST, OPTIONS", RemoteApiDispatcher.AllowedMethodsFor("/api/v1/cues/go"));
        });
    }

    [Fact]
    public void Status_ReportsCounts()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            var result = Execute(dispatcher, "/api/v1/status", method: "GET");

            Assert.Equal(200, result.Status);
            Assert.Contains("\"players\":1", result.Body);
            Assert.Contains("\"soundboards\":1", result.Body);
        });
    }

    [Fact]
    public void CuesGo_FiresGoCommand_And409WhenNothingFireable()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();

            // Empty cue list: nothing fireable.
            Assert.Equal(409, Execute(dispatcher, "/api/v1/cues/go").Status);

            cues.AddEmptyMediaCue(); // selected media cue makes Go available
            Assert.Equal(200, Execute(dispatcher, "/api/v1/cues/go").Status);
        });
    }

    [Fact]
    public void CuesPause_RequiresActiveCue()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(409, Execute(dispatcher, "/api/v1/cues/pause").Status);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/cues/stop").Status); // stop is always legal
        });
    }

    [Fact]
    public void PlayerVolume_SetsClampedMasterVolume()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();

            var result = Execute(dispatcher, "/api/v1/players/1/volume",
                query: new Dictionary<string, string> { ["db"] = "-12.5" });
            Assert.Equal(200, result.Status);
            Assert.Equal(-12.5, players[0].MasterVolumeDb);

            Execute(dispatcher, "/api/v1/players/1/volume",
                query: new Dictionary<string, string> { ["db"] = "99" });
            Assert.Equal(12.0, players[0].MasterVolumeDb); // clamped

            Assert.Equal(400, Execute(dispatcher, "/api/v1/players/1/volume").Status); // missing param
        });
    }

    [Fact]
    public void PlayerHold_TogglesAndSetsExplicitly()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();

            Execute(dispatcher, "/api/v1/players/1/hold");
            Assert.True(players[0].HoldFallbackVideo); // toggle from off

            Execute(dispatcher, "/api/v1/players/1/hold",
                query: new Dictionary<string, string> { ["on"] = "false" });
            Assert.False(players[0].HoldFallbackVideo);
        });
    }

    [Fact]
    public void PlayerAddressing_ByIndexAndName_Unknown404()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(200, Execute(dispatcher, "/api/v1/players/Player 1/hold").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/2/play").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/nope/play").Status);
        });
    }

    [Fact]
    public void PlaylistItem_Plays_SelectsTab_AndValidatesIndices()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();
            var player = players[0];
            var secondTab = new PlaylistTabViewModel("Set B");
            secondTab.Items.Add(new FilePlaylistItem("/tmp/a.wav"));
            secondTab.Items.Add(new FilePlaylistItem("/tmp/b.wav"));
            player.PlaylistTabs.Add(secondTab);

            // Out of range item / playlist.
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/2/3").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/9/1").Status);

            // Valid: bare form (no verb) plays and selects the tab.
            var result = Execute(dispatcher, "/api/v1/players/1/2/2");
            Assert.Equal(200, result.Status);
            Assert.Same(secondTab, player.SelectedPlaylistTab);
            Assert.Contains("b", result.Body);

            // Explicit /play verb also accepted; unknown verbs are not.
            Assert.Equal(200, Execute(dispatcher, "/api/v1/players/1/2/1/play").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/2/1/eject").Status);
        });
    }

    [Fact]
    public void SoundboardTile_TapStopFade_RouteToCallbacks()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, soundboard, _) = CreateDispatcher();
            var board = soundboard.Boards[0];
            var tile = board.Tiles[2]; // tile number 3
            board.BindTile(tile, "/tmp/sting.wav");
            var played = new List<Guid>();
            var stopped = new List<Guid>();
            var faded = new List<Guid>();
            soundboard.PlaySoundCallback = r => { played.Add(r.TileId); return Task.FromResult<string?>(null); };
            soundboard.StopSoundCallback = id => { stopped.Add(id); return Task.CompletedTask; };
            soundboard.FadeOutSoundCallback = id => { faded.Add(id); return Task.CompletedTask; };

            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3").Status); // bare = tap
            Assert.Equal([tile.Id], played);

            soundboard.OnSoundStarted(tile.Id);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/play").Status); // force restart
            Assert.Equal(2, played.Count);

            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/fade").Status);
            Assert.Equal([tile.Id], faded);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/stop").Status);
            Assert.Equal([tile.Id], stopped);

            // Edit mode must not turn a remote trigger into a selection.
            soundboard.IsEditMode = true;
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3").Status);
            Assert.Equal(3, played.Count);
            Assert.Null(soundboard.SelectedTile);

            // Unbound tile → 409; bad indices → 404.
            Assert.Equal(409, Execute(dispatcher, "/api/v1/soundboards/1/1").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/soundboards/9/1").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/soundboards/1/999").Status);
        });
    }

    [Fact]
    public async Task HttpServer_RoundTrips_StatusCommandAndNotFound()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        var (unauthorizedCode, statusCode, statusBody, tapCode, notFoundCode, getMutationCode, getMutationAllow) =
            await session.Dispatch(async () =>
        {
            var cues = new CuePlayerViewModel();
            var soundboard = new SoundboardWorkspaceViewModel();
            var played = 0;
            soundboard.PlaySoundCallback = _ => { played++; return Task.FromResult<string?>(null); };
            soundboard.Boards[0].BindTile(soundboard.Boards[0].Tiles[0], "/tmp/x.wav");
            var dispatcher = new RemoteApiDispatcher(cues, () => [], soundboard, null);

            using var server = new RestApiServer();
            var port = GetFreePort();
            const string token = "test-token";
            Assert.True(server.Start(port, dispatcher, token));
            var baseUrl = server.BaseUrl!;

            using var http = new HttpClient();
            var unauthorized = await http.GetAsync($"{baseUrl}/api/v1/status");
            var status = await http.GetAsync($"{baseUrl}/api/v1/status?key={token}");
            var body = await status.Content.ReadAsStringAsync();
            using var bearer = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/soundboards/1/1/tap");
            bearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var tap = await http.SendAsync(bearer);
            using var notFoundRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/bogus?key={token}");
            var notFound = await http.SendAsync(notFoundRequest);
            var getMutation = await http.GetAsync($"{baseUrl}/api/v1/soundboards/1/1/tap?key={token}");
            return ((int)unauthorized.StatusCode, (int)status.StatusCode, body, (int)tap.StatusCode,
                (int)notFound.StatusCode, (int)getMutation.StatusCode,
                getMutation.Content.Headers.Allow.SingleOrDefault());
        }, CancellationToken.None);

        Assert.Equal(401, unauthorizedCode);
        Assert.Equal(200, statusCode);
        Assert.Contains("\"ok\":true", statusBody);
        Assert.Equal(200, tapCode);
        Assert.Equal(404, notFoundCode);
        Assert.Equal(405, getMutationCode);
        Assert.Equal("POST", getMutationAllow);
    }

    [Fact]
    public async Task HttpServer_OptionalToken_OpenWhenUnset_RequiredWhenSet()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        var (noTokenStatus, tokenNoKeyStatus, tokenWithKeyStatus) = await session.Dispatch(async () =>
        {
            var dispatcher = new RemoteApiDispatcher(
                new CuePlayerViewModel(), () => [], new SoundboardWorkspaceViewModel(), null);
            using var http = new HttpClient();

            // No token configured → open (closed-LAN automation): status answers without a key.
            using (var open = new RestApiServer())
            {
                var port = GetFreePort();
                Assert.True(open.Start(port, dispatcher, accessToken: null));
                var r = await http.GetAsync($"{open.BaseUrl}/api/v1/status");
                var noToken = (int)r.StatusCode;

                // Token configured → required: unauthenticated 401, correct key 200.
                using var secured = new RestApiServer();
                var port2 = GetFreePort();
                Assert.True(secured.Start(port2, dispatcher, "secret-token"));
                var unauth = (int)(await http.GetAsync($"{secured.BaseUrl}/api/v1/status")).StatusCode;
                var auth = (int)(await http.GetAsync($"{secured.BaseUrl}/api/v1/status?key=secret-token")).StatusCode;
                return (noToken, unauth, auth);
            }
        }, CancellationToken.None);

        Assert.Equal(200, noTokenStatus);
        Assert.Equal(401, tokenNoKeyStatus);
        Assert.Equal(200, tokenWithKeyStatus);
    }

    [Fact]
    public async Task HttpServer_Stop_DoesNotBlockUiThreadWhenDispatchIsQueued()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Task<HttpResponseMessage>? request = null;

        var elapsed = await session.Dispatch(() =>
        {
            var dispatcher = new RemoteApiDispatcher(
                new CuePlayerViewModel(), () => [], new SoundboardWorkspaceViewModel(), null);
            using var server = new RestApiServer();
            var port = GetFreePort();
            Assert.True(server.Start(port, dispatcher, accessToken: null));

            // Keep the UI thread occupied long enough for the listener to queue its UI dispatch, then stop
            // from that same UI thread. A synchronous handler drain waits on itself here (the old 2 s stall).
            request = http.PostAsync($"{server.BaseUrl}/api/v1/cues/stop", content: null);
            Thread.Sleep(100);
            var started = Stopwatch.GetTimestamp();
            server.Stop();
            return Stopwatch.GetElapsedTime(started);
        }, CancellationToken.None);

        // Regression guard: the pre-fix bug blocked the UI thread ~2 s (a synchronous handler drain waiting on
        // itself). The fixed path returns near-instantly, so the bound only needs to sit below that 2 s stall.
        // 1500 ms tolerates GC/scheduling jitter on an overloaded shared CI runner (a real 679 ms sample was
        // seen when a normally-12 s assembly took ~7 min) while still catching the regression.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1500), $"Stop blocked the UI thread for {elapsed.TotalMilliseconds:0} ms");
        try
        {
            using var response = await request!.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Cancellation/connection close is the expected outcome; the assertion is that Stop returns promptly.
        }
    }

    [Fact]
    public void RemoteApi_CopyUrls_AreTokenless_ForHeaderAuth()
    {
        // API-01: the token is NEVER embedded in a copied URL (it would land in the clipboard, shell history, or
        // a shared controller config). A token-protected server expects the X-HaPlay-Api-Key header instead; the
        // server still accepts ?key= for manual use, but the app does not generate it.
        var previousBase = RemoteApi.BaseUrl;
        try
        {
            RemoteApi.BaseUrl = "http://localhost:8990";

            var url = RemoteApi.TileTapUrl(2, 4);

            Assert.Equal("http://localhost:8990/api/v1/soundboards/2/4/tap", url);
            Assert.DoesNotContain("key=", url);
            Assert.DoesNotContain("token=", url);
        }
        finally
        {
            RemoteApi.BaseUrl = previousBase;
        }
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void TileGridIndex_Is1BasedRowMajor_AndSurvivesResize()
    {
        DispatchUi(static () =>
        {
            var workspace = new SoundboardWorkspaceViewModel();
            var board = workspace.Boards[0];

            Assert.Equal(1, board.Tiles[0].GridIndex);
            Assert.Equal(board.Columns + 1, board.Tiles[board.Columns].GridIndex); // row 1, col 0

            board.Columns = 4;
            for (var i = 0; i < board.Tiles.Count; i++)
                Assert.Equal(i + 1, board.Tiles[i].GridIndex);
        });
    }
}
