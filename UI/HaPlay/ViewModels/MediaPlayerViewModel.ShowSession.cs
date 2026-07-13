using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// The media-player deck's file and live-input playback runs on a per-player headless
/// <see cref="ShowSession"/> (via <see cref="MediaPlayerShowMapper"/>) - the only playback runtime since the
/// legacy engines were deleted (NXT-13). Covers play / pause / resume / stop / seek + position readout,
/// end-of-track auto-advance, idle logo/hold, and NDI live input. The transport methods operate only while
/// <see cref="ShowSessionActive"/>.
/// </summary>
public partial class MediaPlayerViewModel
{
    private static readonly ILogger ShowLog = MediaDiagnostics.CreateLogger("HaPlay.MediaPlayer.ShowSession");

    private ShowSession? _playerShowSession;

    /// <summary>The deck's headless session for the Debug-stats poll (null while no session is built).
    /// Read-only, lock-free consumers only - see <see cref="ShowSession.GetActiveClipPipelineMetrics"/>.</summary>
    internal ShowSession? PipelineStatsSession => _playerShowSession;

    // --- projectM visualizer (Phase 5): a session-held full-canvas GL layer fed by the deck's audio ---

    /// <summary>Latched "VIZ" toggle: while on (and libprojectM-4 is present), a projectM layer covers
    /// the deck's composition, audio-reactive to whatever the deck plays. Survives track changes (the
    /// open path re-applies it after each document swap, like HOLD).</summary>
    [ObservableProperty]
    private bool _visualizerEnabled;

    // One settings read for all visualizer fields (avoids 4× disk reads + FileGate contention on the
    // UI thread during VM construction).
    private static readonly Models.AppSettings VisualizerSettingsSeed = Models.AppSettings.Load();

    /// <summary>Preset directory for the visualizer (*.milk, scanned recursively). Persisted
    /// per-machine (AppSettings, set via the VIZ ▾ picker); defaults to the dev build's fetched pack.</summary>
    [ObservableProperty]
    private string? _visualizerPresetDirectory =
        VisualizerSettingsSeed.VisualizerPresetDirectory is { Length: > 0 } saved && Directory.Exists(saved)
            ? saved
            : DefaultPresetDirectory();

    partial void OnVisualizerPresetDirectoryChanged(string? value)
    {
        _ = value;
        // Live re-apply: a new folder replaces the running surface (fresh preset scan).
        if (VisualizerEnabled)
            _ = ApplyShowSessionVisualizerAsync();
    }

    /// <summary>Visualizer render width (0 = follow the canvas / output preset). Persisted per-machine.</summary>
    [ObservableProperty]
    private int _visualizerWidth = VisualizerSettingsSeed.VisualizerWidth;

    /// <summary>Visualizer render height (0 = follow the canvas / output preset).</summary>
    [ObservableProperty]
    private int _visualizerHeight = VisualizerSettingsSeed.VisualizerHeight;

    /// <summary>Visualizer target FPS (0 = follow the canvas rate).</summary>
    [ObservableProperty]
    private int _visualizerFps = VisualizerSettingsSeed.VisualizerFps;

    /// <summary>A human summary of the visualizer resolution for the settings UI. Zero (older settings
    /// files) resolves to the real default, so the label says so instead of the old "Match output" lie.</summary>
    public string VisualizerResolutionLabel =>
        VisualizerWidth > 0 && VisualizerHeight > 0
            ? $"{VisualizerWidth}×{VisualizerHeight}" + (VisualizerFps > 0 ? $" @ {VisualizerFps} fps" : "")
            : "Default (1920×1080 @ 60 fps)";

    /// <summary>VIZ ▾: applies (and persists) the visualizer render resolution + fps. 0/0 = follow the
    /// deck's normal canvas sizing. Re-applies a live visualizer (reopens when it owns the canvas).</summary>
    internal void SetVisualizerResolution(int width, int height, int fps)
    {
        VisualizerWidth = Math.Max(0, width);
        VisualizerHeight = Math.Max(0, height);
        VisualizerFps = Math.Max(0, fps);
        OnPropertyChanged(nameof(VisualizerResolutionLabel));
        PersistVisualizerSettings();

        if (VisualizerEnabled)
            _ = ApplyShowSessionVisualizerAsync();
    }

    /// <summary>The visualizer's fixed render size + rate (explicit setting, else 1080p60 as the default).
    /// Also drives the deck canvas while VIZ owns it, so every track's canvas is identical - the invariant
    /// that lets one visualizer run across track changes.</summary>
    private (int Width, int Height, int Fps) ResolveVisualizerRenderSize() =>
        (VisualizerWidth > 0 ? VisualizerWidth : 1920,
         VisualizerHeight > 0 ? VisualizerHeight : 1080,
         VisualizerFps > 0 ? VisualizerFps : 60);

    /// <summary>VIZ ▾: pick the preset folder (persisted per-machine, re-applies a live visualizer).</summary>
    internal void SetVisualizerPresetDirectory(string path)
    {
        VisualizerPresetDirectory = path;
        PersistVisualizerSettings();
    }

    /// <summary>Persists the deck's visualizer settings OFF the UI thread - Save() does synchronous
    /// disk I/O (temp-file write + atomic move, with retry sleeps) that must never stall the UI.</summary>
    private void PersistVisualizerSettings()
    {
        var dir = VisualizerPresetDirectory;
        var (w, h, fps) = (VisualizerWidth, VisualizerHeight, VisualizerFps);
        _ = Task.Run(() =>
        {
            try
            {
                Models.AppSettings.Update(settings =>
                {
                    settings.VisualizerPresetDirectory = dir;
                    settings.VisualizerWidth = w;
                    settings.VisualizerHeight = h;
                    settings.VisualizerFps = fps;
                });
            }
            catch (Exception ex)
            {
                ShowLog.LogWarning(ex, "visualizer settings not persisted");
            }
        });
    }

    public bool IsVisualizerAvailable => RuntimeModules.IsProjectMAvailable;

    public string? VisualizerUnavailableReason => RuntimeModules.ProjectMUnavailableReason;

    public string VisualizerTooltip => RuntimeModules.IsProjectMAvailable
        ? Resources.Strings.VisualizerToggleTooltip
        : RuntimeModules.ProjectMUnavailableReason ?? Resources.Strings.VisualizerToggleTooltip;

    // The attached source, kept ONLY for the next-preset request (the session owns its lifetime).
    private S.Media.Visualizer.ProjectM.ProjectMVisualSource? _visualizerSource;

    // The current open has the visualizer owning a FIXED canvas (any video/cover-art base layer swapped
    // out, audio drives the viz). Toggling VIZ reopens in place when this must flip: ON → make the canvas
    // viz-owned; OFF → restore the native content. This is what "restart on enable/disable, continuous
    // while on" hangs off of. Set at open, read by the toggle handler.
    private bool _playerShowVizOwnsCanvas;

    partial void OnVisualizerEnabledChanged(bool value)
    {
        if (!value)
        {
            // The deck owns the persistent source (disposeSourceOnRemove: false at attach) - VIZ-off is
            // the "restart boundary": stop the continuous renderer here. The apply below detaches the
            // session side (which won't double-dispose).
            _visualizerSource?.Dispose();
            _visualizerSource = null;
        }

        _ = ApplyShowSessionVisualizerAsync();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NextVisualizerPreset() => _visualizerSource?.RequestNextPreset();

    private static string? DefaultPresetDirectory()
    {
        // scripts/build-projectm.sh installs a preset pack under its install root (…/<rid>/presets).
        // Prefer the explicit env override's sibling, else the auto-discovered dev build; a plain
        // system install has no obvious preset home, so leave it unset there.
        var libDir = Environment.GetEnvironmentVariable(ProjectMLib.Runtime.ProjectMLibraryResolver.EnvironmentOverride);
        if (!string.IsNullOrWhiteSpace(libDir) && Directory.Exists(libDir))
        {
            var root = Path.GetDirectoryName(libDir.TrimEnd(Path.DirectorySeparatorChar));
            var presets = root is null ? null : Path.Combine(root, "presets");
            if (presets is not null && Directory.Exists(presets))
                return presets;
        }

        if (ProjectMLib.Runtime.ProjectMLibraryResolver.TryFindDevBuildRoot() is { } devRoot)
        {
            var presets = Path.Combine(devRoot, "presets");
            if (Directory.Exists(presets))
                return presets;
        }

        return null;
    }

    /// <summary>Applies the VIZ toggle to the RUNNING session: attaches (or removes) the projectM layer
    /// + audio tap on the deck's composition. Safe/idempotent; a failure just clears the toggle with a
    /// log line (no crash when a preset dir is bogus or the compositor fell back to CPU).</summary>
    // Serializes visualizer applies: a rapid toggle / preset-change / resolution-change (or a
    // reopen's re-apply racing a manual toggle) must not stack overlapping reopens or attach twice.
    private readonly SemaphoreSlim _visualizerApplyGate = new(1, 1);

    internal async Task ApplyShowSessionVisualizerAsync()
    {
        if (_playerShowSession is null)
            return;
        await _visualizerApplyGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ApplyShowSessionVisualizerCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _visualizerApplyGate.Release();
        }
    }

    private async Task ApplyShowSessionVisualizerCoreAsync()
    {
        if (_playerShowSession is not { } session)
            return;
        try
        {
            if (!VisualizerEnabled)
            {
                ClearVisualizerWarning();
                // If the visualizer owned the canvas (a video/cover-art base layer was swapped out, or a
                // pure viz canvas on audio), reopen in place so the native content returns now that VIZ is
                // off. Otherwise just detach the surface.
                if (ShowSessionActive && IsMediaLoaded && !IsLiveSource && _playerShowVizOwnsCanvas)
                {
                    await ReopenInPlacePreservingPositionAsync().ConfigureAwait(true);
                    return;
                }

                await session.SetCompositionVisualizerAsync(MediaPlayerShowMapper.PlayerCompositionId, null)
                    .ConfigureAwait(true);
                return;
            }

            if (!RuntimeModules.IsProjectMAvailable)
            {
                ShowLog.LogWarning("visualizer unavailable: {Reason}", RuntimeModules.ProjectMUnavailableReason);
                SetVisualizerWarning($"Visualizer unavailable: {RuntimeModules.ProjectMUnavailableReason}");
                VisualizerEnabled = false;
                return;
            }

            // Mid-play enable: if the current open isn't already visualizer-owned (plain audio with no
            // canvas, cover art, OR real video), reopen the item IN PLACE (position + play state preserved)
            // so the open path builds the fixed viz canvas with the base video layer swapped out; its
            // completion re-enters here and attaches the surface normally.
            if (ShowSessionActive && IsMediaLoaded && !IsLiveSource && !_playerShowVizOwnsCanvas)
            {
                const string rebuilding = "Visualizer: rebuilding the canvas…";
                SetVisualizerWarning(rebuilding);
                await ReopenInPlacePreservingPositionAsync().ConfigureAwait(true);
                // Clear only OUR banner - the reopen's completion may have posted its own (idle preset hint).
                if (StatusMessage == rebuilding)
                    StatusMessage = null;
                return; // the reopen completion applied the visualizer on the fresh canvas
            }

            var presetDir = VisualizerPresetDirectory;
            // Count the presets off-thread (a large pack stats thousands of files - never on the
            // dispatcher). The count feeds the status banner so the operator can verify their folder.
            var presetCount = presetDir is { Length: > 0 } && Directory.Exists(presetDir)
                ? await Task.Run(() =>
                {
                    try { return Directory.EnumerateFiles(presetDir, "*.milk", SearchOption.AllDirectories).Count(); }
                    catch { return 0; }
                }).ConfigureAwait(true)
                : 0;

            // PERSISTENT source: reuse the running one across track changes (that's the continuity - its
            // renderer thread never stops); recreate only when its settings changed (resolution/fps/preset
            // dir) - the documented "restarts on settings change" boundary.
            var (renderW, renderH, renderFps) = ResolveVisualizerRenderSize();
            if (_visualizerSource is { } running
                && (running.Options.RenderWidth != renderW
                    || running.Options.RenderHeight != renderH
                    || running.Options.Fps != renderFps
                    || !string.Equals(running.Options.PresetDirectory, presetDir, StringComparison.Ordinal)))
            {
                running.Dispose();
                _visualizerSource = null;
            }

            var source = _visualizerSource ?? new S.Media.Visualizer.ProjectM.ProjectMVisualSource(
                renderW, renderH, new S.Media.Core.Video.Rational(renderFps > 0 ? renderFps : 30, 1),
                new S.Media.Visualizer.ProjectM.ProjectMOptions
                {
                    PresetDirectory = presetDir,
                    RenderWidth = renderW,
                    RenderHeight = renderH,
                    Fps = renderFps,
                    Shuffle = true,
                });
            // disposeSourceOnRemove: false - the DECK owns this source's lifetime (VIZ-off / settings
            // change / deck teardown), so the per-track document reload only unhooks it and this method
            // re-attaches the SAME source to the fresh composition: the visuals continue mid-stream.
            var attached = await session
                .SetCompositionVisualizerAsync(
                    MediaPlayerShowMapper.PlayerCompositionId, source, disposeSourceOnRemove: false)
                .ConfigureAwait(true);
            if (!attached)
            {
                // Keep the source alive (its renderer keeps running) - the next successful open attaches it.
                _visualizerSource = source;
                ShowLog.LogWarning("visualizer not attached: no active canvas/GL composition");
                // Enabling VIZ during audio-only playback that was OPENED without it: the canvas only
                // exists when the open declared it - a replay picks it up.
                SetVisualizerWarning(
                    "Visualizer will start with the next track: press ▶ (or restart the current item) so the canvas is created.");
            }
            else
            {
                _visualizerSource = source;
                if (presetCount <= 0)
                    SetVisualizerWarning(
                        "Visualizer running with the built-in idle preset only - pick a *.milk preset folder via the VIZ ▾ button.");
                else
                    SetVisualizerWarning($"Visualizer: {presetCount} presets loaded from {Path.GetFileName(presetDir!.TrimEnd(Path.DirectorySeparatorChar))}.");
            }
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "visualizer apply failed");
            SetVisualizerWarning($"Visualizer failed: {ex.Message}");
        }
    }

    /// <summary>Reopens the current item in place, preserving the play position. Used by the VIZ toggle
    /// to flip the canvas between viz-owned and native content. The live Duration/CurrentPosition reset
    /// during the close, so the seek uses snapshots taken BEFORE the reopen (a live-Duration gate would
    /// see 0 and skip the seek, replaying from the start).</summary>
    private async Task ReopenInPlacePreservingPositionAsync()
    {
        var resumePosition = CurrentPosition;
        var knownDuration = Duration;
        await OpenOrReloadAsync().ConfigureAwait(true);
        if (resumePosition > TimeSpan.FromSeconds(1)
            && (knownDuration <= TimeSpan.Zero || resumePosition < knownDuration))
        {
            await ShowSessionSeekAsync(resumePosition).ConfigureAwait(true); // SeekAsync clamps
        }
    }

    private const string VisualizerWarningPrefix = "Visualizer";

    private void SetVisualizerWarning(string message) => StatusMessage = message;

    private void ClearVisualizerWarning()
    {
        if (StatusMessage is not null && StatusMessage.StartsWith(VisualizerWarningPrefix, StringComparison.Ordinal))
            StatusMessage = null;
    }
    // Per-composition video outputs the deck drives, tagged with the OUTPUT LINE they belong to so each gets a
    // STABLE composition-output id (CompositionOutputId) - required for hot add/remove of a single line on a
    // live composition (index-based ids would shift when a line is added/removed).
    private Dictionary<string, List<(Guid LineId, IVideoOutput Output)>> _playerVideoOutputs =
        new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredLines = new();
    // NDI-output audio (ShowSession re-back): each selected audio-capable NDI line's carrier audio sink, keyed
    // by the route device id the audio-output factory resolves. Populated (and the lines held) on open, released
    // on stop/switch - the audio analogue of _playerVideoOutputs / _playerAcquiredLines.
    private readonly Dictionary<string, IAudioOutput> _playerNDIAudioOutputs = new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredAudioLines = new();
    // PortAudio route device id → the LINE's configured sample rate, so the audio-output factory can fall back
    // to opening a device at its own rate (behind an egress resampler) when it rejects the clip's mix rate.
    // Concurrent: written on the UI thread (route building), read on the session thread (the factory).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _playerPaDeviceRates =
        new(StringComparer.Ordinal);
    private DispatcherTimer? _playerShowPoll;
    // Consecutive poll ticks that observed the clip stopped-while-playing. A coordinated seek transiently
    // pauses the clip, so the deck only treats "not running" as end-of-track once it PERSISTS across ticks.
    private int _showSessionEndConfirmTicks;
    // The last TransportSnapshot.TimelineGeneration the poll saw (NXT-04): a change = seek/pause/resume/clip
    // swap just happened, so the end-confirmation window restarts (its transient pause must not read as end).
    private int _showSessionLastTimelineGeneration = -1;
    // The current show's composition canvas size (set at open) - the HOLD image renders at this size so the
    // top layer covers the canvas exactly.
    private (int Width, int Height) _playerShowCanvas = (1920, 1080);
    // Whether the current source opened with a video composition (the file probe / live stream selection at
    // open) - drives the SourceKindLabel readout.
    private bool _playerShowHasVideo;

    /// <summary>True while a deck source is playing through the per-player ShowSession (transport diverts here).</summary>
    public bool ShowSessionActive { get; private set; }

    /// <summary>Opens a deck source: builds/loads the 1-cue player show and fires it. Returns false on a
    /// non-live open failure (the caller surfaces the error); live failures enter the waiting-for-source
    /// retry and return true.</summary>
    private async Task<bool> TryOpenViaShowSessionAsync(PlaylistItem item, IReadOnlyList<OutputLineViewModel> lines)
    {
        // Every open bumps the play generation FIRST: any natural-end event raised for the previous
        // track is dropped at delivery (see the ClipNaturallyEnded subscription).
        Interlocked.Increment(ref _playGeneration);

        // Resolve the registry URI + whether there's a video composition. Files probe for a video stream;
        // live inputs map to their option-carrying ndi:/padev: descriptor URIs.
        string mediaPath;
        bool hasVideo;
        // The source's native raster/rate (0 = unknown). Only a probed file exposes it up front; live inputs
        // (NDI/capture) and YouTube keep it 0 and the canvas falls back to the driven output resolution.
        int srcWidth = 0, srcHeight = 0, srcFpsNum = 0, srcFpsDen = 0;
        switch (item)
        {
            case FilePlaylistItem file:
                mediaPath = file.Path;
                var probe = await CueMediaProbe.TryProbeAsync(file.Path).ConfigureAwait(true);
                hasVideo = probe?.HasVideo == true;
                if (probe is { HasVideo: true } vp)
                    (srcWidth, srcHeight, srcFpsNum, srcFpsDen) =
                        (vp.SourceVideoWidth, vp.SourceVideoHeight, vp.SourceFrameRateNum, vp.SourceFrameRateDen);
                break;
            case NDIInputPlaylistItem ndi when RuntimeModules.IsNDIAvailable:
                mediaPath = BuildNDIInputUri(ndi);
                hasVideo = !ndi.AudioOnly;
                break;
            case YouTubePlaylistItem yt:
                // Prepared-cache asset behind its canonical URI (reliable mode: unprepared selections fail
                // the open with an actionable error instead of starting a download mid-show).
                mediaPath = HaPlayPlaybackHelpers.BuildYouTubeUri(yt);
                hasVideo = !yt.AudioOnly && yt.VideoStreamDescriptor is not null;
                break;
            case MMDPlaylistItem mmd:
                mediaPath = HaPlayPlaybackHelpers.BuildMMDUri(mmd);
                hasVideo = true; // the MMD source is video-only
                break;
            case PortAudioInputPlaylistItem paIn:
                // Live capture through the registry's `padev:` provider (the same URI the cue path uses; the
                // provider unescapes, so device names with spaces round-trip). Audio-only - no composition.
                mediaPath = BuildPortAudioInputUri(paIn);
                hasVideo = false;
                break;
            default:
                return false;
        }

        // Visualizer owns the canvas: when VIZ is on it fills the frame, so swap out ANY base video layer
        // (cover art OR a real video file/stream) and play the track audio-only - its audio drives the
        // visualizer and still reaches the speakers, and the cover art/tags still flow to the metadata hub.
        // This makes the visualizer work over video, and keeps the canvas identical across every track (the
        // invariant the continuous-across-tracks behaviour depends on). Scoped to file/YouTube media; live
        // capture (NDI/PortAudio) and MMD keep their own pipeline.
        if (hasVideo && VisualizerEnabled && IsVisualizerAvailable
            && item is FilePlaylistItem or YouTubePlaylistItem)
        {
            hasVideo = false;
        }

        try
        {
            _playerShowSession ??= new ShowSession(
                MediaRuntime.Registry,
                MediaRuntime.Registry.AudioBackends.FirstOrDefault(),
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex),
                // Borrowed lines: the deck owns each output's lifetime (acquire/release via _playerAcquiredLines),
                // so the leases declare DisposeOutputOnRuntimeDispose=false - the session never disposes them (NXT-01).
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
                // session-owned. Pure lookup - the carrier was acquired on the UI thread during open.
                audioOutputFactory: (deviceId, format) => BuildDeckAudioLease(deviceId, format),
                // Effect buses (Phase 4): tags + cover art for the metadata hub (visualizers/overlays).
                metadataProbe: S.Media.Decode.FFmpeg.MediaTagProbe.TryRead);

            // Review M4: event-driven advancement (primary; the 250ms poll stays as fallback with a shared
            // advance-once gate). Raised on the SESSION dispatcher - never for loops or operator stops -
            // so end detection no longer depends on UI poll heuristics. Subscribed once per session.
            if (!_naturalEndHooked)
            {
                _naturalEndHooked = true;
                var hookedSession = _playerShowSession;
                hookedSession.ClipNaturallyEnded += cueId =>
                {
                    if (!string.Equals(cueId, MediaPlayerShowMapper.PlayerCueId, StringComparison.Ordinal))
                        return;
                    var generationAtRaise = Volatile.Read(ref _playGeneration);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_playerShowSession, hookedSession)
                            && generationAtRaise == Volatile.Read(ref _playGeneration))
                        {
                            _ = HandleShowSessionNaturalEndAsync("event");
                        }
                    });
                };
            }

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

                // NXT-20: detach the still-attached outputs from the LIVE composition BEFORE the release/
                // re-acquire block below. The composition pump outlives the clip; releasing a line first
                // reconfigures its sink (idle slate) while the old composition is still submitting canvas
                // frames into it - the same format-mismatch flood ShowSessionStopAsync guards against.
                var attachedLines = await Dispatcher.UIThread.InvokeAsync(() => _playerAcquiredLines.ToList());
                foreach (var held in attachedLines)
                {
                    try
                    {
                        await _playerShowSession.RemoveCompositionOutputAsync(
                            MediaPlayerShowMapper.PlayerCompositionId, CompositionOutputId(held)).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        ShowLog.LogWarning(ex, "MediaPlayer: ShowSession detach output on source switch");
                    }
                }
            }

            // UI thread: drop any idle logo, release the prior single-holder leases, then (re)acquire the video
            // lines for this source. Acquire realizes the SDL window / NDI sender, so it must run on the UI thread;
            // doing it before LoadDocument keeps the video factory a pure lookup during the (synchronous) load.
            IReadOnlyList<ShowClipAudioRoute> audioRoutes = [];
            var canvas = (Width: 1920, Height: 1080);
            var canvasFps = (Num: 30, Den: 1);
            var canvasDeclared = false;
            var vizOwnsCanvasResult = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                // #25 REVERTED (2nd attempt): holding output leases across the track change removed the
                // black gap but the user hit a freeze during song change - the held-output/composition-swap
                // interaction needs live debugging before another attempt. Release/re-acquire is the
                // proven-stable path; the black gap is the accepted cost until then.
                foreach (var held in _playerAcquiredLines)
                    _outputs.ReleaseVideoOutputForLine(held);
                _playerAcquiredLines.Clear();

                var outputs = new List<(Guid LineId, IVideoOutput Output)>();
                var resolutions = new List<(int Width, int Height)>();
                // The visualizer needs the canvas + video lines even for audio-only media (its surface
                // becomes the canvas content); a plain audio open keeps skipping them.
                var wantsCanvas = hasVideo || (VisualizerEnabled && IsVisualizerAvailable);
                if (wantsCanvas)
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
                    // Media-player sizing: the compositor is AUTOSIZED to the source (AsSource) - or to the chosen
                    // fixed preset - and the OUTPUT letterboxes into each display. This replaces the old
                    // "canvas = largest output resolution + Cover fit", which cropped differing-aspect media to
                    // cover the screen. A live input with no known raster still falls back to the driven outputs.
                    // When VIZ is on it already suppressed the base video layer (hasVideo == false), so the
                    // visualizer owns the canvas at its own FIXED resolution.
                    var sizeFromSource = hasVideo;
                    var vizOwnsCanvas = !sizeFromSource && VisualizerEnabled && IsVisualizerAvailable;
                    if (vizOwnsCanvas)
                    {
                        // Fixed visualizer canvas (1080p60 default, tunable). The deck rebuilds this canvas per
                        // track - continuous-across-tracks preservation was reverted (it wedged the dispatcher;
                        // see the composition-preservation notes). Use the cue player for a continuous visualizer.
                        var (vw, vh, vf) = ResolveVisualizerRenderSize();
                        canvas = (vw, vh);
                        canvasFps = (vf, 1);
                    }
                    else
                    {
                        var resolved = ResolveDeckCanvas(
                            OutputPreset, SanitizedCustomOutputWidth(), SanitizedCustomOutputHeight(),
                            sizeFromSource ? srcWidth : 0, sizeFromSource ? srcHeight : 0,
                            sizeFromSource ? srcFpsNum : 0, sizeFromSource ? srcFpsDen : 0, resolutions);
                        canvas = (resolved.Width, resolved.Height);
                        canvasFps = (resolved.FpsNum, resolved.FpsDen);
                    }

                    canvasDeclared = true;
                    // Remembered so the VIZ toggle knows whether the canvas is viz-owned (reopen to flip).
                    vizOwnsCanvasResult = vizOwnsCanvas;
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
                _playerNDIAudioOutputs.Clear();
                foreach (var line in lines)
                {
                    // Carrier-audio lines: NDI senders and ARMED file-record sessions both expose a
                    // borrowed audio sink keyed by a stable device id the routes below resolve.
                    var carrierKey = line.Definition switch
                    {
                        NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly =>
                            NDIAudioDeviceId(line.Definition.Id),
                        FileOutputDefinition or LiveStreamOutputDefinition => FileAudioDeviceId(line.Definition.Id),
                        _ => null,
                    };
                    if (carrierKey is null)
                        continue;
                    if (_outputs.AcquireAudioOutputForLine(line.Definition.Id) is not { } audio)
                        continue;
                    _playerNDIAudioOutputs[carrierKey] = audio;
                    _playerAcquiredAudioLines.Add(line.Definition.Id);
                }

                // Route audio to the deck's selected device(s) (on the UI thread - reads deck observable state).
                audioRoutes = BuildDeckShowAudioRoutes(lines);
            });

            // NXT-21: await the load instead of the sync-blocking LoadDocument - this runs on the UI thread,
            // and blocking it on the session dispatcher turns any dispatcher stall into a whole-app freeze.
            await _playerShowSession.LoadDocumentAsync(
                MediaPlayerShowMapper.ToShowDocument(mediaPath, hasVideo, audioRoutes, canvas.Width, canvas.Height,
                    (item as FilePlaylistItem)?.Subtitles
                        ?? (item as YouTubePlaylistItem)?.Subtitles,
                    // Loop is a modifier on unattended progression: it only loops the current item when
                    // Auto-Advance is also on. With Auto-Advance off the item plays once and stops.
                    loop: IsLooping && AutoAdvancePlaylist,
                    // The operator's audio-track choice from the Properties dialog (null = automatic). File items
                    // only; live/YouTube sources have no container multi-track selection here.
                    audioStreamIndex: (item as FilePlaylistItem)?.AudioTrackIndex,
                    // The compositor runs at the source (or preset) rate, not a hardcoded 30 - so 50/60 fps
                    // content is composited frame-for-frame instead of being decimated to 30.
                    canvasFrameRateNum: canvasFps.Num,
                    canvasFrameRateDen: canvasFps.Den,
                    // VIZ on an audio-only track: declare the canvas anyway so the visualizer surface
                    // has something to render onto (the clip itself stays audio-only).
                    includeCanvas: canvasDeclared),
                // Deck always rebuilds the composition per track (preservation reverted - it wedged the
                // dispatcher when the projectM pump stalled; see the composition-preservation notes).
                preserveMatchingCompositions: false).ConfigureAwait(true);
            await _playerShowSession.FireCueAsync(MediaPlayerShowMapper.PlayerCueId).ConfigureAwait(true);
            var openedSnapshot = _playerShowSession.Snapshot()
                .FirstOrDefault(s => s.GroupId == ShowSession.DefaultGroup);

            _playerShowCanvas = canvas; // the HOLD top-layer renders at this canvas size
            _playerShowHasVideo = hasVideo;
            _playerShowVizOwnsCanvas = vizOwnsCanvasResult;

            // UI thread: flip the deck into the playing state (observable-property writes).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowSessionActive = true;
                ExitWaitingForSource();
                MediaFilePath = (item as FilePlaylistItem)?.Path;
                IsMediaLoaded = true;
                LoadState = PlayerLoadState.Ready;
                IsPlaying = true;
                StatusMessage = null;
                LastLoadError = null;
                if (!_audioMatrixSourceChannelsExplicit && openedSnapshot is { AudioChannels: > 0 })
                    SetAudioMatrixSourceChannels(openedSnapshot.AudioChannels, explicitValue: false, resize: true);
                StartShowSessionPoll();
                // Kick the scrubber waveform explicitly. The engine path starts it post-arc (OpenOrReload's tail),
                // but the ShowSession path returns before that, and OnMediaFilePathChanged bails while a transport
                // is busy - so without this the waveform intermittently never loads on the deck. Safe/idempotent:
                // StartWaveformExtraction cancels any in-flight run and no-ops for a null/NDI (non-file) path.
                // A prepared YouTube item's cached asset IS a local file, so the scrubber waveform works there too.
                StartWaveformExtraction(item switch
                {
                    FilePlaylistItem f => f.Path,
                    YouTubePlaylistItem yt => HaPlayPlaybackHelpers.TryGetPreparedYouTubeAssetPath(yt),
                    _ => null,
                });
                UpdateNoOutputWarning(); // opened with no output routed → play to nothing + warn
                // HOLD survives track changes: the new document replaced the composition (and with it the
                // hold top-layer), so re-cover the fresh canvas when the toggle is on.
                if (HoldFallbackVideo)
                    _ = ApplyShowSessionHoldImageAsync();
            });

            // Visualizer re-attach (continuous mode): the reload rebuilt the composition, unhooking the
            // visualizer's surface + tap - but NOT the source: the deck owns it (disposeSourceOnRemove:
            // false) and its offscreen renderer kept running through the swap. Re-attaching the SAME
            // source gives the fresh composition a blit surface that picks the stream up mid-flow, so the
            // visuals are continuous across the track change. The session query guards against
            // double-attach in case a future path ever carries the slot over.
            if (VisualizerEnabled)
            {
                var alreadyAttached = await _playerShowSession
                    .HasCompositionVisualizerAsync(MediaPlayerShowMapper.PlayerCompositionId).ConfigureAwait(true);
                if (!alreadyAttached)
                    await ApplyShowSessionVisualizerAsync().ConfigureAwait(true);
            }

            ShowLog.LogInformation("MediaPlayer: playing through the per-player ShowSession (convergence default).");
            return true;
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession open failed");
            await ShowSessionStopAsync().ConfigureAwait(true);
            if (item.IsLive && GetRetrySeconds(item) > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    EnterWaitingForSource(item, ex.GetBaseException().Message);
                    SyncIdleSlate();
                });
                return true; // handled by the ShowSession retry path; do not silently fall back to the old engine
            }
            return false;
        }
    }

    // The descriptor-URI builders live in HaPlayPlaybackHelpers so the CUE mapper shares them (a cue-fired
    // live input must keep its per-item options exactly like a deck-fired one); these thin forwards keep the
    // deck's call sites and tests stable.
    internal static string BuildNDIInputUri(NDIInputPlaylistItem item) =>
        HaPlayPlaybackHelpers.BuildNDIInputUri(item);

    internal static string BuildPortAudioInputUri(PortAudioInputPlaylistItem item) =>
        HaPlayPlaybackHelpers.BuildPortAudioInputUri(item);

    // Pause/resume command in flight (like _seekArcRunning for seeks): resume flips IsPlaying=true
    // immediately, but the session's Play() prefills + starts the audio hardware before the clip clock
    // runs - the lock-free snapshot shows IsRunning=false with an unchanged generation for that whole
    // window. Without this guard the 250 ms poll read those ticks as a natural end and auto-advanced;
    // with a single-item playlist the "next" item is the same item, so resume restarted from zero.
    private int _pauseResumeInFlight;

    private async Task ShowSessionPauseAsync(bool paused)
    {
        IsPlaying = !paused;
        if (_playerShowSession is not { } session)
            return;
        Interlocked.Increment(ref _pauseResumeInFlight);
        try
        {
            await session.SetPausedAsync(paused).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Decrement(ref _pauseResumeInFlight);
        }
    }

    private Task ShowSessionSeekAsync(TimeSpan position) =>
        _playerShowSession?.SeekAsync(position) ?? Task.CompletedTask;

    /// <summary>Deck output-line health under the ShowSession path (engine-parity for the outputs-panel LEDs):
    /// sums this player's ShowSession composition throughput for a video line it drives AND the audio-pump
    /// chunks of any deck route to the line's device (a PortAudio line's backend device id, or an NDI line's
    /// carrier-audio key), scoring a combined state like the engine's
    /// <see cref="Playback.OutputLineHealthEvaluator"/> - so an audio-only deck line lights up too (it used to
    /// report Idle because this probe was gated on the deck's VIDEO lines). Returns null when this deck isn't
    /// ShowSession-driving the line at all, so the caller falls back to the engine probe. Lock-free
    /// (composition stats + audio-pump snapshot), no marshaling.</summary>
    // Previous cumulative counters per line for the health poll's recent-rate scoring (UI-thread only:
    // the 1 Hz health DispatcherTimer is the sole caller).
    private readonly Dictionary<Guid, long> _lineHealthPrevVideoLate = new();
    private readonly Dictionary<Guid, long> _lineHealthPrevAudioDropped = new();

    internal OutputLineHealthEvaluator.LineHealthMetrics? TryGetShowSessionLineHealthMetrics(Guid outputLineId)
    {
        if (!ShowSessionActive || _playerShowSession is not { } session)
            return null;

        long videoSubmitted = 0;
        long videoLateRecent = 0;
        var driven = false;
        if (_playerAcquiredLines.Contains(outputLineId)
            && session.GetCompositionStats(MediaPlayerShowMapper.PlayerCompositionId) is { } stats
            && stats.FramesSubmitted > 0)
        {
            driven = true;
            videoSubmitted = stats.FramesSubmitted;
            // Pump overruns only - composite ticks over the canvas budget, i.e. the output genuinely ran
            // late. SlotOverflowFrames is deliberately excluded: master-aligned slots supersede a stale
            // pending frame as part of normal pacing, so counting it reported a steady stream of phantom
            // "drops" (and latched the LED red via the old lifetime >120 rule) on a perfectly smooth deck.
            videoLateRecent = OutputLineHealthEvaluator.RecentDelta(
                _lineHealthPrevVideoLate, outputLineId, stats.PumpOverruns);
        }

        // Audio: reverse-map the line to the device id the deck routes to - PortAudio lines by their backend
        // device, NDI lines by their carrier-audio key - and sum the active clip's pump chunks for it.
        long audioEnqueued = 0;
        long audioDropped = 0;
        var deviceId = _outputs.DefinitionsSnapshot
                .OfType<PortAudioOutputDefinition>()
                .FirstOrDefault(d => d.Id == outputLineId)?.EffectiveAudioBackendDeviceId
            ?? (_playerNDIAudioOutputs.ContainsKey(NDIAudioDeviceId(outputLineId))
                ? NDIAudioDeviceId(outputLineId)
                : _playerNDIAudioOutputs.ContainsKey(FileAudioDeviceId(outputLineId))
                    ? FileAudioDeviceId(outputLineId)
                    : null);
        if (deviceId is not null
            && session.TryGetActiveAudioPumpStats(deviceId, out var audio)
            && audio.Enqueued > 0)
        {
            driven = true;
            audioEnqueued = audio.Enqueued;
            audioDropped = OutputLineHealthEvaluator.RecentDelta(
                _lineHealthPrevAudioDropped, outputLineId, audio.Dropped);
        }

        if (!driven)
            return null;

        var state = OutputLineHealthEvaluator.Score(videoLateRecent, audioDropped);
        return new OutputLineHealthEvaluator.LineHealthMetrics(
            state, videoSubmitted, videoLateRecent, 0, 0, audioEnqueued, audioDropped);
    }

    /// <summary>Builds the deck's initial ShowSession audio routes from its selected PortAudio output bindings, so
    /// a deck on the ShowSession path plays audio on the operator-SELECTED device(s) (with the binding's channel
    /// map + effective gain) instead of the default device - the core parity fix for the flipped default.
    /// Runs on the UI thread (reads deck observable state).</summary>
    /// <remarks>One device route per selected audio line with a full per-cell gain matrix + the compound
    /// (master × per-output) gain. PortAudio lines route to their backend device; audio-capable NDI lines route
    /// to their carrier's audio side (its device id resolves through <see cref="BuildNDIAudioLease"/>, populated
    /// on open).</remarks>
    private IReadOnlyList<ShowClipAudioRoute> BuildDeckShowAudioRoutes(IReadOnlyList<OutputLineViewModel> lines)
    {
        var routes = new List<ShowClipAudioRoute>();
        foreach (var line in lines)
        {
            if (Outputs.FirstOrDefault(b => b.Line == line) is not { } binding)
                continue;
            var declaredCells = binding.Matrix.Cells;
            var gainCells = BuildDeckGainMatrixCells(declaredCells.Select(c =>
            {
                var (trimDb, trimMuted) = InputTrimValues(c.InputChannel);
                return (c.InputChannel, c.OutputChannel, c.GainDb, c.Muted, trimDb, trimMuted);
            }).ToArray());
            if (declaredCells.Count > 0 && gainCells.Count == 0)
                continue; // declared matrix but every cell is muted/at floor → silent line, no route
            var matrix = declaredCells.Count == 0 ? new[] { 0, 1 } : null; // unsized grid → stereo default
            int? matrixOutputs = declaredCells.Count == 0 ? null : declaredCells.Max(c => c.OutputChannel) + 1;

            ShowClipAudioRoute Route(string? deviceId, float gain, int? sampleRate) => new(
                deviceId, matrix, gain, sampleRate)
            {
                MatrixCells = gainCells,
                MatrixOutputChannels = matrixOutputs,
            };

            switch (line.Definition)
            {
                case PortAudioOutputDefinition pa:
                    if (pa.EffectiveAudioBackendDeviceId is { } paDevice)
                        _playerPaDeviceRates[paDevice] = pa.SampleRate; // factory rate-fallback lookup
                    routes.Add(Route(pa.EffectiveAudioBackendDeviceId, CompoundEnvelope(binding),
                        pa.SampleRate > 0 ? pa.SampleRate : null));
                    break;
                case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly:
                    // Only route to an NDI line whose carrier audio was acquired on open; the device id maps back
                    // to that borrowed carrier in the audio-output factory.
                    var ndiDeviceId = NDIAudioDeviceId(line.Definition.Id);
                    if (_playerNDIAudioOutputs.ContainsKey(ndiDeviceId))
                        routes.Add(Route(ndiDeviceId, CompoundEnvelope(binding),
                            nd.AudioSampleRate > 0 ? nd.AudioSampleRate : null));
                    break;
                case FileOutputDefinition or LiveStreamOutputDefinition:
                    // Armed encode line (recording or live stream): its primary encode track was acquired
                    // on open like an NDI carrier.
                    var fileDeviceId = FileAudioDeviceId(line.Definition.Id);
                    if (_playerNDIAudioOutputs.ContainsKey(fileDeviceId))
                        routes.Add(Route(fileDeviceId, CompoundEnvelope(binding), sampleRate: null));
                    break;
            }
        }
        return routes;
    }

    /// <summary>Stable route device id for an NDI line's carrier audio - the key shared by
    /// <see cref="BuildDeckShowAudioRoutes"/> (emits it), the <c>_playerNDIAudioOutputs</c> map (populated on
    /// open), and <see cref="BuildNDIAudioLease"/> (resolves it in the audio-output factory).</summary>
    private static string NDIAudioDeviceId(Guid lineId) => $"ndi-audio:{lineId}";

    /// <summary>Stable route device id for an armed file-record line's primary encode track - same
    /// carrier-audio pattern as <see cref="NDIAudioDeviceId"/> (emitted by the routes, resolved by the
    /// audio-output factory via the borrowed-carrier map).</summary>
    private static string FileAudioDeviceId(Guid lineId) => $"file-audio:{lineId}";

    /// <summary>Maps a route device id back to its output LINE (for the line's audio-effect inserts):
    /// carrier ids ("ndi-audio:{id}" / "file-audio:{id}") parse directly; PortAudio backend device ids
    /// reverse-look-up through the definitions snapshot. Null for default-device routes.</summary>
    private Guid? TryResolveLineIdForAudioDevice(string deviceId)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["ndi-audio:", "file-audio:"])
        {
            if (deviceId.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParse(deviceId[prefix.Length..], out var carrierLineId))
            {
                return carrierLineId;
            }
        }

        return _outputs.DefinitionsSnapshot
            .OfType<PortAudioOutputDefinition>()
            .FirstOrDefault(d => string.Equals(d.EffectiveAudioBackendDeviceId, deviceId, StringComparison.Ordinal))
            ?.Id;
    }

    /// <summary>Stable composition-output id for a driven output line - shared by the video-output factory (fire
    /// path) and hot add/remove, so a single line's composition output is attached/detached by a fixed id
    /// (index-based ids would shift when a line is added or removed).</summary>
    private static string CompositionOutputId(Guid lineId) =>
        $"{MediaPlayerShowMapper.PlayerCompositionId}_line_{lineId:N}";

    /// <summary>The audio-output factory. Wraps every resolved output in a <see cref="MeteringAudioOutput"/>
    /// tap (registered for the deck's VU poll, unregistered via the lease release hook) around
    /// <see cref="BuildDeckAudioLeaseCore"/>. The wrapper is disposal-transparent, so the session's ownership
    /// semantics (<c>DisposeOutputOnRuntimeDispose</c>) are unchanged.</summary>
    private ClipAudioOutputLease? BuildDeckAudioLease(string deviceId, AudioFormat format)
    {
        if (BuildDeckAudioLeaseCore(deviceId, format) is not { } lease)
            return null;
        // Line audio effects (Phase 4/5 inserts) run before the meter so the VU shows the processed
        // signal. Ownership (review H4): for a SESSION-OWNED terminal (DisposeOutputOnRuntimeDispose)
        // the wrapper chain is disposal-transparent - session disposes meter → effects → device. For a
        // BORROWED carrier the session never disposes the chain, so the Release hook retires the
        // wrappers itself (the effect wrapper holds disposeInner:false and stops at the carrier).
        var effectWrapped = TryResolveLineIdForAudioDevice(deviceId) is { } effectLineId
            ? _outputs.WrapAudioEffectsForLine(effectLineId, lease.Output, disposeInner: lease.DisposeOutputOnRuntimeDispose)
            : lease.Output;
        var tap = MeteringAudioOutput.Wrap(effectWrapped);
        RegisterMeterTap(tap);
        return lease with
        {
            Output = tap,
            Release = () =>
            {
                UnregisterMeterTap(tap);
                if (!lease.DisposeOutputOnRuntimeDispose && !ReferenceEquals(effectWrapped, lease.Output))
                {
                    // Borrowed terminal WITH effects: retire OUR wrappers - meter → effect chain, which
                    // holds disposeInner:false and stops at the carrier. Without effects the meter is a
                    // stateless shell and must NOT be disposed (it is disposal-transparent and would
                    // reach the borrowed carrier).
                    try { tap.Dispose(); }
                    catch { /* best effort */ }
                }

                lease.Release?.Invoke();
            },
        };
    }

    /// <summary>The audio-output factory body. NDI route device ids resolve to the borrowed carrier audio held
    /// since open (wrapped in a per-fire resampler only when the carrier's format differs). Every other device id
    /// is a PortAudio route: the deck creates it here so a device that rejects the clip's mix rate (a fixed-rate
    /// JACK graph, mismatched hardware) can be opened at the LINE's configured rate behind an egress resampler -
    /// without this, routing a device to an already-playing clip whose mix rate the device can't open simply
    /// failed (the mid-play "route → no output" bug). Safe on the session thread.</summary>
    private ClipAudioOutputLease? BuildDeckAudioLeaseCore(string deviceId, AudioFormat format)
    {
        if (_playerNDIAudioOutputs.TryGetValue(deviceId, out var carrierAudio))
        {
            if (carrierAudio.Format.SampleRate == format.SampleRate && carrierAudio.Format.Channels == format.Channels)
                // Format matches → route straight into the carrier (borrowed: released by the deck on stop).
                return new ClipAudioOutputLease(carrierAudio, DisposeOutputOnRuntimeDispose: false);
            // Mismatch → a per-fire resampler adapts the clip to the carrier. Its Dispose frees only the resampler
            // (NOT the borrowed carrier), so the session may own it (DisposeOutputOnRuntimeDispose: true).
            return new ClipAudioOutputLease(
                ResamplingAudioOutput.Wrap(carrierAudio, format), DisposeOutputOnRuntimeDispose: true);
        }

        if (MediaRuntime.Registry.AudioBackends.FirstOrDefault() is not { } backend)
            return null; // no backend - let the session report the failure its own way

        try
        {
            // The common case: the device accepts the clip's mix rate directly (session-owned output).
            return new ClipAudioOutputLease(backend.CreateOutput(deviceId, format), DisposeOutputOnRuntimeDispose: true);
        }
        catch
        {
            // The device rejected the clip's mix rate. Open it at ITS rate (the line's configured rate, else
            // the device default) and egress-resample the router format into it. The session owns the wrapper;
            // the created device is released via the lease hook (ResamplingAudioOutput never owns its inner).
            var deviceRate = _playerPaDeviceRates.GetValueOrDefault(deviceId, 0);
            if (deviceRate <= 0)
                deviceRate = (int)Math.Round(backend.EnumerateOutputDevices()
                    .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))?.DefaultSampleRate ?? 0);
            if (deviceRate <= 0 || deviceRate == format.SampleRate)
                throw; // no viable alternative rate - surface the real open failure
            var device = backend.CreateOutput(deviceId, new AudioFormat(deviceRate, format.Channels));
            return new ClipAudioOutputLease(
                ResamplingAudioOutput.Wrap(device, format),
                DisposeOutputOnRuntimeDispose: true,
                Release: () => (device as IDisposable)?.Dispose());
        }
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

    /// <summary>Pure: the deck's composition-canvas raster (size + rate). The compositor is sized like a media
    /// player - <em>autosized to the media</em> - not like a cue's fixed program raster:
    /// <list type="bullet">
    /// <item><see cref="PlayerOutputPreset.AsSource"/> (default) → the source's own resolution/rate, so the media
    /// fills the canvas 1:1 and each OUTPUT letterboxes it into its physical display (the fix for differing-aspect
    /// media covering the screen).</item>
    /// <item>a fixed preset (1080p60 / 720p60 / Custom) → that preset's raster, into which the clip placement
    /// letterboxes the source.</item>
    /// </list>
    /// Falls back to the largest driven output resolution (then 1080p) when the source resolution is unknown -
    /// a live NDI/capture input, or a probe miss - since there is no media raster to match yet.</summary>
    internal static (int Width, int Height, int FpsNum, int FpsDen) ResolveDeckCanvas(
        PlayerOutputPreset preset,
        int customWidth,
        int customHeight,
        int sourceWidth,
        int sourceHeight,
        int sourceFpsNum,
        int sourceFpsDen,
        IEnumerable<(int Width, int Height)> outputResolutions)
    {
        var sourceRate = sourceFpsNum > 0 && sourceFpsDen > 0
            ? new Rational(sourceFpsNum, sourceFpsDen)
            : default;

        // Fixed preset: the canvas is the preset raster; the clip placement letterboxes the source into it.
        if (preset != PlayerOutputPreset.AsSource
            && OutputPresetFormats.TryResolve(preset, sourceRate, out var target, customWidth, customHeight))
        {
            return (target.Width, target.Height, target.FrameRate.Numerator, target.FrameRate.Denominator);
        }

        // AsSource with a known media raster: match it exactly (compositor == source; output letterboxes).
        if (sourceWidth > 0 && sourceHeight > 0)
        {
            return sourceRate.Numerator > 0
                ? (sourceWidth, sourceHeight, sourceRate.Numerator, sourceRate.Denominator)
                : (sourceWidth, sourceHeight, 30, 1);
        }

        // Unknown source raster (live input / probe miss): fall back to the driven outputs, keeping the source
        // rate when we happen to know it.
        var (w, h) = ResolveDeckCanvasSize(outputResolutions);
        return sourceRate.Numerator > 0
            ? (w, h, sourceRate.Numerator, sourceRate.Denominator)
            : (w, h, 30, 1);
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

    /// <summary>Pure conversion of the deck's per-cell dB matrix (including input trim) into the linear cells
    /// carried by <see cref="ShowClipAudioRoute"/>. Unlike <see cref="BuildDeckChannelMatrix"/>, this preserves
    /// several input channels mixed into the same output and each cell's independent gain.</summary>
    internal static IReadOnlyList<ShowAudioMatrixCell> BuildDeckGainMatrixCells(
        IReadOnlyList<(int Input, int Output, double GainDb, bool Muted, double InputTrimDb, bool InputMuted)> cells)
    {
        var result = new List<ShowAudioMatrixCell>(cells.Count);
        foreach (var cell in cells)
        {
            var db = cell.GainDb + cell.InputTrimDb;
            if (cell.Muted || cell.InputMuted || db <= AudioMatrixDefaults.MutedFloorDb)
                continue;
            result.Add(new ShowAudioMatrixCell(
                cell.Input, cell.Output, (float)Math.Pow(10.0, Math.Clamp(db, -80.0, 24.0) / 20.0)));
        }
        return result;
    }

    /// <summary>Live-re-apply the deck's current audio routing (per-line channel maps + compound gains + mutes)
    /// to the RUNNING ShowSession clip, so matrix / gain / mute edits take effect DURING playback - the
    /// ShowSession analog of the engine deck's <c>TrySetOutputMatrix</c> ride. No-op off the ShowSession path
    /// (the engine methods handle their own case). Fire-and-forget: it hops the session dispatcher; a
    /// stable-composition edit (gain / per-cell route / output-mute) is applied in place, while a change that
    /// adds/removes a whole route (a line selected/deselected or all-cells-muted) is deferred by the framework
    /// to the next fire - see <see cref="ShowSession.ApplyActiveAudioRoutesAsync"/>.</summary>
    private void ReapplyDeckAudioToShowSessionIfActive()
    {
        if (!ShowSessionActive || _playerShowSession is not { } session)
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

    /// <summary>True while a file is playing through the per-player ShowSession - the deck's hot output add/remove
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
        // returns false for an audio-only source (no composition) - release rather than hold the lease.
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

        // Carrier audio (NDI sender / armed file-record session): hold it so the rebuild's route can reach it.
        var hotCarrierKey = line.Definition switch
        {
            NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.VideoOnly } => NDIAudioDeviceId(lineId),
            FileOutputDefinition or LiveStreamOutputDefinition => FileAudioDeviceId(lineId),
            _ => null,
        };
        if (hotCarrierKey is not null
            && !_playerNDIAudioOutputs.ContainsKey(hotCarrierKey)
            && _outputs.AcquireAudioOutputForLine(lineId) is { } audio)
        {
            _playerNDIAudioOutputs[hotCarrierKey] = audio;
            _playerAcquiredAudioLines.Add(lineId);
        }

        await RebuildDeckShowSessionAudioAsync().ConfigureAwait(true);
        UpdateNoOutputWarning();
    }

    /// <summary>Hot-removes an output LINE from the RUNNING ShowSession deck (the ShowSession analog of the
    /// engine's <c>TryRemoveOutput</c>): detaches its video from the live composition, REBUILDS the clip's audio
    /// outputs to drop its route (the clip keeps playing on its discard sink even at zero outputs), THEN releases
    /// the physical outputs - the order matters: releasing a sink before its router route is gone would dangle.
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

        // 2) Drop the carrier (NDI / file-record) from the audio map so the rebuild excludes its route
        //    (don't release it yet).
        var hadNDIAudio = _playerNDIAudioOutputs.Remove(NDIAudioDeviceId(lineId))
                          | _playerNDIAudioOutputs.Remove(FileAudioDeviceId(lineId));
        if (hadNDIAudio)
            _playerAcquiredAudioLines.Remove(lineId);

        // 3) Rebuild the clip's audio outputs from the REMAINING routes - removes the dead route in the router
        //    before we release its sink; the clip keeps playing (its discard sink stays) even down to zero.
        await RebuildDeckShowSessionAudioAsync().ConfigureAwait(true);

        // 4) Now the physical outputs carry no route/composition reference - safe to release.
        if (hadVideo)
            _outputs.ReleaseVideoOutputForLine(lineId);
        if (hadNDIAudio)
            _outputs.ReleaseAudioOutputForLine(lineId);

        UpdateNoOutputWarning();
    }

    /// <summary>Rebuilds the RUNNING ShowSession clip's audio outputs from the deck's current routes - the
    /// count-change path (hot add/remove) that <see cref="ReapplyDeckAudioToShowSessionIfActive"/>'s in-place
    /// re-apply defers. Keeps playback running (the clip's discard sink stays) even when no output is routed.</summary>
    private async Task RebuildDeckShowSessionAudioAsync()
    {
        if (!ShowSessionActive || _playerShowSession is not { } session)
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

    /// <summary>Applies (HOLD on + image set) or clears the deck's hold image on the RUNNING per-player
    /// ShowSession: the image renders letterboxed at the composition canvas size and is held in the top-most
    /// full-canvas layer (<see cref="ShowSession.SetCompositionTestPatternAsync"/> - the same held-top-layer
    /// mechanism the calibration grid uses), so every fanned-out output shows it while audio keeps playing.
    /// This is the ShowSession replacement for the engine's <c>LogoFallbackVideoOutput</c> hold: under the
    /// flipped default the engine session is null, so the old wiring made the HOLD button a silent no-op
    /// during playback. Audio-only media has no composition (returns false harmlessly) - the idle slate
    /// covers that case (see <c>SyncIdleSlate</c>). UI thread (reads deck observable state).</summary>
    internal async Task ApplyShowSessionHoldImageAsync()
    {
        if (!ShowSessionActive || _playerShowSession is not { } session)
            return;
        try
        {
            if (HoldFallbackVideo && !string.IsNullOrWhiteSpace(FallbackImagePath) && File.Exists(FallbackImagePath))
            {
                var canvasFormat = new VideoFormat(
                    _playerShowCanvas.Width, _playerShowCanvas.Height, PixelFormat.Bgra32, new Rational(30, 1));
                var frame = FallbackImageLoader.TryBuildHoldCpuFrame(canvasFormat, FallbackImagePath!);
                if (frame is null)
                {
                    ShowLog.LogWarning("MediaPlayer: hold image '{Path}' could not be loaded/converted.", FallbackImagePath);
                    return;
                }

                await session.SetCompositionTestPatternAsync(
                    MediaPlayerShowMapper.PlayerCompositionId, frame).ConfigureAwait(true);
            }
            else
            {
                await session.SetCompositionTestPatternAsync(
                    MediaPlayerShowMapper.PlayerCompositionId, null).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession hold image apply/clear");
        }
    }

    /// <summary>Surfaces a banner when a loaded/playing deck has NO output routed - playback continues (to
    /// nothing) rather than stopping, matching a hardware player. Cleared once any output is routed again.</summary>
    private void UpdateNoOutputWarning()
    {
        if (IsMediaLoaded && SelectedOutputLines().Count == 0)
            StatusMessage = "No output routed - the deck is still playing. Route an output to see/hear it.";
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
        // idle logo slate (FallbackImagePath) - the same idle fallback the engine path shows when it stops.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StopShowSessionPoll();
            foreach (var held in _playerAcquiredLines)
                _outputs.ReleaseVideoOutputForLine(held);
            _playerAcquiredLines.Clear();
            _playerVideoOutputs = new(StringComparer.Ordinal);
            _playerShowVizOwnsCanvas = false;
            foreach (var held in _playerAcquiredAudioLines)
                _outputs.ReleaseAudioOutputForLine(held);
            _playerAcquiredAudioLines.Clear();
            _playerNDIAudioOutputs.Clear();
            _playerPaDeviceRates.Clear();
            ShowSessionActive = false;
            IsPlaying = false;
            IsMediaLoaded = false;
            CurrentPlayingItem = null; // deck back to idle - clear the now-playing markers
            CurrentPosition = TimeSpan.Zero;
            SyncIdleSlate();
        });
    }

    private void StartShowSessionPoll()
    {
        _showSessionEndConfirmTicks = 0;
        _showSessionLastTimelineGeneration = -1;
        _naturalEndHandled = false; // fresh track - the advance-once gate re-arms (review M4)
        _playerShowPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerShowPoll.Tick -= OnShowSessionPollTick;
        _playerShowPoll.Tick += OnShowSessionPollTick;
        _playerShowPoll.Start();
    }

    private void StopShowSessionPoll()
    {
        _playerShowPoll?.Stop();
        PeakLevelDb = double.NegativeInfinity;
    }

    private async void OnShowSessionPollTick(object? sender, EventArgs e)
    {
        if (_playerShowSession is null || !ShowSessionActive)
            return;
        try
        {
            PollAudioMeters();
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
            // router can keep reporting IsRunning while it waits for data - so this is checked separately). A
            // live source NEVER playlist-auto-advances; return the deck to idle.
            if (snap.LiveSourceDisconnected && IsPlaying)
            {
                StopShowSessionPoll();
                await ShowSessionStopAsync().ConfigureAwait(true);
                return;
            }

            // Natural end: auto-advance to the next playlist item when enabled (honoring the tab's
            // shuffle/repeat), else return the deck to idle. Stop the poll first so the (async) advance can't
            // be re-entered by the next tick.
            //
            // A coordinated seek transiently pauses the clip (IsRunning=false) while it reseeks the demux -
            // sometimes 100ms+ when the audio pump is slow to idle. Without discrimination the poll mistakes
            // that transient for end-of-track and tears the deck down ("freezes then stops" after a few seeks).
            // Guard on it two ways: skip while a seek/scrub is in flight, AND require the stopped state to
            // PERSIST across two ticks (a seek's pause is far shorter than the 250ms interval, so it can never
            // span two ticks; a genuine end does).
            if (ConfirmShowSessionEnded(snap.IsRunning, IsPlaying, IsScrubbing,
                    _seekArcRunning || Volatile.Read(ref _pauseResumeInFlight) > 0,
                    snap.TimelineGeneration, ref _showSessionLastTimelineGeneration,
                    ref _showSessionEndConfirmTicks))
            {
                await HandleShowSessionNaturalEndAsync("poll").ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowLog.LogTrace("MediaPlayer: ShowSession poll: {Message}", ex.Message);
        }
    }

    // Review M4: single advance-once gate shared by the event path (ClipNaturallyEnded, primary) and
    // the poll path (fallback) - both run on the UI thread, so a plain bool suffices. Reset when the
    // poll (re)starts after each successful open.
    private bool _naturalEndHandled;
    private bool _naturalEndHooked; // ClipNaturallyEnded subscribed once per player session
    // Monotonic play generation: bumped at the START of every open. A ClipNaturallyEnded event captures
    // the generation when RAISED (session dispatcher) and is dropped at UI delivery if a newer open
    // started in between - a stale end event for the PREVIOUS track must never advance again (the
    // double-play regression: track N ends → advance to N+1 → stale N event lands after N+1's open
    // completed and the boolean gate was re-armed → N+2 started on top of N+1).
    private int _playGeneration;

    /// <summary>The ONE natural-end/advance entry (review M4). Primary trigger: the session's
    /// ClipNaturallyEnded event (precise, raised by the end-of-clip machinery, never for loops or
    /// operator stops). Fallback: the poll's confirmed-stopped heuristic. Idempotent per track.</summary>
    private async Task HandleShowSessionNaturalEndAsync(string source)
    {
        if (_naturalEndHandled || !ShowSessionActive)
            return;
        _naturalEndHandled = true;
        ShowLog.LogDebug("MediaPlayer: natural end via {Source} - advancing", source);
        StopShowSessionPoll();
        // Loop-current wins over playlist auto-advance. A loop active at OPEN time never reaches this
        // (the clip's Loop flag restarts it seamlessly inside the session); this replay covers loop
        // toggled ON mid-play, when the running clip predates the flag.
        if (IsLooping && AutoAdvancePlaylist && _currentPlaylistItem is { } current)
            await PlayPlaylistItemAsync(current).ConfigureAwait(true);
        else if (AutoAdvancePlaylist && TryGetAutoAdvanceItem(out var next))
            await PlayPlaylistItemAsync(next).ConfigureAwait(true);
        else
            await ShowSessionStopAsync().ConfigureAwait(true);
    }

    /// <summary>Pure end-of-track decision for the deck poll. A coordinated seek transiently pauses the clip
    /// (IsRunning=false) while it reseeks the demux, so this treats "stopped while playing" as the true end
    /// only when NOT mid-seek/scrub/pause-resume AND the stopped state has PERSISTED across two poll ticks - a
    /// seek's pause is far shorter than the 250 ms poll interval, so it can never span two ticks; a genuine end
    /// does. <paramref name="seekInFlight"/> also covers a resume in flight: the session's Play() prefills +
    /// starts audio hardware before the clip clock runs, so IsRunning stays false (with IsPlaying already true)
    /// long enough to span ticks - that window must never read as an end.
    /// A change of <paramref name="timelineGeneration"/> (the session's NXT-04 discontinuity signal: any
    /// seek/pause/resume/clip swap, including ones the deck did not initiate - e.g. a control-surface seek)
    /// authoritatively restarts the window, however long that discontinuity's transient pause lasts.
    /// <paramref name="confirmTicks"/> accumulates consecutive qualifying ticks and is reset otherwise.</summary>
    internal static bool ConfirmShowSessionEnded(
        bool isRunning, bool isPlaying, bool isScrubbing, bool seekInFlight,
        int timelineGeneration, ref int lastTimelineGeneration, ref int confirmTicks)
    {
        if (timelineGeneration != lastTimelineGeneration)
        {
            lastTimelineGeneration = timelineGeneration;
            confirmTicks = 0;
            return false;
        }

        if (!isRunning && isPlaying && !isScrubbing && !seekInFlight)
            return ++confirmTicks >= 2;
        confirmTicks = 0;
        return false;
    }
}
