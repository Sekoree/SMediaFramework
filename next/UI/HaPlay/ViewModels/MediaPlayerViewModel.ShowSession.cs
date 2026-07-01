using Avalonia.Threading;
using HaPlay.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Audio;
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
    // Per-composition video outputs the deck drives, tagged with the OUTPUT LINE they belong to so each gets a
    // STABLE composition-output id (CompositionOutputId) — required for hot add/remove of a single line on a
    // live composition (index-based ids would shift when a line is added/removed).
    private Dictionary<string, List<(Guid LineId, IVideoOutput Output)>> _playerVideoOutputs =
        new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredLines = new();
    // NDI-output audio (ShowSession re-back): each selected audio-capable NDI line's carrier audio sink, keyed
    // by the route device id the audio-output factory resolves. Populated (and the lines held) on open, released
    // on stop/switch — the audio analogue of _playerVideoOutputs / _playerAcquiredLines.
    private readonly Dictionary<string, IAudioOutput> _playerNdiAudioOutputs = new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredAudioLines = new();
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
                    ? outs.Select(o => new ClipCompositionOutputLease(
                        CompositionOutputId(o.LineId), name, o.Output, DisposeOutputOnRuntimeDispose: false)).ToArray()
                    : Array.Empty<ClipCompositionOutputLease>(),
                // Composite on the GPU (GL, CPU-fallback) exactly like the cue workspace's ShowSession. Without
                // this the deck fell back to the CPU compositor, whose jittery frame cadence + cost made NDI
                // output video stutter (red video health in the NDI monitor) while audio stayed fine.
                CueCompositionRuntime.CreateShowSessionCompositor,
                // NDI-output audio: hand a clip's audio route to the SAME NDI carrier that emits its video. The
                // carrier audio is borrowed (held via _playerAcquiredAudioLines, released on stop) so the session
                // never disposes it; only a per-fire resampler wrapper (when the carrier rate ≠ the clip rate) is
                // session-owned. Pure lookup — the carrier was acquired on the UI thread during open.
                audioOutputFactory: (deviceId, format) => BuildNdiAudioLease(deviceId, format));

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
            var canvas = (Width: 1920, Height: 1080);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                foreach (var held in _playerAcquiredLines)
                    _outputs.ReleaseVideoOutputForLine(held);
                _playerAcquiredLines.Clear();

                var outputs = new List<(Guid LineId, IVideoOutput Output)>();
                var resolutions = new List<(int Width, int Height)>();
                if (hasVideo)
                {
                    foreach (var line in lines)
                    {
                        if (_outputs.AcquireVideoOutputForLine(line.Definition.Id) is not { } o)
                            continue;
                        outputs.Add((line.Definition.Id, o));
                        _playerAcquiredLines.Add(line.Definition.Id);
                        if (HaPlayPlaybackHelpers.TryGetOutputResolution(line.Definition, out var rw, out var rh))
                            resolutions.Add((rw, rh));
                    }
                    // Composite at the driven outputs' resolution (largest, for best quality) rather than a fixed
                    // 1080p — otherwise 4K content on a 4K line is needlessly downscaled through the canvas.
                    canvas = ResolveDeckCanvasSize(resolutions);
                }
                _playerVideoOutputs = new Dictionary<string, List<(Guid, IVideoOutput)>>(StringComparer.Ordinal)
                {
                    [MediaPlayerShowMapper.PlayerCompositionId] = outputs,
                };

                // NDI-output audio: release any previously-held NDI carrier audio, then acquire it for each
                // selected audio-capable NDI line so the routes below (and the audio-output factory) can send the
                // clip's audio into the SAME NDI stream as its video. Independent of hasVideo (audio-only NDI too).
                foreach (var held in _playerAcquiredAudioLines)
                    _outputs.ReleaseAudioOutputForLine(held);
                _playerAcquiredAudioLines.Clear();
                _playerNdiAudioOutputs.Clear();
                foreach (var line in lines)
                {
                    if (line.Definition is not NDIOutputDefinition nd || nd.StreamMode == NDIOutputStreamMode.VideoOnly)
                        continue;
                    if (_outputs.AcquireAudioOutputForLine(line.Definition.Id) is not { } audio)
                        continue;
                    _playerNdiAudioOutputs[NdiAudioDeviceId(line.Definition.Id)] = audio;
                    _playerAcquiredAudioLines.Add(line.Definition.Id);
                }

                // Route audio to the deck's selected device(s) (on the UI thread — reads deck observable state).
                audioRoutes = BuildDeckShowAudioRoutes(lines);
            });

            _playerShowSession.LoadDocument(
                MediaPlayerShowMapper.ToShowDocument(mediaPath, hasVideo, audioRoutes, canvas.Width, canvas.Height,
                    (item as FilePlaylistItem)?.Subtitles));
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
                UpdateNoOutputWarning(); // opened with no output routed → play to nothing + warn
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
    /// <remarks>One device route per selected audio line with an out←src channel map + the compound
    /// (master × per-output) gain. PortAudio lines route to their backend device; audio-capable NDI lines route
    /// to their carrier's audio side (its device id resolves through <see cref="BuildNdiAudioLease"/>, populated
    /// on open). Deferred (needs hardware validation): the full per-cell gain matrix (int channel map + one
    /// compound gain here).</remarks>
    private IReadOnlyList<ShowClipAudioRoute> BuildDeckShowAudioRoutes(IReadOnlyList<OutputLineViewModel> lines)
    {
        var routes = new List<ShowClipAudioRoute>();
        foreach (var line in lines)
        {
            if (Outputs.FirstOrDefault(b => b.Line == line) is not { } binding)
                continue;
            if (BuildDeckChannelMatrix(binding.Matrix.Cells.Select(c => (c.InputChannel, c.OutputChannel, c.Muted)).ToArray())
                is not { } matrix)
                continue; // all cells muted → silent line, no route

            switch (line.Definition)
            {
                case PortAudioOutputDefinition pa:
                    routes.Add(new ShowClipAudioRoute(
                        pa.EffectiveAudioBackendDeviceId, matrix, CompoundEnvelope(binding),
                        pa.SampleRate > 0 ? pa.SampleRate : null));
                    break;
                case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly:
                    // Only route to an NDI line whose carrier audio was acquired on open; the device id maps back
                    // to that borrowed carrier in the audio-output factory.
                    var ndiDeviceId = NdiAudioDeviceId(line.Definition.Id);
                    if (_playerNdiAudioOutputs.ContainsKey(ndiDeviceId))
                        routes.Add(new ShowClipAudioRoute(
                            ndiDeviceId, matrix, CompoundEnvelope(binding),
                            nd.AudioSampleRate > 0 ? nd.AudioSampleRate : null));
                    break;
            }
        }
        return routes;
    }

    /// <summary>Stable route device id for an NDI line's carrier audio — the key shared by
    /// <see cref="BuildDeckShowAudioRoutes"/> (emits it), the <c>_playerNdiAudioOutputs</c> map (populated on
    /// open), and <see cref="BuildNdiAudioLease"/> (resolves it in the audio-output factory).</summary>
    private static string NdiAudioDeviceId(Guid lineId) => $"ndi-audio:{lineId}";

    /// <summary>Stable composition-output id for a driven output line — shared by the video-output factory (fire
    /// path) and hot add/remove, so a single line's composition output is attached/detached by a fixed id
    /// (index-based ids would shift when a line is added or removed).</summary>
    private static string CompositionOutputId(Guid lineId) =>
        $"{MediaPlayerShowMapper.PlayerCompositionId}_line_{lineId:N}";

    /// <summary>The audio-output factory body: resolves an NDI route's device id to the borrowed carrier audio
    /// held since open, wrapping it in a per-fire resampler only when the carrier's audio format differs from the
    /// clip's requested format. Null for a device the deck didn't acquire (a PortAudio route → the session's
    /// backend creates it). Pure dictionary lookup, safe on the session thread.</summary>
    private ClipAudioOutputLease? BuildNdiAudioLease(string deviceId, AudioFormat format)
    {
        if (!_playerNdiAudioOutputs.TryGetValue(deviceId, out var carrierAudio))
            return null;
        if (carrierAudio.Format.SampleRate == format.SampleRate && carrierAudio.Format.Channels == format.Channels)
            // Format matches → route straight into the carrier (borrowed: released by the deck on stop).
            return new ClipAudioOutputLease(carrierAudio, DisposeOutputOnRuntimeDispose: false);
        // Mismatch → a per-fire resampler adapts the clip to the carrier. Its Dispose frees only the resampler
        // (NOT the borrowed carrier), so the session may own it (DisposeOutputOnRuntimeDispose: true).
        return new ClipAudioOutputLease(
            ResamplingAudioOutput.Wrap(carrierAudio, format), DisposeOutputOnRuntimeDispose: true);
    }

    /// <summary>Pure: the deck's composition-canvas size = the LARGEST driven output resolution (by pixel area),
    /// so a single canvas feeds every line at native quality and each line scales down as needed. Falls back to
    /// 1080p when no output advertises a locked resolution (e.g. an auto-sized local window).</summary>
    internal static (int Width, int Height) ResolveDeckCanvasSize(IEnumerable<(int Width, int Height)> outputResolutions)
    {
        int w = 0, h = 0;
        foreach (var (rw, rh) in outputResolutions)
            if (rw > 0 && rh > 0 && (long)rw * rh > (long)w * h)
                (w, h) = (rw, rh);
        return w > 0 && h > 0 ? (w, h) : (1920, 1080);
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

    /// <summary>True while a file is playing through the per-player ShowSession — the deck's hot output add/remove
    /// (the ShowSession analog of the engine's TryAddOutput/TryRemoveOutput) should divert here.</summary>
    internal bool ShowSessionHotSwapActive => ShowSessionActive && _playerShowSession is not null;

    /// <summary>Hot-adds an output LINE to the RUNNING ShowSession deck without a re-fire (the ShowSession analog
    /// of the engine's <c>TryAddOutput</c>): acquires + attaches the line's video output to the live composition,
    /// acquires an audio-capable NDI line's carrier audio, then REBUILDS the clip's audio outputs so the new
    /// line's audio (and video) attach at the live position. UI thread.</summary>
    private async Task HotAddOutputToShowSessionAsync(OutputLineViewModel line)
    {
        if (!ShowSessionHotSwapActive || _playerShowSession is not { } session)
            return;
        var lineId = line.Definition.Id;

        // Video: acquire + attach to the live composition (skip if already driven). AddCompositionOutputAsync
        // returns false for an audio-only source (no composition) — release rather than hold the lease.
        if (_playerVideoOutputs.TryGetValue(MediaPlayerShowMapper.PlayerCompositionId, out var vids)
            && !_playerAcquiredLines.Contains(lineId)
            && _outputs.AcquireVideoOutputForLine(lineId) is { } vo)
        {
            var attached = await session.AddCompositionOutputAsync(
                MediaPlayerShowMapper.PlayerCompositionId,
                new ClipCompositionOutputLease(CompositionOutputId(lineId), line.Definition.DisplayName, vo,
                    DisposeOutputOnRuntimeDispose: false)).ConfigureAwait(true);
            if (attached)
            {
                vids.Add((lineId, vo));
                _playerAcquiredLines.Add(lineId);
            }
            else
            {
                _outputs.ReleaseVideoOutputForLine(lineId);
            }
        }

        // NDI audio: hold the carrier audio for an audio-capable NDI line so the rebuild's route can reach it.
        if (line.Definition is NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.VideoOnly }
            && !_playerNdiAudioOutputs.ContainsKey(NdiAudioDeviceId(lineId))
            && _outputs.AcquireAudioOutputForLine(lineId) is { } audio)
        {
            _playerNdiAudioOutputs[NdiAudioDeviceId(lineId)] = audio;
            _playerAcquiredAudioLines.Add(lineId);
        }

        await RebuildDeckShowSessionAudioAsync().ConfigureAwait(true);
        UpdateNoOutputWarning();
    }

    /// <summary>Hot-removes an output LINE from the RUNNING ShowSession deck (the ShowSession analog of the
    /// engine's <c>TryRemoveOutput</c>): detaches its video from the live composition, REBUILDS the clip's audio
    /// outputs to drop its route (the clip keeps playing on its discard sink even at zero outputs), THEN releases
    /// the physical outputs — the order matters: releasing a sink before its router route is gone would dangle.
    /// UI thread.</summary>
    private async Task HotRemoveOutputFromShowSessionAsync(OutputLineViewModel line)
    {
        if (!ShowSessionHotSwapActive || _playerShowSession is not { } session)
            return;
        var lineId = line.Definition.Id;

        // 1) Detach video from the live composition + drop it from tracking (don't release the lease yet).
        var hadVideo = _playerVideoOutputs.TryGetValue(MediaPlayerShowMapper.PlayerCompositionId, out var vids)
                       && vids.RemoveAll(v => v.LineId == lineId) > 0;
        if (hadVideo)
        {
            await session.RemoveCompositionOutputAsync(
                MediaPlayerShowMapper.PlayerCompositionId, CompositionOutputId(lineId)).ConfigureAwait(true);
            _playerAcquiredLines.Remove(lineId);
        }

        // 2) Drop the NDI carrier from the audio map so the rebuild excludes its route (don't release it yet).
        var hadNdiAudio = _playerNdiAudioOutputs.Remove(NdiAudioDeviceId(lineId));
        if (hadNdiAudio)
            _playerAcquiredAudioLines.Remove(lineId);

        // 3) Rebuild the clip's audio outputs from the REMAINING routes — removes the dead route in the router
        //    before we release its sink; the clip keeps playing (its discard sink stays) even down to zero.
        await RebuildDeckShowSessionAudioAsync().ConfigureAwait(true);

        // 4) Now the physical outputs carry no route/composition reference — safe to release.
        if (hadVideo)
            _outputs.ReleaseVideoOutputForLine(lineId);
        if (hadNdiAudio)
            _outputs.ReleaseAudioOutputForLine(lineId);

        UpdateNoOutputWarning();
    }

    /// <summary>Rebuilds the RUNNING ShowSession clip's audio outputs from the deck's current routes — the
    /// count-change path (hot add/remove) that <see cref="ReapplyDeckAudioToShowSessionIfActive"/>'s in-place
    /// re-apply defers. Keeps playback running (the clip's discard sink stays) even when no output is routed.</summary>
    private async Task RebuildDeckShowSessionAudioAsync()
    {
        if (_session is not null || !ShowSessionActive || _playerShowSession is not { } session)
            return;
        var routes = BuildDeckShowAudioRoutes(SelectedOutputLines());
        try
        {
            await session.RebuildActiveClipAudioOutputsAsync(MediaPlayerShowMapper.PlayerCueId, routes)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession audio rebuild");
        }
    }

    /// <summary>Surfaces a banner when a loaded/playing deck has NO output routed — playback continues (to
    /// nothing) rather than stopping, matching a hardware player. Cleared once any output is routed again.</summary>
    private void UpdateNoOutputWarning()
    {
        if (IsMediaLoaded && SelectedOutputLines().Count == 0)
            StatusMessage = "No output routed — the deck is still playing. Route an output to see/hear it.";
        else if (StatusMessage is not null && StatusMessage.StartsWith("No output routed", StringComparison.Ordinal))
            StatusMessage = null;
    }

    /// <summary>Stops the player ShowSession, releases its video leases, and returns the deck to idle.</summary>
    private async Task ShowSessionStopAsync()
    {
        if (_playerShowSession is { } session)
        {
            try { await session.StopAsync(fade: false).ConfigureAwait(true); }
            catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession stop"); }

            // Detach every output from the live composition BEFORE the UI releases them below. The composition
            // pump outlives the clip; if an output is still attached when ReleaseVideoOutputForLine reconfigures
            // it back to its idle/native format, the pump keeps submitting canvas-format frames to it → a
            // format-mismatch flood (Submit throws every tick). Detaching first stops those submits.
            var attachedLines = await Dispatcher.UIThread.InvokeAsync(() => _playerAcquiredLines.ToList());
            foreach (var held in attachedLines)
            {
                try
                {
                    await session.RemoveCompositionOutputAsync(
                        MediaPlayerShowMapper.PlayerCompositionId, CompositionOutputId(held)).ConfigureAwait(true);
                }
                catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession detach output on stop"); }
            }
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
            foreach (var held in _playerAcquiredAudioLines)
                _outputs.ReleaseAudioOutputForLine(held);
            _playerAcquiredAudioLines.Clear();
            _playerNdiAudioOutputs.Clear();
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

            // Live-source disconnect: an NDI/capture input dropped (its live source is exhausted, though a live
            // router can keep reporting IsRunning while it waits for data — so this is checked separately). A
            // live source NEVER playlist-auto-advances; end the clip like the engine's IsLiveSourceDisconnected
            // path — return to idle, and fire cue AutoFollow when this deck was playing a cue.
            if (snap.LiveSourceDisconnected && IsPlaying)
            {
                StopShowSessionPoll();
                var wasCue = _cuePlaybackActive;
                await ShowSessionStopAsync().ConfigureAwait(true);
                if (wasCue)
                {
                    _cuePlaybackActive = false;
                    NaturalPlaybackEnded?.Invoke(this, EventArgs.Empty);
                }
                return;
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
