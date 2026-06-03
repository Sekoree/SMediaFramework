using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Media.Core;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.Playback;

namespace HaPlay.Playback;

/// <summary>
/// Cue-side playback runtime. Manages N concurrent media cues plus two pools of shared resources:
/// <see cref="CueCompositionRuntime"/> per active composition (shared video mixer + acquired
/// video outputs) and <see cref="CueAudioOutputRuntime"/> per active audio-capable output line
/// (shared audio router so N cues' audio mix into one device). Completely independent of
/// MediaPlayer tabs — only shares the <see cref="OutputManagementViewModel"/> registry of
/// physical output lines.
/// </summary>
public sealed class CuePlaybackEngine : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CuePlaybackEngine");
    private static readonly TimeSpan BoundedPauseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BoundedDisposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Resource-policy ceiling on concurrently-prepared standby decoders, independent of the
    /// requested pre-roll window size. Each prepared cue holds an opened + seeked decoder.</summary>
    private const int MaxPreparedDecoders = 6;

    private readonly OutputManagementViewModel _outputs;
    private readonly CuePlayerViewModel _cuePlayer;
    private readonly object _gate = new();

    private readonly Dictionary<Guid, ActiveCue> _active = new();
    private readonly Dictionary<Guid, PreparedCue> _prepared = new();
    private readonly Dictionary<Guid, CueCompositionRuntime> _compositions = new();
    private readonly Dictionary<Guid, CueAudioOutputRuntime> _audioOutputs = new();
    private readonly object _previewGate = new();
    private CuePreviewSession? _preview;

    public CuePlaybackEngine(OutputManagementViewModel outputs, CuePlayerViewModel cuePlayer)
    {
        _outputs = outputs;
        _cuePlayer = cuePlayer;
    }

    /// <summary>Raised on the UI thread when a cue's media ends naturally (file reached duration).
    /// The cue VM listens to drive <c>AutoFollow</c> for the most-recently-fired cue.</summary>
    public event EventHandler? NaturalEnd;

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

    // Per-cue preparation status, guarded by _gate. Only non-idle cues are tracked; absence == Idle.
    private readonly Dictionary<Guid, CuePreparationStatus> _prepStatus = new();

    private void SetPrepStatus(Guid cueId, PreparedCueState state, string? error = null)
    {
        lock (_gate)
        {
            if (state == PreparedCueState.Idle)
            {
                if (!_prepStatus.Remove(cueId))
                    return;
            }
            else
            {
                var status = new CuePreparationStatus(cueId, state, error);
                if (_prepStatus.TryGetValue(cueId, out var existing) && existing == status)
                    return;
                _prepStatus[cueId] = status;
            }
        }
        RaisePreparedCueStatesChanged();
    }

    private void RaisePreparedCueStatesChanged()
    {
        CuePreparationStatus[] snapshot;
        lock (_gate)
            snapshot = _prepStatus.Values.ToArray();
        try { PreparedCueStatesChanged?.Invoke(snapshot); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: PreparedCueStatesChanged handler"); }
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
            await ClearPreparedCuesAsync().ConfigureAwait(false);
            return;
        }

        var list = await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.SelectedCueList?.ToModel());
        if (list is null)
        {
            await ClearPreparedCuesAsync().ConfigureAwait(false);
            return;
        }

        var keepIds = new HashSet<Guid>();
        foreach (var cue in cues)
        {
            ct.ThrowIfCancellationRequested();

            if (cue.Source is not FilePlaylistItem)
                continue;

            if (HasActiveCue(cue.Id))
                continue;

            var plan = BuildRoutePlan(cue);
            if (!plan.HasAnyRoute)
                continue;

            keepIds.Add(cue.Id);
            var cacheKey = BuildPreparedCueKey(cue, list);
            if (HasMatchingPreparedCue(cue.Id, cacheKey))
            {
                SetPrepStatus(cue.Id, PreparedCueState.Ready);
                continue;
            }

            await RemovePreparedCueAsync(cue.Id).ConfigureAwait(false);
            SetPrepStatus(cue.Id, PreparedCueState.Preparing);

            var (entry, err) = await OpenCueEntryAsync(cue, list, plan, wireRoutes: false, ct)
                .ConfigureAwait(false);
            if (entry is null)
            {
                if (!string.IsNullOrWhiteSpace(err))
                    Trace.LogWarning("CuePlaybackEngine.RefreshPreparedCuesAsync: {Cue} failed: {Error}", cue.Id, err);
                SetPrepStatus(cue.Id, PreparedCueState.Failed, err ?? "Failed to prepare cue.");
                continue;
            }

            if (HasActiveCue(cue.Id))
            {
                await DisposeEntryAsync(entry, notifyEnded: false).ConfigureAwait(false);
                SetPrepStatus(cue.Id, PreparedCueState.Idle);
                continue;
            }

            await StorePreparedCueAsync(cue.Id, cacheKey, entry).ConfigureAwait(false);
            SetPrepStatus(cue.Id, PreparedCueState.Ready);
        }

        // Resource policy: never hold more than MaxPreparedDecoders open standby decoders, even if a
        // very large pre-roll window was requested — long H.264 files each keep an opened+seeked
        // decoder. The window size (cues.Count) caps it further when smaller.
        var cap = Math.Clamp(cues.Count, 1, MaxPreparedDecoders);
        await EvictPreparedExceptAsync(keepIds, cap).ConfigureAwait(false);

        // Reconcile: any cue we still track a status for but is no longer in the warm window
        // (dropped out, or a Failed cue that left the pre-roll set) returns to Idle.
        Guid[] trackedNotKept;
        lock (_gate)
            trackedNotKept = _prepStatus.Keys.Where(id => !keepIds.Contains(id)).ToArray();
        foreach (var id in trackedNotKept)
            SetPrepStatus(id, PreparedCueState.Idle);

        RaisePreparedCuesChanged();
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
                try { entry.Player.Play(videoOnlyMaster: entry.PlaybackClockMaster); }
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

        // The current pipeline supports file sources; live (NDI/PortAudio input) cues fall back
        // to "not yet wired through the new engine".
        if (cue.Source is not FilePlaylistItem fileItem)
            return $"Live input cues aren't routed through the compositor/mixer yet (source: {cue.Source.GetType().Name}).";

        await StopPreviewAsync().ConfigureAwait(false);

        var list = await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.SelectedCueList?.ToModel());
        if (list is null)
            return "No cue list selected.";

        var plan = BuildRoutePlan(cue);
        if (!plan.HasAnyRoute)
            return "Cue has no audio routes or video placements wired to outputs.";

        // If the same cue id is already running, stop its prior instance. A matching prepared
        // standby instance is kept and consumed below.
        await StopActiveCueAsync(cue.Id).ConfigureAwait(false);

        var cacheKey = BuildPreparedCueKey(cue, list);
        var entry = TryTakePreparedCue(cue.Id, cacheKey);
        if (entry is not null)
        {
            await ReleaseConflictingOutputsAsync(list, plan.AudioByOutput.Keys, plan.PlacementsByComp.Keys)
                .ConfigureAwait(false);

            var wireErr = await WireEntryRoutesAsync(entry, list, plan, startPaused: true).ConfigureAwait(false);
            if (wireErr is not null)
            {
                await DisposeEntryAsync(entry, notifyEnded: false).ConfigureAwait(false);
                return wireErr;
            }
        }
        else
        {
            await RemovePreparedCueAsync(cue.Id).ConfigureAwait(false);
            await ReleaseConflictingOutputsAsync(list, plan.AudioByOutput.Keys, plan.PlacementsByComp.Keys)
                .ConfigureAwait(false);

            var result = await OpenCueEntryAsync(cue, list, plan, wireRoutes: true, ct).ConfigureAwait(false);
            entry = result.Entry;
            if (entry is null)
                return result.Error ?? "Failed to open cue media.";
        }

        lock (_gate)
            _active[cue.Id] = entry;

        if (!deferPlay)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    entry.Player.Play(videoOnlyMaster: entry.PlaybackClockMaster);
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
        targets.AddRange(plan.PlacementsByComp.Keys
            .Select(id => list.Compositions.FirstOrDefault(c => c.Id == id)?.Name ?? "")
            .Where(n => n.Length > 0));
        return $"playing {fileItem.DisplayName} → {string.Join(", ", targets)}";
    }

    private static RoutePlan BuildRoutePlan(MediaCueNode cue)
    {
        // Group audio routes by target output line — each group becomes one shared-runtime source.
        var audioByOutput = cue.AudioRoutes
            .Where(r => r.OutputLineId != Guid.Empty)
            .GroupBy(r => r.OutputLineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Group video placements by composition — one placement per composition per cue.
        var placementsByComp = cue.VideoPlacements
            .Where(p => p.CompositionId != Guid.Empty)
            .GroupBy(p => p.CompositionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.LayerIndex).First());

        return new RoutePlan(audioByOutput, placementsByComp);
    }

    private async Task<(ActiveCue? Entry, string? Error)> OpenCueEntryAsync(
        MediaCueNode cue,
        CueList list,
        RoutePlan plan,
        bool wireRoutes,
        CancellationToken ct)
    {
        if (cue.Source is not FilePlaylistItem fileItem)
            return (null, "Cue source is not a file.");

        // Whether to use MediaPlayer's *internal* audio router. We turn it off only when this
        // cue's audio is being mixed externally via CueAudioOutputRuntime — otherwise we leave
        // it on so the player consumes (and silently drops) any audio stream in the source.
        // Skipping consumption back-pressures the demuxer and starves the video pump, which is
        // what was breaking video playback on a video-with-audio file that had no audio routes
        // wired (e.g. the operator only set up a video placement on it).
        var hasAudioRoutes = plan.AudioByOutput.Count > 0;

        MediaPlayer? player = null;
        string? openErr = null;
        await Task.Run(() =>
        {
            try
            {
                var opts = new MediaPlayerOpenOptions(
                    TryHardwareAcceleration: true,
                    IncludeAudioRouter: !hasAudioRoutes);

                if (!MediaPlayer.OpenFile(fileItem.Path)
                        .WithOptions(opts)
                        .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder)
                        .TryBuild(out player, out openErr))
                    player = null;
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "CuePlaybackEngine: MediaPlayer open threw");
                openErr = ex.Message;
            }
        }, ct).ConfigureAwait(false);

        if (player is null)
            return (null, openErr ?? "Failed to open cue media.");

        var entry = new ActiveCue(cue, player, new CancellationTokenSource(), CueClipWindow.From(cue, player.Duration));

        if (wireRoutes)
        {
            var wireErr = await WireEntryRoutesAsync(entry, list, plan, startPaused: true).ConfigureAwait(false);
            if (wireErr is not null)
            {
                await DisposeEntryAsync(entry, notifyEnded: false).ConfigureAwait(false);
                return (null, wireErr);
            }
        }

        if (entry.ClipWindow.Start > TimeSpan.Zero)
        {
            var seekErr = await SeekEntryToSourcePositionAsync(entry, entry.ClipWindow.Start, ct).ConfigureAwait(false);
            if (seekErr is not null)
            {
                await DisposeEntryAsync(entry, notifyEnded: false).ConfigureAwait(false);
                return (null, seekErr);
            }
        }

        return (entry, null);
    }

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
                WireVideoPlacements(entry, list, plan.PlacementsByComp);
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
        List<PreparedCue> preparedToDispose;
        lock (_gate)
        {
            toDispose = _active.Values.ToList();
            preparedToDispose = _prepared.Values.ToList();
            _active.Clear();
            _prepared.Clear();
        }
        foreach (var entry in toDispose)
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        foreach (var prepared in preparedToDispose)
            await DisposeEntryAsync(prepared.Entry, notifyEnded: false).ConfigureAwait(false);
        RaisePreparedCuesChanged();
    }

    /// <summary>Stop a specific cue.</summary>
    public async Task StopCueAsync(Guid cueId)
    {
        await StopActiveCueAsync(cueId).ConfigureAwait(false);
        await RemovePreparedCueAsync(cueId).ConfigureAwait(false);
    }

    private async Task StopActiveCueAsync(Guid cueId)
    {
        ActiveCue? entry;
        lock (_gate)
        {
            if (!_active.Remove(cueId, out entry))
                return;
        }
        await DisposeEntryAsync(entry).ConfigureAwait(false);
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
                        entry.Player.Play(videoOnlyMaster: entry.PlaybackClockMaster));
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

    private bool HasMatchingPreparedCue(Guid cueId, string cacheKey)
    {
        lock (_gate)
            return _prepared.TryGetValue(cueId, out var prepared)
                   && string.Equals(prepared.CacheKey, cacheKey, StringComparison.Ordinal);
    }

    private ActiveCue? TryTakePreparedCue(Guid cueId, string cacheKey)
    {
        ActiveCue? entry = null;
        lock (_gate)
        {
            if (_prepared.TryGetValue(cueId, out var prepared)
                && string.Equals(prepared.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                _prepared.Remove(cueId);
                entry = prepared.Entry;
            }
        }
        if (entry is not null)
        {
            // Consumed for playback — no longer a standby entry.
            SetPrepStatus(cueId, PreparedCueState.Idle);
            RaisePreparedCuesChanged();
        }
        return entry;
    }

    private async Task StorePreparedCueAsync(Guid cueId, string cacheKey, ActiveCue entry)
    {
        PreparedCue? replaced = null;
        lock (_gate)
        {
            if (_prepared.Remove(cueId, out var existing))
                replaced = existing;
            _prepared[cueId] = new PreparedCue(cacheKey, entry);
        }

        if (replaced is not null)
            await DisposeEntryAsync(replaced.Entry, notifyEnded: false).ConfigureAwait(false);
        RaisePreparedCuesChanged();
    }

    private async Task RemovePreparedCueAsync(Guid cueId)
    {
        PreparedCue? prepared;
        lock (_gate)
            _prepared.Remove(cueId, out prepared);

        if (prepared is null)
            return;

        await DisposeEntryAsync(prepared.Entry, notifyEnded: false).ConfigureAwait(false);
        SetPrepStatus(cueId, PreparedCueState.Idle);
        RaisePreparedCuesChanged();
    }

    private async Task ClearPreparedCuesAsync()
    {
        List<PreparedCue> toDispose;
        bool hadStatuses;
        lock (_gate)
        {
            toDispose = _prepared.Values.ToList();
            _prepared.Clear();
            hadStatuses = _prepStatus.Count > 0;
            _prepStatus.Clear();
        }

        if (toDispose.Count == 0 && !hadStatuses)
            return;

        foreach (var prepared in toDispose)
            await DisposeEntryAsync(prepared.Entry, notifyEnded: false).ConfigureAwait(false);
        if (hadStatuses)
            RaisePreparedCueStatesChanged();
        RaisePreparedCuesChanged();
    }

    private async Task EvictPreparedExceptAsync(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        List<PreparedCue> toDispose = new();
        lock (_gate)
        {
            var keep = keepCueIds is HashSet<Guid> hs ? hs : keepCueIds.ToHashSet();
            foreach (var id in _prepared.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                toDispose.Add(_prepared[id]);
                _prepared.Remove(id);
            }

            while (_prepared.Count > maxEntries)
            {
                var oldest = _prepared.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                toDispose.Add(_prepared[oldest]);
                _prepared.Remove(oldest);
            }
        }

        if (toDispose.Count == 0)
            return;

        foreach (var prepared in toDispose)
        {
            await DisposeEntryAsync(prepared.Entry, notifyEnded: false).ConfigureAwait(false);
            SetPrepStatus(prepared.Entry.Cue.Id, PreparedCueState.Idle);
        }
        RaisePreparedCuesChanged();
    }

    private void RaisePreparedCuesChanged()
    {
        Guid[] snapshot;
        lock (_gate)
            snapshot = _prepared.Keys.ToArray();
        try { PreparedCuesChanged?.Invoke(snapshot); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: PreparedCuesChanged handler"); }
    }

    private async Task DisposeEntryAsync(ActiveCue entry, bool notifyEnded = true)
    {
        try { entry.Cts.Cancel(); } catch { /* best effort */ }
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
                try { entry.Player.Dispose(); }
                catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine: player dispose"); }

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

    private void ReleaseEmptyRuntimes()
    {
        List<KeyValuePair<Guid, CueCompositionRuntime>> emptyComps;
        List<KeyValuePair<Guid, CueAudioOutputRuntime>> emptyAudio;
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

    private void WireAudioRoutes(ActiveCue entry, Dictionary<Guid, List<CueAudioRoute>> audioByOutput)
    {
        if (audioByOutput.Count == 0) return;

        S.Media.Core.Audio.IAudioSource decoderAudio;
        try
        {
            if (!entry.Player.Bundle.Decoder.HasAudio)
                return;
            decoderAudio = entry.Player.Bundle.Decoder.Audio;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WireAudioRoutes: source has no audio");
            return;
        }

        AudioSourceFanout? fanout = null;
        if (audioByOutput.Count > 1)
        {
            fanout = new AudioSourceFanout(decoderAudio);
            entry.AudioDisposables.Add(fanout);
        }

        foreach (var (lineId, routes) in audioByOutput)
        {
            var runtime = GetOrCreateAudioRuntime(lineId);
            if (runtime is null) continue;

            var routedSource = fanout is null
                ? decoderAudio
                : fanout.CreateBranch();
            var pausable = new PausableAudioSource(routedSource, disposeInner: fanout is not null)
            {
                IsPaused = entry.IsPaused,
            };
            entry.PausableAudioSources.Add(pausable);
            entry.AudioDisposables.Add(pausable);

            var srcId = runtime.AddSource(pausable, routes, sourceIdHint: $"cue_{entry.Cue.Id:N}_{entry.InstanceId:N}");
            entry.AudioSources.Add((runtime, srcId));
            if (runtime.PlaybackClock is { } playbackClock)
                entry.PlaybackClockMaster ??= playbackClock;
        }
    }

    private void WireVideoPlacements(
        ActiveCue entry,
        CueList list,
        Dictionary<Guid, CueVideoPlacement> placementsByComp)
    {
        if (placementsByComp.Count == 0) return;

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

        foreach (var (compId, placement) in placementsByComp)
        {
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

            var outId = router.AddOutput(layerOutput, id: $"cuecomp_{entry.Cue.Id:N}_{entry.InstanceId:N}_{compId:N}",
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

    private CueAudioOutputRuntime? GetOrCreateAudioRuntime(Guid outputLineId)
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
            var runtime = new CueAudioOutputRuntime(line, _outputs);
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

    private async Task<string?> SeekEntryToSourcePositionAsync(ActiveCue entry, TimeSpan sourcePosition, CancellationToken ct)
    {
        try
        {
            await Task.Run(() =>
                entry.Player.SeekCoordinated(sourcePosition, CancellationToken.None, PauseFlushPolicy.SkipFlush), ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "CuePlaybackEngine: start-offset seek failed");
            return ex.Message;
        }
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
                entry.Player.Play(videoOnlyMaster: entry.PlaybackClockMaster);
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
                    await Dispatcher.UIThread.InvokeAsync(() => NaturalEnd?.Invoke(this, EventArgs.Empty));
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

    public void Dispose()
    {
        try { StopPreviewAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }

        List<CueCompositionRuntime> compsLeft;
        List<CueAudioOutputRuntime> audioLeft;
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
                p.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
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

    private sealed record RoutePlan(
        Dictionary<Guid, List<CueAudioRoute>> AudioByOutput,
        Dictionary<Guid, CueVideoPlacement> PlacementsByComp)
    {
        public bool HasAnyRoute => AudioByOutput.Count > 0 || PlacementsByComp.Count > 0;
    }

    private sealed record PreparedCue(string CacheKey, ActiveCue Entry, DateTime CreatedUtc)
    {
        public PreparedCue(string cacheKey, ActiveCue entry)
            : this(cacheKey, entry, DateTime.UtcNow)
        {
        }
    }

    private sealed class ActiveCue
    {
        public ActiveCue(MediaCueNode cue, MediaPlayer player, CancellationTokenSource cts, ClipWindow clipWindow)
        {
            Cue = cue;
            Player = player;
            Cts = cts;
            ClipWindow = clipWindow;
        }

        public MediaCueNode Cue { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public MediaPlayer Player { get; }
        public CancellationTokenSource Cts { get; }
        public ClipWindow ClipWindow { get; }

        /// <summary>The cue's audio-runtime playback clock, captured from the first wired
        /// <see cref="CueAudioOutputRuntime.PlaybackClock"/>. Used as the composition master
        /// (<see cref="CueCompositionRuntime.SetClockMaster"/>) and passed to
        /// <c>MediaPlayer.Play(videoOnlyMaster:)</c> so video presents at the audio clock's rate.
        /// Null for video-only cues, which then free-run on their own clock.</summary>
        public IPlaybackClock? PlaybackClockMaster { get; set; }
        public bool IsPaused { get; set; }
        public bool RoutesWired { get; set; }
        public List<CueCompositionRuntime.LayerSlot> LayerSlots { get; } = new();
        public List<BgraConvertingVideoOutput> ConvertingOutputs { get; } = new();
        public List<(CueAudioOutputRuntime Runtime, string SourceId)> AudioSources { get; } = new();
        public List<PausableAudioSource> PausableAudioSources { get; } = new();
        public List<IDisposable> AudioDisposables { get; } = new();
    }
}

/// <summary>Periodic progress sample for the Now Playing panel.</summary>
public readonly record struct CuePlaybackProgress(Guid CueId, TimeSpan Position, TimeSpan Duration);

/// <summary>Standby preparation lifecycle for one cue. <c>Idle</c> = not in the warm window or not
/// attempted; <c>Preparing</c> = opening/seeking; <c>Ready</c> = opened, routed, seeked to start;
/// <c>Failed</c> = open failed (reason in <see cref="CuePreparationStatus.Error"/>). A stale entry
/// (config changed) is re-prepared, transiting back through <c>Preparing</c>.</summary>
public enum PreparedCueState
{
    Idle,
    Preparing,
    Ready,
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
