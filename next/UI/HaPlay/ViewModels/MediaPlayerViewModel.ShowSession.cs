using Avalonia.Threading;
using HaPlay.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Session;

namespace HaPlay.ViewModels;

/// <summary>
/// Phase-8 convergence: the media-player deck's FILE playback runs on a per-player headless
/// <see cref="ShowSession"/> (via <see cref="MediaPlayerShowMapper"/>) instead of <c>HaPlayPlaybackSession</c>.
/// This is the <b>default</b> as of the 2026-07-01 flip; <c>HAPLAY_USE_SHOWSESSION=0</c> falls back to the engine
/// (see <see cref="ShowSessionGate"/>). Covers play / pause / resume / stop / seek + position readout, end-of-track
/// auto-advance, idle logo/hold, and NDI live input. The transport methods early-return into here only while
/// <see cref="ShowSessionActive"/>.
/// </summary>
public partial class MediaPlayerViewModel
{
    private static readonly ILogger ShowLog = MediaDiagnostics.CreateLogger("HaPlay.MediaPlayer.ShowSession");

    private ShowSession? _playerShowSession;
    private Dictionary<string, IVideoOutput[]> _playerVideoOutputs = new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredLines = new();
    private DispatcherTimer? _playerShowPoll;
    // Consecutive poll ticks that observed the clip stopped-while-playing. A coordinated seek transiently
    // pauses the clip, so the deck only treats "not running" as end-of-track once it PERSISTS across ticks.
    private int _showSessionEndConfirmTicks;

    /// <summary>True while a file is playing through the per-player ShowSession (the transport guards divert here).</summary>
    public bool ShowSessionActive { get; private set; }

    private static bool UseShowSessionPlayer => ShowSessionGate.UseShowSession;

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

            // Switching from a currently-playing source: stop its poll and clip FIRST so the old clip releases its
            // audio DEVICE and borrowed video leases before we re-acquire and fire the new source. Without this the
            // reuse-in-place path (a) leaves the old poll running so it sees the intermediate stopped state and
            // auto-advances/stops the deck, and (b) opens the new clip's audio output on a device the old clip
            // still holds → contention → the new clip faults and the deck "just stops" instead of switching.
            if (ShowSessionActive)
            {
                await Dispatcher.UIThread.InvokeAsync(StopShowSessionPoll);
                try { await _playerShowSession.StopAsync(fade: false).ConfigureAwait(true); }
                catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession stop before source switch"); }
            }

            // UI thread: drop any idle logo, release the prior single-holder leases, then (re)acquire the video
            // lines for this source. Acquire realizes the SDL window / NDI sender, so it must run on the UI thread;
            // doing it before LoadDocument keeps the video factory a pure lookup during the (synchronous) load.
            IReadOnlyList<ShowClipAudioRoute> audioRoutes = [];
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
                // Route audio to the deck's selected device(s) (on the UI thread — reads deck observable state).
                audioRoutes = BuildDeckShowAudioRoutes(lines);
            });

            _playerShowSession.LoadDocument(MediaPlayerShowMapper.ToShowDocument(mediaPath, hasVideo, audioRoutes));
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
                // Kick the scrubber waveform explicitly. The engine path starts it post-arc (OpenOrReload's tail),
                // but the ShowSession path returns before that, and OnMediaFilePathChanged bails while a transport
                // is busy — so without this the waveform intermittently never loads on the deck. Safe/idempotent:
                // StartWaveformExtraction cancels any in-flight run and no-ops for a null/NDI (non-file) path.
                StartWaveformExtraction((item as FilePlaylistItem)?.Path);
            });
            ShowLog.LogInformation("MediaPlayer: playing through the per-player ShowSession (convergence default).");
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

    /// <summary>Deck output-line health under the ShowSession path (engine-parity for the outputs-panel LEDs):
    /// reads this player's ShowSession composition throughput for a line it drives and scores video health like
    /// the engine's <see cref="Playback.OutputLineHealthEvaluator"/>. Returns null when this deck isn't
    /// ShowSession-driving the line, so the caller falls back to the engine probe. Video only for now — the
    /// ShowSession deck path does not yet device-route audio (that is the audio-matrix re-back gap), so an
    /// audio-only deck line still reports Unknown here. Lock-free (composition stats), no marshaling.</summary>
    internal OutputLineHealthEvaluator.LineHealthMetrics? TryGetShowSessionLineHealthMetrics(Guid outputLineId)
    {
        if (!ShowSessionActive || _playerShowSession is not { } session)
            return null;
        if (!_playerAcquiredLines.Contains(outputLineId))
            return null;
        if (session.GetCompositionStats(MediaPlayerShowMapper.PlayerCompositionId) is not { } stats)
            return null;

        var videoSubmitted = stats.FramesSubmitted;
        if (videoSubmitted == 0)
            return null;
        var videoDropped = stats.PumpOverruns + stats.SlotOverflowFrames;
        var state = videoDropped == 0
            ? OutputLineHealthState.Healthy
            : videoDropped > 120 || (double)videoDropped / videoSubmitted > 0.05
                ? OutputLineHealthState.Error
                : OutputLineHealthState.Warning;
        return new OutputLineHealthEvaluator.LineHealthMetrics(
            state, videoSubmitted, videoDropped, 0, 0, 0, 0);
    }

    /// <summary>Builds the deck's initial ShowSession audio routes from its selected PortAudio output bindings, so
    /// a deck on the ShowSession path plays audio on the operator-SELECTED device(s) (with the binding's channel
    /// map + effective gain) instead of the default device — the core parity fix for the flipped default.
    /// Runs on the UI thread (reads deck observable state).</summary>
    /// <remarks>First cut: one device route per selected audio line with an out←src channel map + the compound
    /// (master × per-output) gain. Deferred (needs hardware validation): the full per-cell gain matrix and live
    /// re-apply on matrix/gain/mute edits during playback — both via <see cref="ShowSession.ApplyActiveAudioMatrixAsync"/>
    /// (the deck's <c>TrySetOutputMatrix</c> already builds the same <c>float[,]</c> the framework's
    /// <c>AudioRouter.ApplyMatrix</c> consumes); and NDI-output audio (PortAudio device lines only here).</remarks>
    private IReadOnlyList<ShowClipAudioRoute> BuildDeckShowAudioRoutes(IReadOnlyList<OutputLineViewModel> lines)
    {
        var routes = new List<ShowClipAudioRoute>();
        foreach (var line in lines)
        {
            if (line.Definition is not PortAudioOutputDefinition pa)
                continue; // NDI-output audio on the ShowSession deck is a follow-up
            if (Outputs.FirstOrDefault(b => b.Line == line) is not { } binding)
                continue;
            if (BuildDeckChannelMatrix(binding.Matrix.Cells.Select(c => (c.InputChannel, c.OutputChannel, c.Muted)).ToArray())
                is not { } matrix)
                continue; // all cells muted → silent line, no route
            routes.Add(new ShowClipAudioRoute(
                pa.EffectiveAudioBackendDeviceId, matrix, CompoundEnvelope(binding),
                pa.SampleRate > 0 ? pa.SampleRate : null));
        }
        return routes;
    }

    /// <summary>Pure: an out←src channel map (index = output channel, value = source channel, -1 = silence) from a
    /// binding's non-muted matrix cells. Defaults to a stereo identity (<c>[0,1]</c>) when the grid isn't sized yet
    /// (the source channel count is unknown until the clip opens); null when every declared cell is muted.</summary>
    internal static int[]? BuildDeckChannelMatrix(IReadOnlyList<(int Input, int Output, bool Muted)> cells)
    {
        var audible = cells.Where(c => !c.Muted).ToList();
        if (audible.Count == 0)
            return cells.Count == 0 ? [0, 1] : null; // unsized grid → stereo default; declared-but-all-muted → silent
        // Size to the widest DECLARED output so a muted high output stays an explicit silence (-1), not dropped.
        var matrix = new int[cells.Max(c => c.Output) + 1];
        Array.Fill(matrix, -1); // ChannelMap.Silence
        foreach (var c in audible)
            matrix[c.Output] = c.Input;
        return matrix;
    }

    /// <summary>Live-re-apply the deck's current audio routing (per-line channel maps + compound gains + mutes)
    /// to the RUNNING ShowSession clip, so matrix / gain / mute edits take effect DURING playback — the
    /// ShowSession analog of the engine deck's <c>TrySetOutputMatrix</c> ride. No-op off the ShowSession path
    /// (the engine methods handle their own case). Fire-and-forget: it hops the session dispatcher; a
    /// stable-composition edit (gain / per-cell route / output-mute) is applied in place, while a change that
    /// adds/removes a whole route (a line selected/deselected or all-cells-muted) is deferred by the framework
    /// to the next fire — see <see cref="ShowSession.ApplyActiveAudioRoutesAsync"/>.</summary>
    private void ReapplyDeckAudioToShowSessionIfActive()
    {
        if (_session is not null || !ShowSessionActive || _playerShowSession is not { } session)
            return;
        var routes = BuildDeckShowAudioRoutes(SelectedOutputLines());
        _ = ReapplyShowSessionAudioRoutesAsync(session, routes);
    }

    private static async Task ReapplyShowSessionAudioRoutesAsync(
        ShowSession session, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        try
        {
            await session.ApplyActiveAudioRoutesAsync(MediaPlayerShowMapper.PlayerCueId, routes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession live audio re-apply");
        }
    }

    /// <summary>Stops the player ShowSession, releases its video leases, and returns the deck to idle.</summary>
    private async Task ShowSessionStopAsync()
    {
        if (_playerShowSession is not null)
        {
            try { await _playerShowSession.StopAsync(fade: false).ConfigureAwait(true); }
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
        _showSessionEndConfirmTicks = 0;
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
            //
            // A coordinated seek transiently pauses the clip (IsRunning=false) while it reseeks the demux —
            // sometimes 100ms+ when the audio pump is slow to idle. Without discrimination the poll mistakes
            // that transient for end-of-track and tears the deck down ("freezes then stops" after a few seeks).
            // Guard on it two ways: skip while a seek/scrub is in flight, AND require the stopped state to
            // PERSIST across two ticks (a seek's pause is far shorter than the 250ms interval, so it can never
            // span two ticks; a genuine end does).
            if (ConfirmShowSessionEnded(snap.IsRunning, IsPlaying, IsScrubbing, _seekArcRunning,
                    ref _showSessionEndConfirmTicks))
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

    /// <summary>Pure end-of-track decision for the deck poll. A coordinated seek transiently pauses the clip
    /// (IsRunning=false) while it reseeks the demux, so this treats "stopped while playing" as the true end
    /// only when NOT mid-seek/scrub AND the stopped state has PERSISTED across two poll ticks — a seek's pause
    /// is far shorter than the 250 ms poll interval, so it can never span two ticks; a genuine end does.
    /// <paramref name="confirmTicks"/> accumulates consecutive qualifying ticks and is reset otherwise.</summary>
    internal static bool ConfirmShowSessionEnded(
        bool isRunning, bool isPlaying, bool isScrubbing, bool seekInFlight, ref int confirmTicks)
    {
        if (!isRunning && isPlaying && !isScrubbing && !seekInFlight)
            return ++confirmTicks >= 2;
        confirmTicks = 0;
        return false;
    }
}
