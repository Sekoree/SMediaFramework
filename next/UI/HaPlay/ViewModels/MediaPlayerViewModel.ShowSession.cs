using Avalonia.Threading;
using HaPlay.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Session;

namespace HaPlay.ViewModels;

/// <summary>
/// 8a.4 convergence (gated by <c>HAPLAY_USE_SHOWSESSION=1</c>): re-backs the media-player deck's FILE playback
/// onto a per-player headless <see cref="ShowSession"/> (via <see cref="MediaPlayerShowMapper"/>) instead of
/// <c>HaPlayPlaybackSession</c>. Off by default → the engine path is untouched. File-core slice: play / pause /
/// resume / stop / seek + position readout; live inputs, logo/hold, and advanced state (fault recovery,
/// end-of-track auto-advance) stay on the engine for now. The transport methods early-return into here only
/// while <see cref="ShowSessionActive"/>.
/// </summary>
public partial class MediaPlayerViewModel
{
    private static readonly ILogger ShowLog = MediaDiagnostics.CreateLogger("HaPlay.MediaPlayer.ShowSession");

    private ShowSession? _playerShowSession;
    private Dictionary<string, IVideoOutput[]> _playerVideoOutputs = new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredLines = new();
    private DispatcherTimer? _playerShowPoll;

    /// <summary>True while a file is playing through the per-player ShowSession (the transport guards divert here).</summary>
    public bool ShowSessionActive { get; private set; }

    private static bool UseShowSessionPlayer =>
        Environment.GetEnvironmentVariable("HAPLAY_USE_SHOWSESSION") == "1";

    /// <summary>Gated file open: builds/loads the 1-cue player show and fires it. Returns false (and leaves the
    /// engine path to run) when disabled or on any failure.</summary>
    private async Task<bool> TryOpenViaShowSessionAsync(PlaylistItem item, IReadOnlyList<OutputLineViewModel> lines)
    {
        if (!UseShowSessionPlayer)
            return false;

        // Resolve the registry URI + whether there's a video composition. Files probe for a video stream; an NDI
        // deck source maps to ndi://<name> (the registry's NDIModule opens it live). Other kinds stay on the engine.
        string mediaPath;
        bool hasVideo;
        switch (item)
        {
            case FilePlaylistItem file:
                mediaPath = file.Path;
                var probe = await CueMediaProbe.TryProbeAsync(file.Path).ConfigureAwait(true);
                hasVideo = probe?.HasVideo == true;
                break;
            case NDIInputPlaylistItem ndi when RuntimeModules.IsNdiAvailable:
                mediaPath = $"ndi://{Uri.EscapeDataString(ndi.SourceName)}";
                hasVideo = !ndi.AudioOnly;
                break;
            default:
                return false;
        }

        try
        {
            _playerShowSession ??= new ShowSession(
                MediaRuntime.Registry,
                MediaRuntime.Registry.AudioBackends.FirstOrDefault(),
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex),
                // Borrowed lines: the deck owns each output's lifetime (acquire/release via _playerAcquiredLines),
                // so the leases declare DisposeOutputOnRuntimeDispose=false — the session never disposes them (NXT-01).
                (compId, name, _, _) => _playerVideoOutputs.TryGetValue(compId, out var outs)
                    ? outs.Select((o, i) => new ClipCompositionOutputLease(
                        $"{compId}_out{i}", name, o, DisposeOutputOnRuntimeDispose: false)).ToArray()
                    : Array.Empty<ClipCompositionOutputLease>());

            // UI thread: drop any idle logo, release the prior single-holder leases, then (re)acquire the video
            // lines for this source. Acquire realizes the SDL window / NDI sender, so it must run on the UI thread;
            // doing it before LoadDocument keeps the video factory a pure lookup during the (synchronous) load.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                foreach (var held in _playerAcquiredLines)
                    _outputs.ReleaseVideoOutputForLine(held);
                _playerAcquiredLines.Clear();

                var outputs = new List<IVideoOutput>();
                if (hasVideo)
                {
                    foreach (var line in lines)
                    {
                        if (_outputs.AcquireVideoOutputForLine(line.Definition.Id) is not { } o)
                            continue;
                        outputs.Add(o);
                        _playerAcquiredLines.Add(line.Definition.Id);
                    }
                }
                _playerVideoOutputs = new Dictionary<string, IVideoOutput[]>(StringComparer.Ordinal)
                {
                    [MediaPlayerShowMapper.PlayerCompositionId] = outputs.ToArray(),
                };
            });

            _playerShowSession.LoadDocument(MediaPlayerShowMapper.ToShowDocument(mediaPath, hasVideo));
            await _playerShowSession.FireCueAsync(MediaPlayerShowMapper.PlayerCueId).ConfigureAwait(true);

            // UI thread: flip the deck into the playing state (observable-property writes).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowSessionActive = true;
                MediaFilePath = (item as FilePlaylistItem)?.Path;
                IsMediaLoaded = true;
                LoadState = PlayerLoadState.Ready;
                IsPlaying = true;
                StartShowSessionPoll();
            });
            ShowLog.LogInformation("MediaPlayer: re-backed onto per-player ShowSession (HAPLAY_USE_SHOWSESSION=1).");
            return true;
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession open failed; falling back to the engine path");
            await ShowSessionStopAsync().ConfigureAwait(true);
            return false;
        }
    }

    private Task ShowSessionPauseAsync(bool paused)
    {
        IsPlaying = !paused;
        return _playerShowSession?.SetPausedAsync(paused) ?? Task.CompletedTask;
    }

    private Task ShowSessionSeekAsync(TimeSpan position) =>
        _playerShowSession?.SeekAsync(position) ?? Task.CompletedTask;

    /// <summary>Stops the player ShowSession, releases its video leases, and returns the deck to idle.</summary>
    private async Task ShowSessionStopAsync()
    {
        if (_playerShowSession is not null)
        {
            try { await _playerShowSession.StopAsync().ConfigureAwait(true); }
            catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession stop"); }
        }
        // UI thread: stop the poll, release the video leases, reset deck state, then hand the outputs to the
        // idle logo slate (FallbackImagePath) — the same idle fallback the engine path shows when it stops.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StopShowSessionPoll();
            foreach (var held in _playerAcquiredLines)
                _outputs.ReleaseVideoOutputForLine(held);
            _playerAcquiredLines.Clear();
            _playerVideoOutputs = new(StringComparer.Ordinal);
            ShowSessionActive = false;
            IsPlaying = false;
            IsMediaLoaded = false;
            CurrentPosition = TimeSpan.Zero;
            SyncIdleSlate();
        });
    }

    private void StartShowSessionPoll()
    {
        _playerShowPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerShowPoll.Tick -= OnShowSessionPollTick;
        _playerShowPoll.Tick += OnShowSessionPollTick;
        _playerShowPoll.Start();
    }

    private void StopShowSessionPoll()
    {
        _playerShowPoll?.Stop();
    }

    private async void OnShowSessionPollTick(object? sender, EventArgs e)
    {
        if (_playerShowSession is null || !ShowSessionActive)
            return;
        try
        {
            var snap = (await _playerShowSession.SnapshotAsync().ConfigureAwait(true))
                .FirstOrDefault(s => s.GroupId == ShowSession.DefaultGroup);
            if (snap is null)
                return;

            Duration = snap.ClipDuration;
            if (!IsScrubbing && !_seekArcRunning)
            {
                CurrentPosition = snap.ClipPosition;
                if (Duration > TimeSpan.Zero)
                    SeekSliderValue = snap.ClipPosition.Ticks * 1000.0 / Duration.Ticks;
            }

            // Natural end: auto-advance to the next playlist item when enabled (honoring the tab's
            // shuffle/repeat), else return the deck to idle. Stop the poll first so the (async) advance can't
            // be re-entered by the next tick.
            if (!snap.IsRunning && IsPlaying)
            {
                StopShowSessionPoll();
                if (AutoAdvancePlaylist && TryGetAutoAdvanceItem(out var next))
                    await PlayPlaylistItemAsync(next).ConfigureAwait(true);
                else
                    await ShowSessionStopAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowLog.LogTrace("MediaPlayer: ShowSession poll: {Message}", ex.Message);
        }
    }
}
