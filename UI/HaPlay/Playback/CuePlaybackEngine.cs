using Avalonia.Threading;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.NDI;
using S.Media.Playback;
using S.Media.PortAudio;

namespace HaPlay.Playback;

/// <summary>
/// Cue-side playback runtime. Manages N concurrent media cues plus two pools of shared resources:
/// <see cref="CueCompositionRuntime"/> per active composition (shared video mixer + acquired
/// video outputs) and <see cref="ClipAudioOutputRuntime"/> per active audio-capable output line
/// (shared audio router so N cues' audio mix into one device). Completely independent of
/// MediaPlayer tabs — only shares the <see cref="OutputManagementViewModel"/> registry of
/// physical output lines.
/// </summary>
public sealed class CuePlaybackEngine : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CuePlaybackEngine");
    private static readonly TimeSpan BoundedPauseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BoundedDisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SoftStopFadeDuration = TimeSpan.FromMilliseconds(750);

    private readonly OutputManagementViewModel _outputs;
    private readonly CuePlayerViewModel _cuePlayer;
    private readonly object _gate = new();
    private readonly ClipStandbyEngine _standby = new();

    private readonly Dictionary<Guid, ActiveCue> _active = new();
    private readonly Dictionary<Guid, CueCompositionRuntime> _compositions = new();
    private readonly Dictionary<Guid, ClipAudioOutputRuntime> _audioOutputs = new();
    private readonly object _previewGate = new();
    private CuePreviewSession? _preview;

    public CuePlaybackEngine(OutputManagementViewModel outputs, CuePlayerViewModel cuePlayer)
    {
        _outputs = outputs;
        _cuePlayer = cuePlayer;
        _standby.StandbyStatesChanged += OnStandbyStatesChanged;
    }

    /// <summary>Raised on the UI thread when a cue's media ends naturally (file reached duration).
    /// The cue VM listens to drive <c>AutoFollow</c> for the most-recently-fired cue.</summary>
    public event Func<Task>? NaturalEnd;

    /// <summary>Raised on the UI thread immediately after a cue begins playing — VM listens to
    /// mark the row's status indicator as <c>Current</c>. Multiple cues can be active at once
    /// (a <c>FireAllSimultaneously</c> group fires N together), so the singular
    /// <c>CurrentCueNode</c> isn't sufficient for the badge state.</summary>
    public event EventHandler<Guid>? CueStarted;

    /// <summary>Raised on the UI thread when a cue stops (natural end, Stop, or Panic).</summary>
    public event EventHandler<Guid>? CueEnded;

    /// <summary>Raised on the UI thread roughly every 150 ms while a cue is active. Carries the
    /// cue id + current position + duration so the Now Playing panel can advance its progress
    /// bars without per-row polling.</summary>
    public event EventHandler<CuePlaybackProgress>? CueProgress;

    /// <summary>Raised when standby preparation changes the set of warmed cue ids.</summary>
    public event Action<IReadOnlyCollection<Guid>>? PreparedCuesChanged;

    /// <summary>Raised when per-cue preparation status changes (idle/preparing/ready/failed +
    /// last failure reason), so the cue UI can show more than a binary warm marker.</summary>
    public event Action<IReadOnlyList<CuePreparationStatus>>? PreparedCueStatesChanged;

    /// <summary>Flags a warmed standby cue as <see cref="PreparedCueState.Stale"/> — its config changed,
    /// so the held decoder may no longer match — until the next pre-roll refresh re-prepares it. No-op
    /// for a cue that isn't currently held warm; only a prepared (ready) entry can go stale.</summary>
    public void MarkPreparedCueStale(Guid cueId) => _standby.MarkStandbyStale(cueId.ToString("N"));

    public async Task UpdateActiveCueVideoPlacementAsync(Guid cueId, int placementIndex, CueVideoPlacement placement)
    {
        if (placementIndex < 0)
            return;

        CueCompositionRuntime.LayerSlot? slot;
        lock (_gate)
        {
            if (!_active.TryGetValue(cueId, out var entry) || entry.Cts.IsCancellationRequested)
                return;
            entry.LayerSlotsByPlacementIndex.TryGetValue(placementIndex, out slot);
        }

        if (slot is null)
            return;

        void Apply()
        {
            try
            {
                slot.UpdatePlacement(placement);
            }
            catch (ObjectDisposedException)
            {
                // Cue stopped while a UI edit was in flight.
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "CuePlaybackEngine.UpdateActiveCueVideoPlacementAsync: live placement update failed");
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            await Dispatcher.UIThread.InvokeAsync(Apply);
    }

    public async Task UpdateActiveCueAudioRoutesAsync(Guid cueId, IReadOnlyList<CueAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        ActiveCue? entry;
        lock (_gate)
        {
            if (!_active.TryGetValue(cueId, out entry) || entry.Cts.IsCancellationRequested)
                return;
        }

        void Apply()
        {
            try
            {
                ReconcileActiveAudioRoutes(entry, routes);
            }
            catch (ObjectDisposedException)
            {
                // Cue stopped while a UI edit was in flight.
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "CuePlaybackEngine.UpdateActiveCueAudioRoutesAsync: live audio route update failed");
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            await Dispatcher.UIThread.InvokeAsync(Apply);
    }

    /// <summary>Raised on the UI thread when preview playback ends (natural end, operator stop,
    /// or preview window closed).</summary>
    public event EventHandler<Guid>? PreviewEnded;

    /// <summary>Id of the cue currently held in the transient preview path, if any.</summary>
    public Guid? PreviewingCueId
    {
        get { lock (_previewGate) return _preview?.CueId; }
    }

    public int? PreviewAudioDeviceIndex { get; set; }

    public Func<IReadOnlyCollection<Guid>, Task>? ReleaseConflictingPlayerOutputsAsync { get; set; }

    public async Task<string?> PreviewCueAsync(MediaCueNode cue, CancellationToken ct)
    {
        await StopPreviewAsync().ConfigureAwait(false);

        var (session, err) = await CuePreviewSession.TryOpenAsync(cue, ct, PreviewAudioDeviceIndex).ConfigureAwait(false);
        if (session is null)
            return err ?? "Preview failed.";

        session.CloseRequested += (_, _) => _ = StopPreviewAsync();

        lock (_previewGate)
            _preview = session;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => session.Play());
            _ = WatchPreviewEndAsync(session);
            return null;
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "CuePlaybackEngine.PreviewCueAsync: Play threw");
            await StopPreviewAsync().ConfigureAwait(false);
            return ex.Message;
        }
    }

    /// <summary>Tears down the transient preview session, if any.</summary>
    public async Task StopPreviewAsync()
    {
        CuePreviewSession? session;
        lock (_previewGate)
        {
            session = _preview;
            _preview = null;
        }

        if (session is null) return;

        var cueId = session.CueId;
        try
        {
            await Task.Run(() => session.Dispose()).WaitAsync(BoundedDisposeTimeout);
        }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.StopPreviewAsync"); }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { PreviewEnded?.Invoke(this, cueId); }
            catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: PreviewEnded handler"); }
        });
    }

    /// <summary>Seek an active cue or the current preview to <paramref name="position"/>.</summary>
    public async Task SeekCueAsync(Guid cueId, TimeSpan position)
    {
        CuePreviewSession? preview;
        lock (_previewGate)
            preview = _preview?.CueId == cueId ? _preview : null;

        if (preview is not null)
        {
            await SeekPreviewAsync(preview, position);
            return;
        }

        ActiveCue? entry;
        lock (_gate)
            _active.TryGetValue(cueId, out entry);

        if (entry is null) return;

        await SeekActiveCueAsync(entry, position);
    }

    public Task<string?> ExecuteAsync(MediaCueNode cue, CancellationToken ct) =>
        ExecuteCoreAsync(cue, ct, deferPlay: false);

    public async Task RefreshPreparedCuesAsync(IReadOnlyList<MediaCueNode> cues, CancellationToken ct = default)
    {
        if (cues.Count == 0)
        {
            await _standby.RefreshStandbyAsync([], new ClipStandbyPolicy(), ct).ConfigureAwait(false);
            return;
        }

        var list = await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.SelectedCueList?.ToModel());
        if (list is null)
        {
            await _standby.RefreshStandbyAsync([], new ClipStandbyPolicy(), ct).ConfigureAwait(false);
            return;
        }

        var specs = new List<ClipSpec>();
        foreach (var cue in cues)
        {
            ct.ThrowIfCancellationRequested();

            if (!SupportsCueEngineSource(cue.Source))
                continue;

            if (HasActiveCue(cue.Id))
                continue;

            var plan = BuildRoutePlan(cue);
            if (!plan.HasAnyRoute)
                continue;

            specs.Add(BuildClipSpec(cue, list, plan));
        }

        await _standby.RefreshStandbyAsync(
                specs,
                new ClipStandbyPolicy(MaxPreparedDecoders: Math.Max(0, list.MaxPreparedDecoders), Window: specs.Count),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fires a group of cues with coordinated start: all decoders are opened in parallel, routes are
    /// wired with audio initially paused, and then all audio sources are unpaused at once so playback
    /// starts in sync regardless of how long each decoder took to open.
    /// </summary>
    public async Task<string?> ExecuteGroupAsync(IReadOnlyList<MediaCueNode> cues, CancellationToken ct)
    {
        if (cues.Count == 0) return null;
        if (cues.Count == 1) return await ExecuteAsync(cues[0], ct).ConfigureAwait(false);

        var tasks = cues.Select(c => ExecuteCoreAsync(c, ct, deferPlay: true)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var errors = results.Where(r => r is not null).ToList();

        // Coordinated start: unpause all audio sources, then Play all players at once.
        List<ActiveCue> group;
        lock (_gate)
            group = cues.Select(c => _active.GetValueOrDefault(c.Id)).Where(e => e is not null).ToList()!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var entry in group)
            {
                try { entry.StartPlayback(); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.ExecuteGroupAsync: Play failed for {Cue}", entry.Cue.Id); }
            }

            foreach (var entry in group)
            {
                entry.IsPaused = false;
                foreach (var src in entry.PausableAudioSources)
                    src.IsPaused = false;
                CueStarted?.Invoke(this, entry.Cue.Id);
            }
        });

        foreach (var entry in group)
            _ = WatchNaturalEndAsync(entry);

        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private async Task<string?> ExecuteCoreAsync(MediaCueNode cue, CancellationToken ct, bool deferPlay)
    {
        if (cue.Source is null)
            return "Cue has no source.";

        if (!SupportsCueEngineSource(cue.Source))
            return $"Unsupported cue source: {cue.Source.GetType().Name}.";

        await StopPreviewAsync().ConfigureAwait(false);

        var list = await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.SelectedCueList?.ToModel());
        if (list is null)
            return "No cue list selected.";

        var plan = BuildRoutePlan(cue);
        if (!plan.HasAnyRoute)
            return "Cue has no audio routes or video placements wired to outputs.";
        if (!CanSourceSatisfyRoutePlan(cue.Source, plan, out var sourceRouteError))
            return sourceRouteError;

        // If the same cue id is already running, stop its prior instance. A matching prepared
        // standby instance is kept and consumed below.
        await StopActiveCueAsync(cue.Id).ConfigureAwait(false);

        var spec = BuildClipSpec(cue, list, plan);

        await ReleaseConflictingOutputsAsync(list, plan.AudioByOutput.Keys, plan.Placements.Select(p => p.CompositionId).Distinct())
            .ConfigureAwait(false);

        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.LogError(ex, "CuePlaybackEngine: clip arm failed");
            return ex.Message;
        }

        // Files report their decoded length; live/held sources (image, text, NDI) report none, so fall
        // back to the cue's custom duration. NDI/PortAudio cues leave DurationMs at 0 (unbounded), while
        // image/text cues use it to drive a natural end.
        var sourceDuration = armed.Player.Duration > TimeSpan.Zero
            ? armed.Player.Duration
            : TimeSpan.FromMilliseconds(Math.Max(0, cue.DurationMs));
        var entry = new ActiveCue(cue, armed, new CancellationTokenSource(), CueClipWindow.From(cue, sourceDuration));
        var wireErr = await WireEntryRoutesAsync(entry, list, plan, startPaused: true).ConfigureAwait(false);
        if (wireErr is not null)
        {
            await DisposeEntryAsync(entry, notifyEnded: false).ConfigureAwait(false);
            return wireErr;
        }

        lock (_gate)
            _active[cue.Id] = entry;

        if (!deferPlay)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    entry.StartPlayback();
                    entry.IsPaused = false;
                    foreach (var source in entry.PausableAudioSources)
                        source.IsPaused = false;
                    CueStarted?.Invoke(this, cue.Id);
                });
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "CuePlaybackEngine: Play threw");
                await StopCueAsync(cue.Id).ConfigureAwait(false);
                return ex.Message;
            }

            _ = WatchNaturalEndAsync(entry);
        }

        var targets = new List<string>();
        targets.AddRange(plan.AudioByOutput.Keys
            .Select(id => _outputs.Outputs.FirstOrDefault(l => l.Definition.Id == id)?.Definition.DisplayName ?? "")
            .Where(n => n.Length > 0));
        targets.AddRange(plan.Placements.Select(p => p.CompositionId).Distinct()
            .Select(id => list.Compositions.FirstOrDefault(c => c.Id == id)?.Name ?? "")
            .Where(n => n.Length > 0));
        return $"playing {cue.Source.DisplayName} → {string.Join(", ", targets)}";
    }

    internal static RoutePlan BuildRoutePlan(MediaCueNode cue)
    {
        // Group audio routes by target output line — each group becomes one shared-runtime source.
        var audioByOutput = cue.AudioRoutes
            .Select((route, sourceIndex) => new AudioRoutePlanEntry(route, sourceIndex))
            .Where(entry => entry.Route.OutputLineId != Guid.Empty)
            .GroupBy(entry => entry.Route.OutputLineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every video placement that targets a composition, ordered by layer index. A cue may place the
        // SAME source multiple times on one composition (e.g. picture-in-picture, or the same feed in two
        // regions) — each placement becomes its own composition layer fed by the cue's video router.
        var placementRoutes = cue.VideoPlacements
            .Select((placement, sourceIndex) => (Placement: placement, SourceIndex: sourceIndex))
            .Where(route => route.Placement.CompositionId != Guid.Empty)
            .OrderBy(route => route.Placement.LayerIndex)
            .ToList();
        var placements = placementRoutes
            .Select(route => route.Placement)
            .ToList();
        var placementSourceIndices = placementRoutes
            .Select(route => route.SourceIndex)
            .ToList();

        return new RoutePlan(audioByOutput, placements, placementSourceIndices);
    }

    private static ClipSpec BuildClipSpec(MediaCueNode cue, CueList list, RoutePlan plan)
    {
        if (cue.Source is null)
            throw new InvalidOperationException("Cue source is missing.");

        var hasAudioRoutes = plan.AudioByOutput.Count > 0;
        var (source, window) = cue.Source switch
        {
            FilePlaylistItem fileItem => BuildFileClipSource(cue, fileItem, hasAudioRoutes),
            ImagePlaylistItem imageItem => BuildImageClipSource(cue, imageItem),
            TextPlaylistItem textItem => BuildTextClipSource(cue, textItem),
            NDIInputPlaylistItem ndiItem => BuildNdiClipSource(ndiItem),
            PortAudioInputPlaylistItem paItem => BuildPortAudioClipSource(paItem),
            _ => throw new InvalidOperationException($"Unsupported cue source: {cue.Source.GetType().Name}."),
        };

        var audioRoutes = plan.AudioByOutput
            .SelectMany(kv => kv.Value)
            .Select(entry => ToAudioRouteSpec(entry.Route))
            .ToArray();
        var videoPlacements = plan.Placements
            .Select(placement => new VideoPlacementSpec(
                placement.CompositionId.ToString("N"),
                placement.LayerIndex,
                placement.Opacity,
                placement.Position.ToString(),
                placement.DestX,
                placement.DestY,
                placement.DestWidth,
                placement.DestHeight,
                placement.CropLeft,
                placement.CropTop,
                placement.CropRight,
                placement.CropBottom))
            .ToArray();

        return new ClipSpec(
            cue.Id.ToString("N"),
            source,
            window,
            BuildPreparedCueKey(cue, list),
            audioRoutes,
            videoPlacements);
    }

    private static (IClipMediaSource Source, ClipWindow Window) BuildFileClipSource(
        MediaCueNode cue,
        FilePlaylistItem fileItem,
        bool hasAudioRoutes)
    {
        // Whether to use MediaPlayer's *internal* audio router. We turn it off only when this
        // cue's audio is being mixed externally via ClipAudioOutputRuntime — otherwise we leave
        // it on so the player consumes (and silently drops) any audio stream in the source.
        // Skipping consumption back-pressures the demuxer and starves the video pump, which is
        // what was breaking video playback on a video-with-audio file that had no audio routes
        // wired (e.g. the operator only set up a video placement on it).
        var openOptions = new MediaPlayerOpenOptions(
            TryHardwareAcceleration: true,
            IncludeAudioRouter: !hasAudioRoutes);

        var source = ClipMediaSource.FromBuilder(
            () => MediaPlayer.OpenFile(fileItem.Path)
                .WithOptions(openOptions)
                .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder),
            fileItem.Path);

        var sourceDuration = TimeSpan.FromMilliseconds(Math.Max(0, cue.DurationMs));
        return (source, CueClipWindow.From(cue, sourceDuration));
    }

    private static (IClipMediaSource Source, ClipWindow Window) BuildNdiClipSource(NDIInputPlaylistItem item) =>
        (new HaPlayLiveClipMediaSource(
            item.DisplayName,
            () =>
            {
                if (!NdiInputConnector.TryConnectLive(item, out var receiver, out _, out _, out var error))
                    throw new InvalidOperationException(error ?? $"Failed to connect NDI source '{item.SourceName}'.");

                var wantAudio = !item.VideoOnly;
                var wantVideo = !item.AudioOnly;
                IAudioSource? audio = wantAudio ? receiver.Audio : null;
                IVideoSource? video = wantVideo ? receiver.Video : null;
                if (video is not null)
                    video = PlaybackVideoPipeline.WrapLiveVideoForLocalDisplay(video, disposeInnerOnWrapperDispose: true);
                return new LiveClipSources(audio, video);
            }),
            ClipWindow.Unbounded);

    private static (IClipMediaSource Source, ClipWindow Window) BuildPortAudioClipSource(PortAudioInputPlaylistItem item) =>
        (new HaPlayLiveClipMediaSource(
            item.DisplayName,
            () =>
            {
                if (!PortAudioInputConnector.TryOpen(item, out var input, out _, out var error))
                    throw new InvalidOperationException(error ?? $"Failed to open PortAudio input '{item.DeviceName}'.");
                return new LiveClipSources(input, null);
            }),
            ClipWindow.Unbounded);

    // Still image / rendered text are shown as a single held frame for the cue's custom duration. Both use
    // a held-frame live source bounded by a known clip window so a natural end fires (auto-follow works).
    private static readonly Rational StaticFrameRate = new(30, 1);

    private static (IClipMediaSource Source, ClipWindow Window) BuildImageClipSource(MediaCueNode cue, ImagePlaylistItem item) =>
        (new HaPlayLiveClipMediaSource(
            item.DisplayName,
            () =>
            {
                var frame = FallbackImageLoader.TryBuildHoldFrameAtImageSize(item.Path, StaticFrameRate)
                    ?? throw new InvalidOperationException($"Could not load image '{item.Path}'.");
                return new LiveClipSources(null, new HeldFrameVideoSource(frame));
            }),
            CueClipWindow.From(cue, TimeSpan.FromMilliseconds(Math.Max(0, cue.DurationMs))));

    private static (IClipMediaSource Source, ClipWindow Window) BuildTextClipSource(MediaCueNode cue, TextPlaylistItem item) =>
        (new HaPlayLiveClipMediaSource(
            item.DisplayName,
            () =>
            {
                var frame = TextFrameRenderer.Render(item, StaticFrameRate)
                    ?? throw new InvalidOperationException("Could not render text frame.");
                return new LiveClipSources(null, new HeldFrameVideoSource(frame));
            }),
            CueClipWindow.From(cue, TimeSpan.FromMilliseconds(Math.Max(0, cue.DurationMs))));

    private static AudioRouteSpec ToAudioRouteSpec(CueAudioRoute route) =>
        new(
            route.OutputLineId.ToString("N"),
            route.SourceChannel,
            route.OutputChannel,
            route.GainDb,
            route.Muted);

    private async Task<string?> WireEntryRoutesAsync(ActiveCue entry, CueList list, RoutePlan plan, bool startPaused)
    {
        if (entry.RoutesWired)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.IsPaused = startPaused;
                foreach (var source in entry.PausableAudioSources)
                    source.IsPaused = startPaused;
            });
            return null;
        }

        // Wire audio (shared mixer per output line) and video (shared compositor per composition)
        // before on-demand seeking. Prepared cues are already seeked, but still defer actual output
        // registration until Go so standby does not advance cue output clocks.
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.IsPaused = startPaused;
                WireAudioRoutes(entry, plan.AudioByOutput);
                WireVideoPlacements(entry, list, plan);
                entry.RoutesWired = true;
            });
            return null;
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "CuePlaybackEngine: wiring failed");
            return ex.Message;
        }
    }

    private async Task ReleaseConflictingOutputsAsync(
        CueList list,
        IEnumerable<Guid> audioOutputLineIds,
        IEnumerable<Guid> placementCompositionIds)
    {
        var callback = ReleaseConflictingPlayerOutputsAsync;
        if (callback is null)
            return;

        var ids = new HashSet<Guid>(audioOutputLineIds.Where(id => id != Guid.Empty));
        var placementComps = new HashSet<Guid>(placementCompositionIds.Where(id => id != Guid.Empty));
        foreach (var binding in list.VideoOutputs)
        {
            if (binding.OutputLineId == Guid.Empty) continue;
            if (placementComps.Contains(binding.CompositionId))
                ids.Add(binding.OutputLineId);
        }

        if (ids.Count == 0)
            return;

        await callback(ids.ToList()).ConfigureAwait(false);
    }

    /// <summary>Stop all active cues — used by the Cue VM's Stop / Panic commands.</summary>
    public async Task StopAsync()
    {
        await StopPreviewAsync().ConfigureAwait(false);

        List<ActiveCue> toDispose;
        lock (_gate)
        {
            toDispose = _active.Values.ToList();
            _active.Clear();
        }
        foreach (var entry in toDispose)
            await DisposeEntryAsync(entry, releaseFade: SoftStopFadeDuration).ConfigureAwait(false);
        await _standby.RefreshStandbyAsync([], new ClipStandbyPolicy()).ConfigureAwait(false);
    }

    /// <summary>Stop a specific cue.</summary>
    public async Task StopCueAsync(Guid cueId)
    {
        await StopActiveCueAsync(cueId, SoftStopFadeDuration).ConfigureAwait(false);
        await _standby.RemoveStandbyAsync(cueId.ToString("N")).ConfigureAwait(false);
    }

    private Task StopActiveCueAsync(Guid cueId) => StopActiveCueAsync(cueId, releaseFade: TimeSpan.Zero);

    private async Task StopActiveCueAsync(Guid cueId, TimeSpan releaseFade)
    {
        ActiveCue? entry;
        lock (_gate)
        {
            if (!_active.Remove(cueId, out entry))
                return;
        }
        await DisposeEntryAsync(entry, releaseFade: releaseFade).ConfigureAwait(false);
    }

    /// <summary>Pause or resume every active cue without tearing down decoders or routes.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        List<ActiveCue> entries;
        lock (_gate)
            entries = _active.Values.ToList();

        // Update model state on UI thread first so audio sources silence immediately.
        // Always set unconditionally — IsPaused can be stale after group execution.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var entry in entries)
            {
                entry.IsPaused = paused;
                foreach (var source in entry.PausableAudioSources)
                    source.IsPaused = paused;
            }
        });

        // Heavy transport (thread joins, PortAudio flush) off UI thread with bounded timeout.
        foreach (var entry in entries)
        {
            try
            {
                if (paused)
                {
                    await Task.Run(() =>
                    {
                        entry.Player.Pause(CancellationToken.None, PauseFlushPolicy.SkipFlush);
                    }).WaitAsync(BoundedPauseTimeout);
                    await Dispatcher.UIThread.InvokeAsync(() => entry.Player.PlayClock.SetMaster(null));
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        entry.StartPlayback());
                }
            }
            catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.SetPausedAsync: cue {Cue}", entry.Cue.Id); }
        }
    }

    private bool HasActiveCue(Guid cueId)
    {
        lock (_gate)
            return _active.ContainsKey(cueId);
    }

    private void OnStandbyStatesChanged(IReadOnlyList<S.Media.Playback.ClipPreparationStatus> states)
    {
        RaisePreparedCuesChanged();

        var mapped = states
            .Select(s => TryParseCueId(s.Key.Id, out var id)
                ? new CuePreparationStatus(id, MapPreparationState(s.State), s.Error)
                : (CuePreparationStatus?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToArray();

        try { PreparedCueStatesChanged?.Invoke(mapped); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: PreparedCueStatesChanged handler"); }
    }

    private static PreparedCueState MapPreparationState(ClipPreparationState state) => state switch
    {
        ClipPreparationState.Preparing => PreparedCueState.Preparing,
        ClipPreparationState.Ready => PreparedCueState.Ready,
        ClipPreparationState.Stale => PreparedCueState.Stale,
        ClipPreparationState.Failed => PreparedCueState.Failed,
        _ => PreparedCueState.Idle,
    };

    private static bool TryParseCueId(string id, out Guid cueId) =>
        Guid.TryParseExact(id, "N", out cueId) || Guid.TryParse(id, out cueId);

    private void RaisePreparedCuesChanged()
    {
        var snapshot = _standby.PreparedKeys
            .Select(k => TryParseCueId(k.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        try { PreparedCuesChanged?.Invoke(snapshot); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: PreparedCuesChanged handler"); }
    }

    private async Task DisposeEntryAsync(ActiveCue entry, bool notifyEnded = true, TimeSpan releaseFade = default)
    {
        try { entry.Cts.Cancel(); } catch { /* best effort */ }

        if (releaseFade > TimeSpan.Zero)
            await FadeEntryOutputsAsync(entry, releaseFade).ConfigureAwait(false);

        try { entry.Cts.Dispose(); } catch { /* best effort */ }

        // Detach from shared runtimes on UI thread (lightweight ops).
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var slot in entry.LayerSlots)
            {
                try { slot.Dispose(); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: slot dispose"); }
            }

            foreach (var (runtime, sourceId) in entry.AudioSources)
            {
                try { runtime.RemoveSource(sourceId); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: audio source remove"); }
            }

            foreach (var disposable in entry.AudioDisposables)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: audio adapter dispose"); }
            }
        });

        // Heavy player teardown off UI thread with bounded timeout.
        try
        {
            await Task.Run(() =>
            {
                try { entry.ArmedClip.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: armed clip dispose"); }

                foreach (var conv in entry.ConvertingOutputs)
                {
                    try { conv.Dispose(); }
                    catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: converter dispose"); }
                }
            }).WaitAsync(BoundedDisposeTimeout);
        }
        catch (TimeoutException)
        {
            Trace.LogWarning("CuePlaybackEngine.DisposeEntryAsync: player dispose timed out after {Timeout}", BoundedDisposeTimeout);
        }

        // UI cleanup: release empty shared runtimes and notify.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ReleaseEmptyRuntimes();

            if (notifyEnded)
            {
                try { CueEnded?.Invoke(this, entry.Cue.Id); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: CueEnded handler"); }
            }
        });
    }

    private async Task FadeEntryOutputsAsync(ActiveCue entry, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        var audioTasks = entry.AudioSources
            .Select(source => FadeAudioSourceAsync(source.Runtime, source.SourceId, duration))
            .ToList();
        var videoTask = FadeVideoSlotsAsync(entry.LayerSlots, duration);

        try
        {
            await Task.WhenAll(audioTasks.Append(videoTask)).WaitAsync(duration + TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Trace.LogWarning("CuePlaybackEngine.FadeEntryOutputsAsync: fade timed out after {Duration}", duration);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.FadeEntryOutputsAsync");
        }
    }

    private static async Task FadeAudioSourceAsync(ClipAudioOutputRuntime runtime, string sourceId, TimeSpan duration)
    {
        try { await runtime.FadeOutSourceAsync(sourceId, duration).ConfigureAwait(false); }
        catch { /* runtime may already be disposed or the route may already be gone */ }
    }

    private static async Task FadeVideoSlotsAsync(IReadOnlyList<CueCompositionRuntime.LayerSlot> slots, TimeSpan duration)
    {
        if (slots.Count == 0)
            return;

        var startOpacities = slots.Select(slot => slot.Opacity).ToArray();
        var steps = Math.Clamp((int)Math.Ceiling(Math.Max(1, duration.TotalMilliseconds) / 33.0), 1, 120);
        var delay = TimeSpan.FromTicks(Math.Max(1, duration.Ticks / steps));
        for (var step = 1; step <= steps; step++)
        {
            var scale = Math.Clamp(1f - (float)step / steps, 0f, 1f);
            for (var i = 0; i < slots.Count; i++)
                slots[i].Opacity = startOpacities[i] * scale;
            if (step < steps)
                await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    private void ReleaseEmptyRuntimes()
    {
        List<KeyValuePair<Guid, CueCompositionRuntime>> emptyComps;
        List<KeyValuePair<Guid, ClipAudioOutputRuntime>> emptyAudio;
        lock (_gate)
        {
            emptyComps = _compositions.Where(kv => kv.Value.LayerCount == 0).ToList();
            emptyAudio = _audioOutputs.Where(kv => kv.Value.SourceCount == 0).ToList();
        }
        foreach (var kv in emptyComps)
        {
            lock (_gate) _compositions.Remove(kv.Key);
            try { kv.Value.Dispose(); } catch (Exception ex) { Trace.LogWarning(ex, "ReleaseEmptyRuntimes: comp"); }
        }
        foreach (var kv in emptyAudio)
        {
            lock (_gate) _audioOutputs.Remove(kv.Key);
            try { kv.Value.Dispose(); } catch (Exception ex) { Trace.LogWarning(ex, "ReleaseEmptyRuntimes: audio"); }
        }
    }

    private void WireAudioRoutes(ActiveCue entry, Dictionary<Guid, List<AudioRoutePlanEntry>> audioByOutput)
    {
        if (audioByOutput.Count == 0) return;

        IAudioSource decoderAudio;
        if (!TryGetCueAudioSource(entry, out decoderAudio))
            return;

        var fanout = new AudioSourceFanout(decoderAudio);
        entry.AudioFanout = fanout;
        entry.AudioDisposables.Add(fanout);

        foreach (var (lineId, routes) in audioByOutput)
            CreateActiveAudioOutput(entry, lineId, routes);
    }

    private ActiveAudioOutput? CreateActiveAudioOutput(
        ActiveCue entry,
        Guid lineId,
        IReadOnlyList<AudioRoutePlanEntry> routes)
    {
        if (routes.Count == 0 || entry.AudioFanout is null)
            return null;

        var runtime = GetOrCreateAudioRuntime(lineId);
        if (runtime is null) return null;

        var routedSource = entry.AudioFanout.CreateBranch();
        var pausable = new PausableAudioSource(routedSource, disposeInner: true)
        {
            IsPaused = entry.IsPaused,
        };
        entry.PausableAudioSources.Add(pausable);
        entry.AudioDisposables.Add(pausable);

        var sourceIdHint = BuildAudioSourceId(entry, lineId);
        var routeSpecs = routes.Select(route => ToAudioRouteSpec(route.Route)).ToArray();
        var srcId = runtime.AddSource(
            pausable,
            routeSpecs,
            sourceIdHint,
            (sourceId, routeOrdinal) => BuildAudioRouteId(sourceId, routes[routeOrdinal].SourceIndex));
        entry.AudioSources.Add((runtime, srcId));

        var output = new ActiveAudioOutput(lineId, runtime, srcId, pausable);
        entry.AudioOutputsByLine[lineId] = output;
        foreach (var route in routes)
        {
            var routeId = BuildAudioRouteId(srcId, route.SourceIndex);
            entry.AudioRoutesByIndex[route.SourceIndex] = new ActiveAudioRoute(
                lineId,
                runtime,
                srcId,
                routeId,
                route.Route);
        }

        if (runtime.PlaybackClock is { } playbackClock)
            entry.PlaybackClockMaster ??= playbackClock;
        return output;
    }

    private void ReconcileActiveAudioRoutes(ActiveCue entry, IReadOnlyList<CueAudioRoute> routes)
    {
        var desired = routes
            .Select((route, sourceIndex) => new AudioRoutePlanEntry(route, sourceIndex))
            .Where(route => route.Route.OutputLineId != Guid.Empty)
            .ToDictionary(route => route.SourceIndex);

        if (desired.Count > 0 && entry.AudioFanout is null)
        {
            if (!TryGetCueAudioSource(entry, out var decoderAudio))
                return;
            entry.AudioFanout = new AudioSourceFanout(decoderAudio);
            entry.AudioDisposables.Add(entry.AudioFanout);
        }

        foreach (var (sourceIndex, active) in entry.AudioRoutesByIndex.ToArray())
        {
            if (!desired.TryGetValue(sourceIndex, out var route)
                || route.Route.OutputLineId != active.OutputLineId)
            {
                RemoveActiveAudioRoute(entry, sourceIndex);
            }
        }

        foreach (var route in desired.Values.OrderBy(route => route.SourceIndex))
        {
            if (entry.AudioRoutesByIndex.TryGetValue(route.SourceIndex, out var active)
                && active.OutputLineId == route.Route.OutputLineId)
            {
                UpdateActiveAudioRoute(entry, route.SourceIndex, active, route.Route);
                continue;
            }

            AddActiveAudioRoute(entry, route);
        }

        ReleaseEmptyRuntimes();
    }

    private void AddActiveAudioRoute(ActiveCue entry, AudioRoutePlanEntry route)
    {
        var lineId = route.Route.OutputLineId;
        try
        {
            if (!entry.AudioOutputsByLine.TryGetValue(lineId, out var output))
            {
                CreateActiveAudioOutput(entry, lineId, [route]);
                return;
            }

            var routeId = BuildAudioRouteId(output.SourceId, route.SourceIndex);
            output.Runtime.UpdateRoute(output.SourceId, routeId, ToAudioRouteSpec(route.Route));
            entry.AudioRoutesByIndex[route.SourceIndex] = new ActiveAudioRoute(
                lineId,
                output.Runtime,
                output.SourceId,
                routeId,
                route.Route);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.AddActiveAudioRoute: route {RouteIndex}", route.SourceIndex);
        }
    }

    private void UpdateActiveAudioRoute(
        ActiveCue entry,
        int routeIndex,
        ActiveAudioRoute active,
        CueAudioRoute route)
    {
        try
        {
            if (active.Route.SourceChannel == route.SourceChannel
                && active.Route.OutputChannel == route.OutputChannel)
            {
                active.Runtime.SetRouteGain(active.SourceId, active.RouteId, route.GainDb, route.Muted);
            }
            else
            {
                active.Runtime.UpdateRoute(active.SourceId, active.RouteId, ToAudioRouteSpec(route));
            }

            entry.AudioRoutesByIndex[routeIndex] = active with { Route = route };
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.UpdateActiveAudioRoute: route {RouteIndex}", routeIndex);
        }
    }

    private void RemoveActiveAudioRoute(ActiveCue entry, int routeIndex)
    {
        if (!entry.AudioRoutesByIndex.Remove(routeIndex, out var active))
            return;

        active.Runtime.RemoveRoute(active.SourceId, active.RouteId);
        RemoveActiveAudioOutputIfEmpty(entry, active.OutputLineId);
    }

    private void RemoveActiveAudioOutputIfEmpty(ActiveCue entry, Guid lineId)
    {
        if (entry.AudioRoutesByIndex.Values.Any(route => route.OutputLineId == lineId))
            return;
        if (!entry.AudioOutputsByLine.Remove(lineId, out var output))
            return;

        output.Runtime.RemoveSource(output.SourceId);
        entry.AudioSources.RemoveAll(source =>
            ReferenceEquals(source.Runtime, output.Runtime)
            && string.Equals(source.SourceId, output.SourceId, StringComparison.Ordinal));
        entry.PausableAudioSources.Remove(output.Source);
        entry.AudioDisposables.Remove(output.Source);
        try { output.Source.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.RemoveActiveAudioOutputIfEmpty: source dispose"); }
    }

    private static string BuildAudioSourceId(ActiveCue entry, Guid lineId) =>
        $"cue_{entry.Cue.Id:N}_{entry.InstanceId:N}_{lineId:N}";

    private static string BuildAudioRouteId(string sourceId, int routeIndex) =>
        $"{sourceId}_ar{routeIndex}";

    private static bool TryGetCueAudioSource(ActiveCue entry, out IAudioSource audioSource)
    {
        audioSource = null!;
        if (entry.ArmedClip.Spec.Source is HaPlayLiveClipMediaSource live)
        {
            if (live.AudioSource is not { } liveAudio)
                return false;
            audioSource = liveAudio;
            return true;
        }

        try
        {
            if (!entry.Player.HasContainerDecoder || !entry.Player.Decoder.HasAudio)
                return false;
            audioSource = entry.Player.Decoder.Audio;
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WireAudioRoutes: source has no audio");
            return false;
        }
    }

    private void WireVideoPlacements(
        ActiveCue entry,
        CueList list,
        RoutePlan plan)
    {
        var placements = plan.Placements;
        if (placements.Count == 0) return;

        S.Media.Core.Video.VideoFormat sourceFormat;
        try
        {
            sourceFormat = entry.Player.Video.Format;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WireVideoPlacements: source has no video");
            return;
        }

        if (sourceFormat.Width <= 0 || sourceFormat.Height <= 0)
            return;

        var router = entry.Player.VideoRouter;
        var inputId = entry.Player.VideoRouterInputId;

        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            var compId = placement.CompositionId;
            var runtime = GetOrCreateComposition(list, compId);
            if (runtime is null) continue;

            // Phase 5.4 — slave the composition pump to this cue's audio master so the
            // composited frames present at the master's video-tick rate instead of free-running
            // on Stopwatch. Composition keeps only the FIRST master it sees (idempotent), so
            // back-to-back cues with different masters don't fight for the slave clock.
            if (entry.PlaybackClockMaster is { } master)
                runtime.SetClockMaster(master);

            var slot = runtime.AddLayer(sourceFormat, placement);
            entry.LayerSlots.Add(slot);
            if (i < plan.PlacementSourceIndices.Count)
                entry.LayerSlotsByPlacementIndex[plan.PlacementSourceIndices[i]] = slot;

            IVideoOutput layerOutput = slot.Output;
            if (runtime.RequiresBgraLayerConversion)
            {
                // CPU composition is BGRA32-only. The OpenGL compositor advertises native YUV/YUVA
                // formats directly, so this conversion is skipped for heavy ProRes/alpha cue stacks.
                var converter = new BgraConvertingVideoOutput(
                    slot.Output,
                    premultiplyAlpha: S.Media.Core.Video.PixelFormatInfo.IsAlphaCarrying(sourceFormat.PixelFormat));
                entry.ConvertingOutputs.Add(converter);
                layerOutput = converter;
            }

            // Black-screen fix: a cue with a start offset is seeked to ClipWindow.Start, so its
            // frames carry source-timeline PTS (e.g. 80 min in). The composition compares layer
            // frames against the cue-relative master clock that starts at t=0, so without rebasing
            // these frames look far in the future and never present. RetimingVideoOutput adds a
            // negative offset (−ClipWindow.Start) to convert source PTS to cue-relative PTS before
            // the frame reaches the composition slot.
            if (entry.ClipWindow.Start > TimeSpan.Zero)
                layerOutput = new RetimingVideoOutput(layerOutput, -entry.ClipWindow.Start);

            // The index keeps the id unique when the same source is placed more than once on one
            // composition (same compId), so the router accepts every layer's output.
            var outId = router.AddOutput(layerOutput, id: $"cuecomp_{entry.Cue.Id:N}_{entry.InstanceId:N}_{compId:N}_{i}",
                disposeOutputOnRouterDispose: false,
                synchronous: true);
            if (!router.TryAddRoute(inputId, outId, out var routeErr))
                throw new InvalidOperationException(routeErr ?? "TryAddRoute failed for composition slot");
        }
    }

    private CueCompositionRuntime? GetOrCreateComposition(CueList list, Guid compositionId)
    {
        lock (_gate)
        {
            if (_compositions.TryGetValue(compositionId, out var existing))
                return existing;
        }

        var composition = list.Compositions.FirstOrDefault(c => c.Id == compositionId);
        if (composition is null) return null;

        var targetLineIds = list.VideoOutputs
            .Where(b => b.CompositionId == compositionId && b.OutputLineId != Guid.Empty)
            .Select(b => b.OutputLineId)
            .ToHashSet();
        var targetLines = _outputs.Outputs.Where(l => targetLineIds.Contains(l.Definition.Id)).ToList();

        var runtime = new CueCompositionRuntime(composition, targetLines, _outputs);
        // Surface drift warnings to the operator via the cue VM's status message — keeps the
        // "your composition is behind by 12 frames" signal close to the transport UI rather
        // than buried in logs only.
        runtime.DriftWarning += async (_, warning) =>
        {
            var msg = $"Composition '{warning.CompositionName}' drift: {warning.FramesBehindMaster} frames behind master ({warning.LagFromMaster.TotalMilliseconds:0} ms)";
            await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.StatusMessage = msg);
        };
        runtime.PumpPressureWarning += async (_, w) =>
        {
            var msg = Strings.Format(nameof(Strings.NdiPumpPressureStatusFormat),
                w.OutputLineName, w.DroppedSinceLastReport, w.DroppedTotal);
            await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.StatusMessage = msg);
        };
        lock (_gate)
        {
            if (_compositions.TryGetValue(compositionId, out var existing))
            {
                runtime.Dispose();
                return existing;
            }
            _compositions[compositionId] = runtime;
        }
        return runtime;
    }

    private ClipAudioOutputRuntime? GetOrCreateAudioRuntime(Guid outputLineId)
    {
        lock (_gate)
        {
            if (_audioOutputs.TryGetValue(outputLineId, out var existing))
                return existing;
        }

        var line = _outputs.Outputs.FirstOrDefault(l => l.Definition.Id == outputLineId);
        if (line is null || !IsAudioCapableOutput(line.Definition))
        {
            Trace.LogWarning("GetOrCreateAudioRuntime: line {Id} is not an audio-capable output", outputLineId);
            return null;
        }

        try
        {
            var (audioOutput, playbackClock, releaseOutput) = AcquireAudioOutput(line, _outputs);
            var runtime = new ClipAudioOutputRuntime(
                outputLineId.ToString("N"),
                audioOutput,
                playbackClock,
                releaseOutput,
                line.Definition.DisplayName);
            lock (_gate)
            {
                if (_audioOutputs.TryGetValue(outputLineId, out var existing))
                {
                    runtime.Dispose();
                    return existing;
                }
                _audioOutputs[outputLineId] = runtime;
            }
            return runtime;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "GetOrCreateAudioRuntime: failed to acquire {Line}", line.Definition.DisplayName);
            return null;
        }
    }

    private static bool IsAudioCapableOutput(OutputDefinition definition) =>
        definition is PortAudioOutputDefinition
        || definition is NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.VideoOnly };

    private static bool SupportsCueEngineSource(PlaylistItem? source) =>
        source is FilePlaylistItem or ImagePlaylistItem or TextPlaylistItem or NDIInputPlaylistItem or PortAudioInputPlaylistItem;

    private static bool CanSourceSatisfyRoutePlan(PlaylistItem? source, RoutePlan plan, out string? error)
    {
        error = null;
        switch (source)
        {
            case PortAudioInputPlaylistItem when plan.Placements.Count > 0:
                error = "PortAudio input cues do not provide video for video placements.";
                return false;
            case NDIInputPlaylistItem { AudioOnly: true } when plan.Placements.Count > 0:
                error = "This NDI input cue is audio-only and cannot feed video placements.";
                return false;
            case NDIInputPlaylistItem { VideoOnly: true } when plan.AudioByOutput.Count > 0:
                error = "This NDI input cue is video-only and cannot feed audio routes.";
                return false;
            default:
                return true;
        }
    }

    private static (IAudioOutput Output, IPlaybackClock? PlaybackClock, Action Release) AcquireAudioOutput(
        OutputLineViewModel line,
        OutputManagementViewModel outputs)
    {
        switch (line.Definition)
        {
            case PortAudioOutputDefinition:
            {
                var pa = outputs.TryAcquirePortAudioForPlayback(line)
                    ?? throw new InvalidOperationException(
                        $"PortAudio output '{line.Definition.DisplayName}' couldn't be acquired (preview not running or held).");
                return (pa, pa, () => outputs.ReleasePortAudioForPlayback(line));
            }
            case NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.VideoOnly } nd:
            {
                var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo: false, needsAudio: true)
                    ?? throw new InvalidOperationException(
                        $"NDI output '{line.Definition.DisplayName}' couldn't be acquired (carrier not running or held).");
                try
                {
                    var channels = Math.Max(1, nd.AudioChannelCount);
                    var sampleRate = Math.Max(1, nd.AudioSampleRate);
                    var output = ndi.EnableAudio(new AudioFormat(sampleRate, channels));
                    return (output, null, () => outputs.ReleaseNDICarrierForPlayback(line, releaseVideo: false, releaseAudio: true));
                }
                catch
                {
                    outputs.ReleaseNDICarrierForPlayback(line, releaseVideo: false, releaseAudio: true);
                    throw;
                }
            }
            case NDIOutputDefinition:
                throw new InvalidOperationException(
                    $"NDI output '{line.Definition.DisplayName}' is video-only and cannot receive cue audio.");
            default:
                throw new InvalidOperationException(
                    $"Output '{line.Definition.DisplayName}' is not an audio-capable output.");
        }
    }

    private async Task SeekPreviewAsync(CuePreviewSession preview, TimeSpan position)
    {
        position = preview.ClipWindow.ToSourcePosition(position);
        try
        {
            await Task.Run(() =>
                preview.Player.SeekCoordinated(position, CancellationToken.None, PauseFlushPolicy.SkipFlush)
            ).WaitAsync(BoundedPauseTimeout);
        }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.SeekPreviewAsync: seek timed out or failed"); }
        await Dispatcher.UIThread.InvokeAsync(() => preview.Play());
    }

    private async Task SeekActiveCueAsync(ActiveCue entry, TimeSpan position)
    {
        position = entry.ClipWindow.ToSourcePosition(position);
        var resume = !entry.IsPaused;
        if (resume)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.IsPaused = true;
                foreach (var source in entry.PausableAudioSources)
                    source.IsPaused = true;
            });
        }

        try
        {
            await Task.Run(() =>
                entry.Player.SeekCoordinated(position, CancellationToken.None, PauseFlushPolicy.SkipFlush)
            ).WaitAsync(BoundedPauseTimeout);
        }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.SeekActiveCueAsync: seek timed out or failed"); }
        if (resume)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.StartPlayback();
                entry.IsPaused = false;
                foreach (var source in entry.PausableAudioSources)
                    source.IsPaused = false;
            });
    }

    private async Task WatchPreviewEndAsync(CuePreviewSession session)
    {
        var ct = session.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);

                TimeSpan pos;
                try { pos = session.Player.PlayClock.CurrentPosition; }
                catch { continue; }

                var progress = new CuePlaybackProgress(
                    session.CueId,
                    session.ClipWindow.ToRelativePosition(pos),
                    session.ClipWindow.Duration);
                await Dispatcher.UIThread.InvokeAsync(() => CueProgress?.Invoke(this, progress));

                if (!session.ClipWindow.HasKnownEnd) continue;
                if (session.ClipWindow.IsAtEnd(pos))
                {
                    await StopPreviewAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WatchPreviewEndAsync");
        }
    }

    private async Task WatchNaturalEndAsync(ActiveCue entry)
    {
        var ct = entry.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);

                TimeSpan pos;
                try { pos = entry.Player.PlayClock.CurrentPosition; }
                catch { continue; }

                // Emit progress for the Now Playing panel even when duration isn't known yet
                // (live sources advertise Duration.Zero but still have a real position).
                var progress = new CuePlaybackProgress(
                    entry.Cue.Id,
                    entry.ClipWindow.ToRelativePosition(pos),
                    entry.ClipWindow.Duration);
                await Dispatcher.UIThread.InvokeAsync(() => CueProgress?.Invoke(this, progress));

                if (!entry.ClipWindow.HasKnownEnd) continue;
                if (entry.ClipWindow.IsAtEnd(pos))
                {
                    if (entry.Cue.Loop || entry.Cue.EndBehavior == CueEndBehavior.Loop)
                    {
                        await SeekActiveCueAsync(entry, TimeSpan.Zero).ConfigureAwait(false);
                        continue;
                    }

                    lock (_gate) _active.Remove(entry.Cue.Id);
                    await RaiseNaturalEndAsync().ConfigureAwait(false);
                    await DisposeEntryAsync(entry).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WatchNaturalEndAsync");
        }
    }

    private async Task RaiseNaturalEndAsync()
    {
        var handlers = NaturalEnd;
        if (handlers is null)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                foreach (Func<Task> handler in handlers.GetInvocationList())
                    await handler().ConfigureAwait(true);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { StopPreviewAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _standby.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best effort */ }

        List<CueCompositionRuntime> compsLeft;
        List<ClipAudioOutputRuntime> audioLeft;
        lock (_gate)
        {
            compsLeft = _compositions.Values.ToList();
            audioLeft = _audioOutputs.Values.ToList();
            _compositions.Clear();
            _audioOutputs.Clear();
        }
        foreach (var r in compsLeft) { try { r.Dispose(); } catch { } }
        foreach (var r in audioLeft) { try { r.Dispose(); } catch { } }
    }

    private static string BuildPreparedCueKey(MediaCueNode cue, CueList list)
    {
        var source = cue.Source?.CacheKey() ?? string.Empty;
        var audio = string.Join(";", cue.AudioRoutes
            .OrderBy(r => r.OutputLineId)
            .ThenBy(r => r.SourceChannel)
            .ThenBy(r => r.OutputChannel)
            .Select(r => string.Join(",",
                r.SourceChannel,
                r.OutputLineId.ToString("N"),
                r.OutputChannel,
                r.GainDb.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.Muted ? "1" : "0")));
        var placements = string.Join(";", cue.VideoPlacements
            .OrderBy(p => p.CompositionId)
            .ThenBy(p => p.LayerIndex)
            .Select(p => string.Join(",",
                p.CompositionId.ToString("N"),
                p.LayerIndex,
                p.Position,
                p.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.DestX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.DestY.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.DestWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.DestHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.CropLeft.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.CropTop.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.CropRight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                p.CropBottom.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        var compositions = string.Join(";", list.Compositions
            .OrderBy(c => c.Id)
            .Select(c => string.Join(",",
                c.Id.ToString("N"),
                c.Width,
                c.Height,
                c.FrameRateNum,
                c.FrameRateDen)));
        var videoOutputs = string.Join(";", list.VideoOutputs
            .OrderBy(o => o.OutputLineId)
            .ThenBy(o => o.CompositionId)
            .Select(o => $"{o.OutputLineId:N},{o.CompositionId:N}"));

        return string.Join("|",
            source,
            $"start:{Math.Max(0, cue.StartOffsetMs)}",
            $"end:{Math.Max(0, cue.EndOffsetMs)}",
            $"loop:{cue.Loop}",
            $"endBehavior:{cue.EndBehavior}",
            $"audio:{audio}",
            $"video:{placements}",
            $"comps:{compositions}",
            $"outputs:{videoOutputs}");
    }

    internal sealed record RoutePlan(
        Dictionary<Guid, List<AudioRoutePlanEntry>> AudioByOutput,
        IReadOnlyList<CueVideoPlacement> Placements,
        IReadOnlyList<int> PlacementSourceIndices)
    {
        public bool HasAnyRoute => AudioByOutput.Count > 0 || Placements.Count > 0;
    }

    internal sealed record AudioRoutePlanEntry(CueAudioRoute Route, int SourceIndex);

    private readonly record struct LiveClipSources(IAudioSource? Audio, IVideoSource? Video);

    private sealed class HaPlayLiveClipMediaSource : IClipMediaSource
    {
        private readonly Func<LiveClipSources> _openSources;

        public HaPlayLiveClipMediaSource(string description, Func<LiveClipSources> openSources)
        {
            Description = string.IsNullOrWhiteSpace(description) ? "(live input)" : description;
            _openSources = openSources ?? throw new ArgumentNullException(nameof(openSources));
        }

        public string Description { get; }

        public IAudioSource? AudioSource { get; private set; }

        public IVideoSource? VideoSource { get; private set; }

        public MediaPlayerOpenBuilder CreateOpenBuilder()
        {
            var sources = _openSources();
            if (sources.Audio is null && sources.Video is null)
                throw new InvalidOperationException("Live cue source did not provide audio or video.");

            AudioSource = sources.Audio;
            VideoSource = sources.Video;

            var videoForPlayer = sources.Video ?? (sources.Audio is not null ? new EmptyCueLiveVideoSource() : null);
            return MediaPlayer.OpenLive(sources.Audio, videoForPlayer)
                .WithOptions(new MediaPlayerOpenOptions(
                    IncludeAudioRouter: false,
                    LiveVideoPresentation: VideoPresentationMode.LatestOnTick))
                .WithDisposeSourcesOnPlayerDispose(true);
        }
    }

    private sealed class EmptyCueLiveVideoSource : IVideoSource
    {
        private VideoFormat _format = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

        public VideoFormat Format => _format;

        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

        public bool IsExhausted => false;

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }
    }

    private sealed class ActiveCue
    {
        public ActiveCue(MediaCueNode cue, IArmedClip armedClip, CancellationTokenSource cts, ClipWindow clipWindow)
        {
            Cue = cue;
            ArmedClip = armedClip;
            Cts = cts;
            ClipWindow = clipWindow;
        }

        public MediaCueNode Cue { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public IArmedClip ArmedClip { get; }
        public MediaPlayer Player => ArmedClip.Player;
        public CancellationTokenSource Cts { get; }
        public ClipWindow ClipWindow { get; }

        /// <summary>The cue's audio-runtime playback clock, captured from the first wired
        /// <see cref="ClipAudioOutputRuntime.PlaybackClock"/>. Used as the composition master
        /// (<see cref="CueCompositionRuntime.SetClockMaster"/>) and passed to
        /// <c>MediaPlayer.Play(videoOnlyMaster:)</c> so video presents at the audio clock's rate.
        /// Null for video-only cues, which then free-run on their own clock.</summary>
        public IPlaybackClock? PlaybackClockMaster { get; set; }
        public bool IsPaused { get; set; }
        public bool RoutesWired { get; set; }
        public List<CueCompositionRuntime.LayerSlot> LayerSlots { get; } = new();
        public Dictionary<int, CueCompositionRuntime.LayerSlot> LayerSlotsByPlacementIndex { get; } = new();
        public List<BgraConvertingVideoOutput> ConvertingOutputs { get; } = new();
        public AudioSourceFanout? AudioFanout { get; set; }
        public Dictionary<Guid, ActiveAudioOutput> AudioOutputsByLine { get; } = new();
        public Dictionary<int, ActiveAudioRoute> AudioRoutesByIndex { get; } = new();
        public List<(ClipAudioOutputRuntime Runtime, string SourceId)> AudioSources { get; } = new();
        public List<PausableAudioSource> PausableAudioSources { get; } = new();
        public List<IDisposable> AudioDisposables { get; } = new();

        public void StartPlayback()
        {
            if (ArmedClip.IsStarted)
            {
                Player.Play(videoOnlyMaster: PlaybackClockMaster);
                return;
            }

            ArmedClip.Start(videoOnlyMaster: PlaybackClockMaster);
        }
    }

    private sealed record ActiveAudioOutput(
        Guid OutputLineId,
        ClipAudioOutputRuntime Runtime,
        string SourceId,
        PausableAudioSource Source);

    private sealed record ActiveAudioRoute(
        Guid OutputLineId,
        ClipAudioOutputRuntime Runtime,
        string SourceId,
        string RouteId,
        CueAudioRoute Route);
}

/// <summary>Periodic progress sample for the Now Playing panel.</summary>
public readonly record struct CuePlaybackProgress(Guid CueId, TimeSpan Position, TimeSpan Duration);

/// <summary>Standby preparation lifecycle for one cue. <c>Idle</c> = not in the warm window or not
/// attempted; <c>Preparing</c> = opening/seeking; <c>Ready</c> = opened, routed, seeked to start;
/// <c>Stale</c> = a previously-ready standby whose cue config changed, awaiting re-preparation by the
/// next pre-roll refresh; <c>Failed</c> = open failed (reason in
/// <see cref="CuePreparationStatus.Error"/>).</summary>
public enum PreparedCueState
{
    Idle,
    Preparing,
    Ready,
    Stale,
    Failed,
}

/// <summary>Per-cue preparation status snapshot raised by
/// <see cref="CuePlaybackEngine.PreparedCueStatesChanged"/>.</summary>
public readonly record struct CuePreparationStatus(Guid CueId, PreparedCueState State, string? Error);

/// <summary>HaPlay adapter over the framework <see cref="ClipWindow"/>: builds one from a media
/// cue's start/end trim offsets. The window math itself now lives in <see cref="ClipWindow"/> so it
/// is shared with the media player and any future clip host.</summary>
internal static class CueClipWindow
{
    public static ClipWindow From(MediaCueNode cue, TimeSpan sourceDuration) =>
        ClipWindow.FromOffsets(
            TimeSpan.FromMilliseconds(Math.Max(0, cue.StartOffsetMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, cue.EndOffsetMs)),
            sourceDuration);
}
