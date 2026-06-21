using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;
using Microsoft.Extensions.Logging;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.NDI;
using S.Media.PortAudio;

namespace HaPlay.ViewModels;

internal sealed record VideoOutputRouteConflict(
    MediaPlayerViewModel TargetPlayer,
    OutputLineViewModel OutputLine,
    IReadOnlyList<MediaPlayerViewModel> ExistingPlayers);

public partial class MediaPlayerViewModel : ViewModelBase
{
    private readonly OutputManagementViewModel _outputs;
    private readonly Func<MediaPlayerViewModel, Task>? _requestRemove;
    private HaPlayPlaybackSession? _session;
    private IDisposable? _sleepInhibitLease;
    private readonly PlaybackThroughputDiagnostics _throughputDiagnostics = new();
    private int _disposeStarted;

    /// <summary>
    /// Machine-wide preference (saved in <see cref="AppSettings"/>). When on, live NDI keeps native UYVY
    /// into local video outputs; when off, frames are converted to BGRA32 before display (default).
    /// </summary>
    [ObservableProperty]
    private bool _preferLiveUyvyPassthrough;

    /// <summary>Active playback session when media is loaded (for output health probes).</summary>
    internal HaPlayPlaybackSession? PlaybackSession => _session;
    private DispatcherTimer? _loopTimer;
    private IdleLogoSlateSession? _idleSlate;
    private string? _idleSlateSig;
    private readonly DispatcherTimer _idleSlateSyncTimer;
    /// <summary>Phase 2B — runs on a threadpool tick instead of the UI dispatcher so transport gate
    /// holds don't stall the hold-image pump (NDI receivers would briefly freeze on every Pause/Play).</summary>
    private Timer? _holdPumpTimer;
    private int _holdPumpReentry;
    private PlaylistTabViewModel? _activePlaybackTab;
    private int _suppressVideoRouteConflictPrompt;

    internal Func<VideoOutputRouteConflict, Task<bool>> VideoOutputRouteConflictPrompt { get; set; } =
        DefaultVideoOutputRouteConflictPromptAsync;

    /// <summary>When true, natural file-end raises <see cref="NaturalPlaybackEnded"/> instead of playlist auto-advance.</summary>
    private bool _cuePlaybackActive;

    /// <summary>Raised when a file-backed session reaches natural end (not live, not looping).</summary>
    public event EventHandler? NaturalPlaybackEnded;

    public void SetCuePlaybackActive(bool active)
    {
        _cuePlaybackActive = active;
        if (!active)
        {
            _activeCueEndBehavior = CueEndBehavior.Stop;
            CancelCueEnvelope();
        }
    }

    /// <summary>Apply loop/end-behavior flags before opening a cue (§5.2).</summary>
    public void ConfigureCueTransport(MediaCueNode cue)
    {
        _cuePlaybackActive = true;
        _activeCueEndBehavior = cue.EndBehavior;
        IsLooping = cue.Loop || cue.EndBehavior == CueEndBehavior.Loop;
    }

    private CueEndBehavior _activeCueEndBehavior = CueEndBehavior.Stop;

    public void InvalidateCuePreRoll()
    {
        _cuePreRoll.InvalidateAll();
        _ndiPreConnect.InvalidateAll();
        _paPreConnect.InvalidateAll();
    }

    public HaPlayFilePlaybackOptions CurrentFilePlaybackOptions() =>
        new(
            OutputPreset,
            TransitionMode,
            TransitionDurationMs,
            CustomOutputWidth: SanitizedCustomOutputWidth(),
            CustomOutputHeight: SanitizedCustomOutputHeight());

    public HaPlayFilePlaybackOptions FilePlaybackOptionsForCue(MediaCueNode cue) =>
        new(OutputPreset, TransitionMode, TransitionDurationMs,
            Math.Max(0, cue.FadeInMs), Math.Max(0, cue.FadeOutMs),
            SanitizedCustomOutputWidth(), SanitizedCustomOutputHeight());

    public void CancelCueEnvelope()
    {
        try { _cueEnvelopeCts?.Cancel(); } catch { /* best effort */ }
        try { _cueEnvelopeCts?.Dispose(); } catch { /* best effort */ }
        _cueEnvelopeCts = null;
        _cueEnvelope = 1f;
        _cueVideoOpacity = 1f;
        _session?.SetLogoOutputOpacity(1f);
    }

    /// <summary>Ramps master×output compound gain for per-cue <see cref="MediaCueNode.FadeInMs"/> /
    /// <see cref="MediaCueNode.FadeOutMs"/> (audio + video via logo opacity when video is routed).</summary>
    public void BeginCueFades(MediaCueNode cue)
    {
        CancelCueEnvelope();
        if (cue.FadeInMs <= 0 && cue.FadeOutMs <= 0)
            return;
        _cueEnvelopeCts = new CancellationTokenSource();
        _ = RunCueEnvelopeAsync(cue, _cueEnvelopeCts.Token);
    }

    private async Task RunCueEnvelopeAsync(MediaCueNode cue, CancellationToken ct)
    {
        try
        {
            if (cue.FadeInMs > 0)
            {
                _cueEnvelope = 0f;
                _cueVideoOpacity = 0f;
                await ApplyCueEnvelopeToSessionAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < cue.FadeInMs)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = (float)Math.Min(1.0, sw.ElapsedMilliseconds / (double)cue.FadeInMs);
                    _cueEnvelope = t;
                    _cueVideoOpacity = t;
                    await ApplyCueEnvelopeToSessionAsync();
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                _cueEnvelope = 1f;
                _cueVideoOpacity = 1f;
                await ApplyCueEnvelopeToSessionAsync();
            }

            if (cue.FadeOutMs > 0)
            {
                var duration = await Dispatcher.UIThread.InvokeAsync(() => Duration);
                if (duration <= TimeSpan.Zero)
                    return;

                var fadeOutStart = duration - TimeSpan.FromMilliseconds(cue.FadeOutMs);
                while (!ct.IsCancellationRequested)
                {
                    var pos = await Dispatcher.UIThread.InvokeAsync(() => CurrentPosition);
                    if (!IsPlaying || pos >= fadeOutStart)
                        break;
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }

                var swOut = System.Diagnostics.Stopwatch.StartNew();
                while (swOut.ElapsedMilliseconds < cue.FadeOutMs && !ct.IsCancellationRequested)
                {
                    var stillPlaying = await Dispatcher.UIThread.InvokeAsync(() => IsPlaying);
                    if (!stillPlaying)
                        break;
                    var t = 1f - (float)Math.Min(1.0, swOut.ElapsedMilliseconds / (double)cue.FadeOutMs);
                    _cueEnvelope = t;
                    _cueVideoOpacity = t;
                    await ApplyCueEnvelopeToSessionAsync();
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* stop/panic/next cue */
        }
        finally
        {
            _cueEnvelope = 1f;
            _cueVideoOpacity = 1f;
            await ApplyCueEnvelopeToSessionAsync();
        }
    }

    private float _cueVideoOpacity = 1f;

    private async Task ApplyCueEnvelopeToSessionAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyAllOutputGainsToSession();
            _session?.SetLogoOutputOpacity(_cueVideoOpacity);
        });
    }

    /// <summary>GO path — adopt pre-opened file/NDI when cache matches; otherwise open normally.</summary>
    public async Task<bool> TryPlayCueMediaAsync(MediaCueNode cue, CancellationToken ct = default)
    {
        if (cue.Source is null)
            return false;

        var adopted = cue.Source switch
        {
            NDIInputPlaylistItem ndi => await TryPlayNdiCueAsync(cue, ndi, ct).ConfigureAwait(false),
            PortAudioInputPlaylistItem pa => await TryPlayPortAudioCueAsync(cue, pa, ct).ConfigureAwait(false),
            _ => await TryPlayFileCueAsync(cue, ct).ConfigureAwait(false),
        };

        await ApplyCueTransportAsync(cue, ct).ConfigureAwait(false);
        return adopted;
    }

    private async Task<bool> TryPlayFileCueAsync(MediaCueNode cue, CancellationToken ct)
    {
        var lines = await Dispatcher.UIThread.InvokeAsync(SelectedOutputLines);
        var fileOpts = FilePlaybackOptionsForCue(cue);
        var cacheKey = CuePreRollCache.BuildCacheKey(cue.Source!, lines, fileOpts);
        if (_cuePreRoll.TryTake(cue.Id, cacheKey, out var session, out var item) && session is not null && item is not null)
        {
            // Cue executors run on pool threads — observable property sets must go via the dispatcher.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _activePlaybackTab = SelectedPlaylistTab;
                SelectedPlaylistItem = item;
            });
            await AdoptPreRolledSessionAsync(session, item, cue, ct).ConfigureAwait(false);
            if (!IsPlaying)
                await StartPlaybackAsync().ConfigureAwait(false);
            return true;
        }

        _pendingCueFilePlayback = fileOpts;
        try
        {
            await PlayPlaylistItemAsync(cue.Source!).ConfigureAwait(false);
        }
        finally
        {
            _pendingCueFilePlayback = null;
        }

        return false;
    }

    private async Task<bool> TryPlayNdiCueAsync(MediaCueNode cue, NDIInputPlaylistItem ndi, CancellationToken ct)
    {
        var cacheKey = NdiInputPreConnectCache.BuildCacheKey(ndi);
        if (_ndiPreConnect.TryTake(cue.Id, cacheKey, out var receiver, out _))
        {
            await OpenPreconnectedNdiAsync(ndi, receiver!, cue, ct).ConfigureAwait(false);
            if (!IsPlaying)
                await StartPlaybackAsync().ConfigureAwait(false);
            return true;
        }

        await PlayPlaylistItemAsync(ndi).ConfigureAwait(false);
        return false;
    }

    private async Task OpenPreconnectedNdiAsync(
        NDIInputPlaylistItem item,
        NDISource receiver,
        MediaCueNode cueRoutes,
        CancellationToken ct)
    {
        _ = ct;
        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: false).ConfigureAwait(false);

            var lines = await Dispatcher.UIThread.InvokeAsync(SelectedOutputLines);
            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreateLive(item, lines, _outputs, receiver, out created, out createErr))
                    created = null;
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    StatusMessage = createErr ?? "Failed to open pre-connected NDI.";
                    try { receiver.Dispose(); } catch { /* best effort */ }
                    return;
                }

                StopIdleSlate();
                _outputs.StopPreviewsForPlayback(lines);
                _currentPlaylistItem = item;
                MediaFilePath = null;
                OnPropertyChanged(nameof(CurrentMediaDisplay));
                _session = created;
                IsMediaLoaded = true;
                Duration = TimeSpan.Zero;
                StatusMessage = null;
                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                ApplyCueRouteOverrides(cueRoutes);
                SyncMatrixSourceChannelsFromSession(created);
                ResizeSelectedAudioMatrices(created);
                RebuildAudioMatrixRows();
                ApplyAllOutputMatricesToSession();
                ApplyAllOutputGainsToSession();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private async Task ApplyCueTransportAsync(MediaCueNode cue, CancellationToken ct)
    {
        if (_session?.IsLive != true)
        {
            var clip = CueClipWindow.From(cue, Duration);
            if (clip.Start > TimeSpan.Zero)
                await SeekToTimeAsync(clip.Start, ct).ConfigureAwait(false);
        }

        BeginCueFades(cue);
    }

    public async Task SeekToTimeAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_session is null || _session.IsLive || Duration <= TimeSpan.Zero)
            return;

        var t = position;
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        if (t > Duration)
            t = Duration;

        await WithPlaybackArcAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var (session, playing, holdFb) = await Dispatcher.UIThread.InvokeAsync(() =>
                (_session, IsPlaying, HoldFallbackVideo));
            if (session is null)
                return;

            await RunFileSeekTransportAsync(session, t, playing, holdFb).ConfigureAwait(false);

            if (!playing)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentPosition = t;
                    if (Duration > TimeSpan.Zero)
                        SeekSliderValue = t.Ticks * 1000.0 / Duration.Ticks;
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HoldFallbackVideo)
                    StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private async Task<bool> TryPlayPortAudioCueAsync(MediaCueNode cue, PortAudioInputPlaylistItem pa, CancellationToken ct)
    {
        var cacheKey = PortAudioInputPreConnectCache.BuildCacheKey(pa);
        if (_paPreConnect.TryTake(cue.Id, cacheKey, out var input, out _))
        {
            await OpenPreconnectedPortAudioAsync(pa, input!, cue, ct).ConfigureAwait(false);
            if (!IsPlaying)
                await StartPlaybackAsync().ConfigureAwait(false);
            return true;
        }

        await PlayPlaylistItemAsync(pa).ConfigureAwait(false);
        return false;
    }

    private async Task OpenPreconnectedPortAudioAsync(
        PortAudioInputPlaylistItem item,
        PortAudioInput input,
        MediaCueNode cueRoutes,
        CancellationToken ct)
    {
        _ = ct;
        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: false).ConfigureAwait(false);

            var lines = await Dispatcher.UIThread.InvokeAsync(SelectedOutputLines);
            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreateLive(item, lines, _outputs, input, out created, out createErr))
                    created = null;
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    StatusMessage = createErr ?? "Failed to open pre-connected PortAudio.";
                    try { input.Dispose(); } catch { /* best effort */ }
                    return;
                }

                StopIdleSlate();
                _outputs.StopPreviewsForPlayback(lines);
                _currentPlaylistItem = item;
                MediaFilePath = null;
                OnPropertyChanged(nameof(CurrentMediaDisplay));
                _session = created;
                IsMediaLoaded = true;
                Duration = TimeSpan.Zero;
                StatusMessage = null;
                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                ApplyCueRouteOverrides(cueRoutes);
                SyncMatrixSourceChannelsFromSession(created);
                ResizeSelectedAudioMatrices(created);
                RebuildAudioMatrixRows();
                ApplyAllOutputMatricesToSession();
                ApplyAllOutputGainsToSession();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    public async Task RefreshPortAudioPreConnectAsync(
        IReadOnlyList<(Guid CueId, PortAudioInputPlaylistItem Item)> targets,
        CancellationToken ct = default)
    {
        if (targets.Count == 0 || IsPlaying)
            return;

        var keepIds = targets.Select(t => t.CueId).ToHashSet();
        foreach (var (cueId, item) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var cacheKey = PortAudioInputPreConnectCache.BuildCacheKey(item);
            if (_paPreConnect.HasMatchingEntry(cueId, cacheKey))
                continue;

            PortAudioInput? input = null;
            AudioFormat format = default;
            string? err = null;
            await Task.Run(() =>
            {
                if (!PortAudioInputConnector.TryOpen(item, out input, out format, out err))
                    input = null;
            }, ct).ConfigureAwait(false);

            if (input is not null)
                _paPreConnect.Store(cueId, cacheKey, input, format);
        }

        _paPreConnect.EvictExcept(keepIds, Math.Max(1, targets.Count));
    }

    public async Task RefreshNdiPreConnectAsync(
        IReadOnlyList<(Guid CueId, NDIInputPlaylistItem Item)> targets,
        CancellationToken ct = default)
    {
        if (targets.Count == 0 || IsPlaying)
            return;

        var keepIds = targets.Select(t => t.CueId).ToHashSet();
        foreach (var (cueId, item) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var cacheKey = NdiInputPreConnectCache.BuildCacheKey(item);
            if (_ndiPreConnect.HasMatchingEntry(cueId, cacheKey))
                continue;

            NDISource? receiver = null;
            AudioFormat format = default;
            string? err = null;
            await Task.Run(() =>
            {
                if (!NdiInputConnector.TryConnectLive(item, out receiver, out format, out _, out err))
                    receiver = null;
            }, ct).ConfigureAwait(false);

            if (receiver is not null)
                _ndiPreConnect.Store(cueId, cacheKey, receiver, format);
        }

        _ndiPreConnect.EvictExcept(keepIds, Math.Max(1, targets.Count));
    }

    private HaPlayFilePlaybackOptions? _pendingCueFilePlayback;

    public async Task RefreshCuePreRollAsync(
        IReadOnlyList<(Guid CueId, PlaylistItem Item, int FadeInMs, int FadeOutMs)> targets,
        CancellationToken ct = default)
    {
        if (targets.Count == 0 || IsPlaying)
            return;

        var lines = await Dispatcher.UIThread.InvokeAsync(SelectedOutputLines);
        var keepIds = targets.Select(t => t.CueId).ToHashSet();

        foreach (var (cueId, item, fadeIn, fadeOut) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var fileOpts = new HaPlayFilePlaybackOptions(
                OutputPreset, TransitionMode, TransitionDurationMs, fadeIn, fadeOut,
                SanitizedCustomOutputWidth(), SanitizedCustomOutputHeight());
            var cacheKey = CuePreRollCache.BuildCacheKey(item, lines, fileOpts);
            if (_cuePreRoll.HasMatchingEntry(cueId, cacheKey))
                continue;

            HaPlayPlaybackSession? created = null;
            string? err = null;
            await Task.Run(() =>
            {
                var preOpened = item is FilePlaylistItem fi ? _decoderCache.TryTake(fi.Path, fi.AudioTrackIndex) : null;
                if (!HaPlayPlaybackSession.TryCreate(item, lines, _outputs, out created, out err, fileOpts, preOpened))
                    created = null;
            }, ct).ConfigureAwait(false);

            if (created is not null)
                _cuePreRoll.Store(cueId, cacheKey, created, item);
        }

        _cuePreRoll.EvictExcept(keepIds, Math.Max(1, targets.Count));
    }
    private bool _syncingPlaylistTabState;
    private readonly ObservableCollection<PlaylistItem> _emptyPlaylistItems = new();

    /// <summary>Phase C.5 (§6.8) — the playlist entry currently loaded into the session. Tracks the
    /// active item across <see cref="PlaylistItem"/> kinds so navigation (next/prev/end-of-track
    /// auto-advance) works for both files and live inputs.</summary>
    private PlaylistItem? _currentPlaylistItem;

    /// <summary>
    /// Trim settings loaded from config, keyed by input channel. Applied whenever the matrix is resized.
    /// </summary>
    private Dictionary<int, InputChannelTrimConfig> _pendingInputTrimConfigs = new();

    /// <summary>Serializes load/unload/stop/pause/play/seek and loop-timer Router use so Dispose cannot overlap transport.</summary>
    private readonly SemaphoreSlim _playbackArc = new(1, 1);
    /// <summary>Wall-clock cap for file seeks. Must exceed the shared-demux prime deadline (~4 s) plus pause/join.</summary>
    private static readonly TimeSpan FileSeekWallTimeout = TimeSpan.FromSeconds(12);
    /// <summary>
    /// Wall-clock cap for a Play/resume. Must exceed AvPlaybackCoordinator.Play's internal
    /// WaitForVideoBufferBeforeStartingAudio budget (8 s) plus the sync-present + hardware-start tail; otherwise
    /// the UI abandons (IsPlaying stays false) while the background Play actually completes a moment later,
    /// leaving audio running with a "Play" button — the user then has to wait out the lock and retry.
    /// </summary>
    private static readonly TimeSpan PlayWallTimeout = TimeSpan.FromSeconds(11);
    private volatile bool _isTransportBusy;
    private readonly Playback.PlaylistDecoderCache _decoderCache = new();
    private CancellationTokenSource? _preOpenCts;
    private readonly CuePreRollCache _cuePreRoll = new();

    /// <summary>Forwarded from the pre-roll cache so the Cue Player can light warming badges
    /// on the affected rows (Phase 5.7.2). Snapshot of currently-warm cue ids.</summary>
    public event Action<IReadOnlyCollection<Guid>>? CuePreRollChanged
    {
        add => _cuePreRoll.EntriesChanged += value;
        remove => _cuePreRoll.EntriesChanged -= value;
    }

    /// <summary>Snapshot of currently-warm cue ids — used for initial UI sync.</summary>
    public IReadOnlyCollection<Guid> CuePreRollSnapshot() => _cuePreRoll.SnapshotWarmCueIds();
    private readonly NdiInputPreConnectCache _ndiPreConnect = new();
    private readonly PortAudioInputPreConnectCache _paPreConnect = new();
    private float _cueEnvelope = 1f;
    private CancellationTokenSource? _cueEnvelopeCts;

    /// <summary>Phase C.5 (§6.9) — switch into the "waiting for source" state. Surfaces a status banner,
    /// stamps the next retry deadline, and ensures the loop timer is running so the deadline ticks.
    /// Caller has already cleared the failed <see cref="_session"/>.</summary>
    private void EnterWaitingForSource(PlaylistItem item, string reason)
    {
        _waitingItem = item;
        IsWaitingForSource = true;
        LoadState = PlayerLoadState.WaitingForSource;
        var retrySec = GetRetrySeconds(item);
        if (retrySec > 0)
        {
            _nextRetryAt = DateTime.UtcNow.AddSeconds(retrySec);
            WaitingForSourceMessage = $"WAITING: {item.DisplayName} — {reason} (retry in {retrySec}s).";
            EnsureLoopTimerStarted();
        }
        else
        {
            WaitingForSourceMessage = $"WAITING: {item.DisplayName} — {reason}.";
        }
        StatusMessage = WaitingForSourceMessage;
    }

    private void ExitWaitingForSource()
    {
        if (!IsWaitingForSource && _waitingItem is null)
            return;
        _waitingItem = null;
        IsWaitingForSource = false;
        WaitingForSourceMessage = null;
    }

    /// <summary>Phase C.5 — per-kind retry interval. Files never retry (0). NDI inputs use their saved
    /// <see cref="NDIInputPlaylistItem.RetrySeconds"/>. PortAudio inputs retry on a fixed 2 s cadence —
    /// device disappearance is rare and PortAudio doesn't have a discovery handshake to wait on.</summary>
    private static int GetRetrySeconds(PlaylistItem item) => item switch
    {
        NDIInputPlaylistItem ndi => ndi.RetrySeconds,
        PortAudioInputPlaylistItem => 2,
        _ => 0,
    };

    /// <summary>Phase C.5 — return the active source's channel count regardless of whether it was
    /// opened via container decoder (files) or via <see cref="MediaPlayer.TryOpenLive"/> (live items).
    /// Live sessions surface the negotiated format on <see cref="HaPlayPlaybackSession.SourceAudioFormat"/>;
    /// file sessions still have a real <c>Decoder</c> and can read it from there. Returns 0 when
    /// nothing is loaded or the source has no known audio format.</summary>
    private static int SourceChannelCountOrZero(HaPlayPlaybackSession? session)
    {
        if (session is null) return 0;
        if (session.IsLive)
            return session.SourceAudioFormat.Channels > 0 ? session.SourceAudioFormat.Channels : 0;
        if (session.Player.HasContainerDecoder && session.Player.Decoder.Audio is { } a)
            return a.Format.Channels;
        return 0;
    }

    private int MatrixInputChannelCountFor(HaPlayPlaybackSession? session)
    {
        if (_audioMatrixSourceChannelsExplicit)
            return Math.Clamp(AudioMatrixSourceChannels, 1, 64);

        var sourceChannels = SourceChannelCountOrZero(session);
        return sourceChannels > 0
            ? Math.Clamp(sourceChannels, 1, 64)
            : Math.Clamp(AudioMatrixSourceChannels, 1, 64);
    }

    private void ResizeSelectedAudioMatrices(HaPlayPlaybackSession? session)
    {
        var inputChannels = MatrixInputChannelCountFor(session);
        var anyInputCountChanged = false;
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            var before = binding.Matrix.InputChannelCount;
            binding.Matrix.Resize(inputChannels, OutputChannelCountOrZero(binding.Line));
            anyInputCountChanged |= before != binding.Matrix.InputChannelCount;
        }

        // P5b: a rule fires only when the channel COUNT changes (e.g. stereo show, then a 5.1 file
        // arrives). Same-count reloads keep whatever the operator hand-tuned for that layout.
        if (anyInputCountChanged)
            ApplyChannelPresetRuleIfMatching(inputChannels);
    }

    private void SyncMatrixSourceChannelsFromSession(HaPlayPlaybackSession? session)
    {
        if (_audioMatrixSourceChannelsExplicit)
            return;

        var sourceChannels = SourceChannelCountOrZero(session);
        if (sourceChannels <= 0)
            return;

        SetAudioMatrixSourceChannels(sourceChannels, explicitValue: false, resize: false);
    }

    private void SetAudioMatrixSourceChannels(int channels, bool explicitValue, bool resize)
    {
        var clamped = Math.Clamp(channels, 1, 64);
        _updatingAudioMatrixSourceChannels = true;
        try
        {
            AudioMatrixSourceChannels = clamped;
        }
        finally
        {
            _updatingAudioMatrixSourceChannels = false;
        }

        _audioMatrixSourceChannelsExplicit = explicitValue;
        if (resize)
        {
            ResizeSelectedAudioMatrices(_session);
            RebuildAudioMatrixRows();
            ApplyAllOutputMatricesToSession();
        }
    }

    /// <summary>
    /// Configured output channel count for one output line. Audio-capable outputs return at least 1.
    /// Video-only outputs return 0 so they drop out of matrix/route grids.
    /// </summary>
    private int OutputChannelCountOrZero(OutputLineViewModel line)
    {
        if (_session?.TryGetEffectiveOutputChannelCount(line, out var effective) == true)
            return effective;

        return line.Definition switch
        {
            PortAudioOutputDefinition pa => Math.Max(1, pa.ChannelCount),
            NDIOutputDefinition { StreamMode: NDIOutputStreamMode.VideoOnly } => 0,
            NDIOutputDefinition nd => Math.Max(1, nd.AudioChannelCount),
            _ => 0,
        };
    }

    private static string OutputChannelSuffix(int outputChannels, int outputChannel) =>
        outputChannels == 2
            ? $"Out {(outputChannel == 0 ? "L" : "R")}"
            : $"Out {outputChannel + 1}";

    private AudioMatrixInputTrimViewModel? InputTrim(int inputChannel) =>
        AudioMatrixInputTrims.FirstOrDefault(t => t.InputChannel == inputChannel);

    private (double GainDb, bool Muted) InputTrimValues(int inputChannel)
    {
        var trim = InputTrim(inputChannel);
        return trim is null ? (0.0, false) : (trim.GainDb, trim.Muted);
    }

    private string EffectiveCellGainText(PlayerOutputBinding binding, AudioMatrixCellViewModel cell)
    {
        var (inputTrimDb, inputTrimMuted) = InputTrimValues(cell.InputChannel);
        if (MasterMuted || binding.IsMuted || cell.Muted || inputTrimMuted)
            return "-inf dB";
        var effective = MasterVolumeDb + binding.GainDb + inputTrimDb + cell.GainDb;
        return $"{effective:0.#} dB";
    }

    private async Task WithPlaybackArcAsync(Func<Task> action)
    {
        var arcWaitStart = Stopwatch.GetTimestamp();
        await _playbackArc.WaitAsync().ConfigureAwait(false);
        if (SDebug.ChangeTrace.IsActive)
        {
            var waitedMs = SDebug.ChangeTrace.TicksToMs(Stopwatch.GetTimestamp() - arcWaitStart);
            SDebug.ChangeTrace.Step($"_playbackArc acquired (waited {waitedMs:F1}ms)");
        }

        _isTransportBusy = true;
        Dispatcher.UIThread.Post(NotifyTransportCanExecuteChanged);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _isTransportBusy = false;
            _playbackArc.Release();
            Dispatcher.UIThread.Post(NotifyTransportCanExecuteChanged);
            SDebug.ChangeTrace.Step("_playbackArc released");
        }
    }

    public MediaPlayerViewModel(OutputManagementViewModel outputs, string name,
        Func<MediaPlayerViewModel, Task>? requestRemove = null)
    {
        _outputs = outputs;
        _requestRemove = requestRemove;
        Name = name;
        SyncOutputsCollection();
        _outputs.Outputs.CollectionChanged += OnSharedOutputsCollectionChanged;
        _outputs.SharedHeadphonesBusesChanged += OnSharedHeadphonesBusesChanged;
        // Phase B (§3.4) — also resync on definition changes (Edit) so clone-of transitions update
        // the routing checkbox list. CollectionChanged alone misses Edit-driven topology changes.
        _outputs.RoutingTopologyChanged += OnRoutingTopologyChanged;
        _outputs.OutputNamingChanged += OnOutputNamingChanged;
        // Phase B follow-up — unwire from the active session BEFORE the runtime is disposed (§4.3.3).
        // Without this the AudioRouter pump keeps Submit'ing to a disposed PortAudioOutput and spams
        // ObjectDisposedException until the session is torn down.
        _outputs.OutputLineRemoving += OnOutputLineRemoving;
        _outputs.OutputLineReconfiguringAsync += OnOutputLineReconfiguringAsync;
        _outputs.OutputLineReconfiguredAsync += OnOutputLineReconfiguredAsync;
        _idleSlateSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _idleSlateSyncTimer.Tick += (_, _) => SyncIdleSlate();
        _idleSlateSyncTimer.Start();
        _preferLiveUyvyPassthrough = PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo;
        var initialTab = new PlaylistTabViewModel("Set A");
        PlaylistTabs.Add(initialTab);
        SelectedPlaylistTab = initialTab;
        Dispatcher.UIThread.Post(() => SyncIdleSlate(), DispatcherPriority.Loaded);
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ReleasePlaybackSleepInhibitor();
            _idleSlateSyncTimer.Stop();
            StopHoldPumpTimer();
            CancelCueEnvelope();
            try { _statusMessageClearCts?.Cancel(); } catch { /* best effort */ }
            try { _statusMessageClearCts?.Dispose(); } catch { /* best effort */ }
            _statusMessageClearCts = null;
            CancelPreOpen();
            CancelWaveformExtraction();
            StopIdleSlate();
            UnsubscribeOutputEvents();
            DetachPlaylistTabSelection();
            DetachOutputBindings();
            UnwatchInputTrimRows();
        });

        await CloseSessionAsync().ConfigureAwait(false);
    }

    private void CancelPreOpen()
    {
        try { _preOpenCts?.Cancel(); } catch { /* best effort */ }
        try { _preOpenCts?.Dispose(); } catch { /* best effort */ }
        _preOpenCts = null;
    }

    private void CancelWaveformExtraction()
    {
        var pending = CancelWaveformExtractionCore();
        DisposeWaveformCancellationWhenComplete(pending.Task, pending.Cts);
    }

    private (Task? Task, CancellationTokenSource? Cts) CancelWaveformExtractionCore()
    {
        var task = _waveformTask;
        var cts = _waveformCts;
        _waveformTask = null;
        _waveformCts = null;
        try { cts?.Cancel(); } catch { /* best effort */ }
        IsExtractingWaveform = false;
        return (task, cts);
    }

    private async Task CancelWaveformExtractionAndWaitAsync()
    {
        var pending = await Dispatcher.UIThread.InvokeAsync(CancelWaveformExtractionCore);
        if (pending.Task is not null)
        {
            try { await pending.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { TransportTrace.LogWarning(ex, "Waveform extraction failed while cancelling"); }
        }

        try { pending.Cts?.Dispose(); } catch { /* best effort */ }
    }

    private static void DisposeWaveformCancellationWhenComplete(Task? task, CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        if (task is { IsCompleted: false })
        {
            _ = task.ContinueWith(
                _ =>
                {
                    try { cts.Dispose(); } catch { /* best effort */ }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        try { cts.Dispose(); } catch { /* best effort */ }
    }

    private void UnsubscribeOutputEvents()
    {
        _outputs.Outputs.CollectionChanged -= OnSharedOutputsCollectionChanged;
        _outputs.SharedHeadphonesBusesChanged -= OnSharedHeadphonesBusesChanged;
        _outputs.RoutingTopologyChanged -= OnRoutingTopologyChanged;
        _outputs.OutputNamingChanged -= OnOutputNamingChanged;
        _outputs.OutputLineRemoving -= OnOutputLineRemoving;
        _outputs.OutputLineReconfiguringAsync -= OnOutputLineReconfiguringAsync;
        _outputs.OutputLineReconfiguredAsync -= OnOutputLineReconfiguredAsync;
    }

    private void DetachPlaylistTabSelection()
    {
        if (SelectedPlaylistTab is not null)
            SelectedPlaylistTab.Items.CollectionChanged -= OnSelectedTabItemsCollectionChanged;
    }

    private void DetachOutputBindings()
    {
        foreach (var binding in Outputs)
        {
            binding.PropertyChanged -= OnOutputBindingPropertyChanged;
            UnwatchMatrixCells(binding);
        }
    }

    private void OnPlaylistItemsChanged()
    {
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        NextTrackCommand.NotifyCanExecuteChanged();
        PreviousTrackCommand.NotifyCanExecuteChanged();
    }

    private void OnSharedOutputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var addedLines = e.NewItems?.OfType<OutputLineViewModel>().ToArray() ?? [];
        Dispatcher.UIThread.Post(() =>
        {
            SyncOutputsCollection();
            SyncIdleSlate();
            foreach (var addedLine in addedLines)
                _ = TryHotAddAddedCloneAsync(addedLine);
        });
    }

    private bool ShouldHotAddAddedClone(OutputLineViewModel line)
    {
        if (_session is null)
            return false;
        if (line.Definition is not LocalVideoOutputDefinition { CloneOfId: { } parentId })
            return false;
        return Outputs.FirstOrDefault(b => b.Line.Definition.Id == parentId)?.IsSelected == true;
    }

    private async Task TryHotAddAddedCloneAsync(OutputLineViewModel line)
    {
        if (!await Dispatcher.UIThread.InvokeAsync(() => ShouldHotAddAddedClone(line)))
            return;

        for (var i = 0; i < 20; i++)
        {
            var ready = await Dispatcher.UIThread.InvokeAsync(() =>
                line.IsPreviewRunning || !ShouldHotAddAddedClone(line));
            if (ready)
                break;
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (!await Dispatcher.UIThread.InvokeAsync(() => line.IsPreviewRunning && ShouldHotAddAddedClone(line)))
            return;

        await WithPlaybackArcAsync(() =>
        {
            var session = _session;
            if (session is null || !ShouldRouteLine(line) || session.HasWiredLine(line))
                return Task.CompletedTask;

            if (!session.TryAddOutput(line, out var err))
            {
                if (!string.IsNullOrWhiteSpace(err))
                    Dispatcher.UIThread.Post(() => StatusMessage = err);
            }
            else
            {
                TransportTrace.LogInformation("Hot-added clone output '{Name}' to running session", line.Definition.DisplayName);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void OnSharedHeadphonesBusesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(RefreshHeadphonesCueTargets);
    }

    private void OnRoutingTopologyChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SyncOutputsCollection();
            SyncIdleSlate();
        });
    }

    /// <summary>UI rewrite P2: alias changes re-label the matrix rows (the routing itself is
    /// untouched, so no session re-apply is needed).</summary>
    private void OnOutputNamingChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(RebuildAudioMatrixRows);
    }

    public OutputManagementViewModel OutputsRepository => _outputs;

    /// <summary>Per-player checkbox bindings. Audio outputs may be selected on several players; video-capable
    /// outputs are guarded by a rewire prompt so one physical video sink is owned by one player at a time.</summary>
    public ObservableCollection<PlayerOutputBinding> Outputs { get; } = new();

    /// <summary>True when no outputs are registered yet — view shows the "click Play to auto-route" hint.</summary>
    public bool HasNoOutputs => Outputs.Count == 0;

    /// <summary>Short header summary so the routing expander reads e.g. "2 selected of 3" when collapsed.</summary>
    public string RoutingSummary
    {
        get
        {
            var total = Outputs.Count;
            var sel = Outputs.Count(b => b.IsSelected);
            return total == 0 ? "(no outputs)" : $"{sel} selected of {total}";
        }
    }

    /// <summary>Short header text for the hold-image expander.</summary>
    public string HoldImageSummary
    {
        get
        {
            if (!HoldFallbackVideo)
                return string.IsNullOrWhiteSpace(FallbackImagePath) ? "(off)" : "(off — image set)";
            return string.IsNullOrWhiteSpace(FallbackImagePath) ? "(on — no image)" : "(on)";
        }
    }

    /// <summary>Visual label for the Play/Pause toggle.</summary>
    public string PlayPauseLabel => IsPlaying ? "⏸ Pause" : "▶ Play";

    public string DetachedWindowTitle => Resources.Strings.Format(
        nameof(Resources.Strings.DetachedPlayerTitleFormat), Name);

    public event Action<MediaPlayerViewModel>? DetachRequested;

    public string PlaybackStateLabel =>
        IsPlaying ? Resources.Strings.PlaybackStatePlayingIndicator
        : IsMediaLoaded ? Resources.Strings.PlaybackStatePausedIndicator
        : Resources.Strings.PlaybackStateStoppedIndicator;

    public Avalonia.Media.ISolidColorBrush PlaybackStateColor =>
        IsPlaying ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2E7D32"))
        : IsMediaLoaded ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"))
        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#44808080"));

    /// <summary>One-word kind label for the source-state chip ("Video", "Audio", "Idle").</summary>
    public string SourceKindLabel
    {
        get
        {
            if (!IsMediaLoaded || _session is null) return "Idle";
            // Live items don't carry a container decoder; use negotiated live capabilities.
            if (_session.IsLive)
            {
                if (_session.LiveHasVideo && _session.LiveHasAudio) return "Live video + audio";
                if (_session.LiveHasVideo) return "Live video";
                if (_session.LiveHasAudio) return "Live audio";
                return "Live";
            }
            var hasVid = _session.Player.HasContainerDecoder && _session.Player.Decoder.HasVideo;
            var hasAud = _session.Player.HasContainerDecoder && _session.Player.Decoder.HasAudio;
            if (hasVid && hasAud) return "Video + audio";
            if (hasVid) return "Video";
            if (hasAud) return "Audio";
            return "Empty";
        }
    }

    [ObservableProperty]
    private bool _isRoutingExpanded = true;

    public ObservableCollection<PlaylistTabViewModel> PlaylistTabs { get; } = new();

    [ObservableProperty]
    private PlaylistTabViewModel? _selectedPlaylistTab;

    /// <summary>Phase C.5 (§6.8) — discriminated entries (files + live inputs) queued for sequential
    /// playback on the visible playlist tab.</summary>
    public ObservableCollection<PlaylistItem> PlaylistItems => SelectedPlaylistTab?.Items ?? _emptyPlaylistItems;

    public IReadOnlyList<PlayerOutputPreset> OutputPresets { get; } = Enum.GetValues<PlayerOutputPreset>();

    public IReadOnlyList<PlayerTransitionMode> TransitionModes { get; } = Enum.GetValues<PlayerTransitionMode>();

    public IReadOnlyList<HeadphonesCueTapPoint> HeadphonesCueTapPoints { get; } = Enum.GetValues<HeadphonesCueTapPoint>();

    public ObservableCollection<HeadphonesCueTargetOption> HeadphonesCueTargets { get; } = new();

    public bool HasHeadphonesCueOutputs => HeadphonesCueTargets.Count > 0;

    /// <summary>Phase C (§4.3.4) — combobox choices for the per-output channel-mix mode.</summary>
    public IReadOnlyList<AudioRouteMixMode> MixModes { get; } = Enum.GetValues<AudioRouteMixMode>();

    /// <summary>Phase C (§4.3.4) — TreeDataGrid rows. One row per (selected device × output channel),
    /// rebuilt whenever the selection set or the sized input channel count changes. Bound by the view's
    /// code-behind, which also installs dynamic input-channel columns.</summary>
    public ObservableCollection<AudioMatrixRow> AudioMatrixRows { get; } = new();

    public ObservableCollection<AudioMatrixOutputSummary> AudioMatrixOutputSummaries { get; } = new();

    public bool HasAudioMatrixOutputs => AudioMatrixOutputSummaries.Count > 0;

    /// <summary>
    /// One row per active matrix connection (audible cell). Backing source for the route list TreeDataGrid.
    /// Uses the same cell objects as <see cref="AudioMatrixRows"/>, so edits are fully synchronized.
    /// </summary>
    public ObservableCollection<AudioMatrixRouteRow> AudioMatrixRouteRows { get; } = new();

    /// <summary>
    /// Per-input-channel trims (column attenuation). Applied on top of every matrix cell from that input.
    /// </summary>
    public ObservableCollection<AudioMatrixInputTrimViewModel> AudioMatrixInputTrims { get; } = new();

    // ----- UI rewrite P5b: channel-count → preset auto-rules -----------------------------------

    /// <summary>Per-player auto-preset rules (one per source channel count). When media whose audio
    /// channel count matches a rule loads, the rule's preset is applied to every selected output's
    /// matrix — this is how an occasional 5.1 file folds down properly without manual cell edits.</summary>
    public ObservableCollection<ChannelPresetRule> ChannelPresetRules { get; } = new();

    public IReadOnlyList<AudioDownmixPreset> DownmixPresetChoices { get; } = AudioDownmixPresets.All;

    [ObservableProperty]
    private int _newRuleChannels = 6;

    [ObservableProperty]
    private AudioDownmixPreset _newRulePreset = AudioDownmixPreset.Surround51ToStereo;

    /// <summary>Adds (or replaces, keyed by channel count) an auto-preset rule.</summary>
    [RelayCommand]
    private void AddChannelPresetRule()
    {
        var channels = Math.Clamp(NewRuleChannels, 1, 64);
        for (var i = ChannelPresetRules.Count - 1; i >= 0; i--)
        {
            if (ChannelPresetRules[i].SourceChannels == channels)
                ChannelPresetRules.RemoveAt(i);
        }

        ChannelPresetRules.Add(new ChannelPresetRule { SourceChannels = channels, Preset = NewRulePreset });
        // An applicable rule takes effect immediately when it matches the current source.
        ApplyChannelPresetRuleIfMatching(MatrixInputChannelCountFor(_session));
    }

    /// <summary>P5c — save this output's matrix as a shareable framework preset file (.mfmix).</summary>
    [RelayCommand]
    private async Task SaveMatrixPresetAsync(PlayerOutputBinding? binding)
    {
        if (binding is null) return;
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerSaveOptions
        {
            Title = Strings.MatrixPresetSaveTitle,
            DefaultExtension = S.Media.Core.Audio.AudioMixPreset.FileExtension,
            SuggestedFileName = $"{binding.Line.EffectiveName}.{S.Media.Core.Audio.AudioMixPreset.FileExtension}",
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.MatrixPresetFileTypeLabel)
                    { Patterns = ["*." + S.Media.Core.Audio.AudioMixPreset.FileExtension] },
            ],
        };
        var picked = await top.StorageProvider.SaveFilePickerAsync(opts);
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            S.Media.Core.Audio.AudioMixPreset
                .FromMatrix(Path.GetFileNameWithoutExtension(path), binding.Matrix.ToLinearMatrix())
                .Save(path);
            StatusMessage = Strings.Format(nameof(Strings.MatrixPresetSavedFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>P5c — load a framework preset file into this output's matrix.</summary>
    [RelayCommand]
    private async Task LoadMatrixPresetAsync(PlayerOutputBinding? binding)
    {
        if (binding is null) return;
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.MatrixPresetLoadTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.MatrixPresetFileTypeLabel)
                    { Patterns = ["*." + S.Media.Core.Audio.AudioMixPreset.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var preset = S.Media.Core.Audio.AudioMixPreset.Load(path);
            binding.Matrix.ApplyLinearMatrix(preset.ToMatrix());
            StatusMessage = Strings.Format(nameof(Strings.MatrixPresetLoadedFormat), preset.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveChannelPresetRule(ChannelPresetRule? rule)
    {
        if (rule is not null)
            ChannelPresetRules.Remove(rule);
    }

    /// <summary>Applies the matching rule's preset to every selected output matrix sized for
    /// <paramref name="inputChannels"/>. No-op without a matching, applicable rule.</summary>
    private void ApplyChannelPresetRuleIfMatching(int inputChannels)
    {
        var rule = ChannelPresetRules.FirstOrDefault(r => r.SourceChannels == inputChannels);
        if (rule is null)
            return;
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount != inputChannels) continue;
            binding.Matrix.ApplyDownmix(rule.Preset);
        }
    }

    private bool _audioMatrixSourceChannelsExplicit;
    private bool _updatingAudioMatrixSourceChannels;

    /// <summary>
    /// User-configurable source channel count for pre-sizing the matrix before a file is open.
    /// When no explicit value was loaded/edited, the active session's real channel count still wins.
    /// </summary>
    [ObservableProperty]
    private int _audioMatrixSourceChannels = 2;

    /// <summary>Phase C (§4.3.4) — current source channel count for the TreeDataGrid's input columns.
    /// 0 until a matrix has been sized. Watched by the view's code-behind to rebuild input columns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrix))]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrixRoutes))]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrixInputTrims))]
    private int _audioMatrixInputChannelCount;

    /// <summary>True once the matrix has been sized and at least one routed output exists — gates the
    /// TreeDataGrid's visibility and the empty-state hint.</summary>
    public bool HasAudioMatrix => AudioMatrixInputChannelCount > 0 && AudioMatrixRows.Count > 0;

    /// <summary>True once there is at least one active (audible) route cell.</summary>
    public bool HasAudioMatrixRoutes => AudioMatrixRouteRows.Count > 0;

    public bool HasAudioMatrixInputTrims => AudioMatrixInputTrims.Count > 0;

    /// <summary>Phase C (§4.3.4) — change-stamp the view can hook to rebuild columns/rows after the matrix
    /// rows or channel counts change. Fires once per coalesced rebuild.</summary>
    public event EventHandler? AudioMatrixLayoutChanged;

    [ObservableProperty]
    private string _name = "Player";

    [ObservableProperty]
    private string? _mediaFilePath;

    [ObservableProperty]
    private string? _fallbackImagePath;

    /// <summary>Phase C.5 — selected item in the visible playlist tab (file OR live input). Replaces
    /// the v1-era string-path selection; the view binds <c>SelectedItem</c> of the playlist ListBox to
    /// this property.</summary>
    [ObservableProperty]
    private PlaylistItem? _selectedPlaylistItem;

    /// <summary>Display label for the "current source" row above the seek bar. Shows the absolute file
    /// path for file items (the v1 behavior) and the live-source name for live items. Falls back to
    /// <see cref="MediaFilePath"/> when nothing is loaded yet.</summary>
    public string? CurrentMediaDisplay =>
        _currentPlaylistItem switch
        {
            FilePlaylistItem f => f.Path,
            { } live => live.ToolTip,
            null => MediaFilePath,
        };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOptions))]
    [NotifyPropertyChangedFor(nameof(ActiveOptionCount))]
    private bool _isLooping;

    /// <summary>When true, the loop timer auto-loads the next playlist entry on natural end of file.
    /// Defaults to false — auto-advance is rarely wanted in performance contexts where each track is cued.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOptions))]
    [NotifyPropertyChangedFor(nameof(ActiveOptionCount))]
    private bool _autoAdvancePlaylist;

    /// <summary>When true (and auto-advancing), the next item is drawn from a shuffle bag instead of
    /// sequential order. Mirrors the selected tab's <see cref="PlaylistTabViewModel.Shuffle"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOptions))]
    [NotifyPropertyChangedFor(nameof(ActiveOptionCount))]
    private bool _shufflePlaylist;

    /// <summary>When true, auto-advance wraps from the last item back to the first instead of stopping.
    /// Distinct from <see cref="IsLooping"/> (loop the current item). Mirrors the selected tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOptions))]
    [NotifyPropertyChangedFor(nameof(ActiveOptionCount))]
    private bool _repeatAllPlaylist;

    /// <summary>True when any of the secondary playback options (loop / auto-advance / shuffle /
    /// repeat-all) is enabled. Drives the active badge on the transport's "Options" flyout button.</summary>
    public bool HasActiveOptions =>
        IsLooping || AutoAdvancePlaylist || ShufflePlaylist || RepeatAllPlaylist;

    /// <summary>Count of enabled secondary playback options, shown as a small badge on the Options button.</summary>
    public int ActiveOptionCount =>
        (IsLooping ? 1 : 0) + (AutoAdvancePlaylist ? 1 : 0)
        + (ShufflePlaylist ? 1 : 0) + (RepeatAllPlaylist ? 1 : 0);

    [ObservableProperty]
    private bool _holdFallbackVideo;

    [ObservableProperty]
    private double _masterVolumeDb;

    [ObservableProperty]
    private bool _masterMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomOutputPreset))]
    private PlayerOutputPreset _outputPreset = PlayerOutputPreset.AsSource;

    public bool IsCustomOutputPreset => OutputPreset == PlayerOutputPreset.Custom;

    partial void OnOutputPresetChanged(PlayerOutputPreset value)
    {
        _ = value;
        InvalidateCuePreRoll();
    }

    partial void OnTransitionModeChanged(PlayerTransitionMode value)
    {
        _ = value;
        InvalidateCuePreRoll();
    }

    partial void OnTransitionDurationMsChanged(int value)
    {
        _ = value;
        InvalidateCuePreRoll();
    }

    [ObservableProperty]
    private PlayerTransitionMode _transitionMode = PlayerTransitionMode.Cut;

    [ObservableProperty]
    private int _transitionDurationMs = 500;

    [ObservableProperty]
    private int _customOutputWidth = 1920;

    partial void OnCustomOutputWidthChanged(int value)
    {
        _ = value;
        InvalidateCuePreRoll();
    }

    [ObservableProperty]
    private int _customOutputHeight = 1080;

    partial void OnCustomOutputHeightChanged(int value)
    {
        _ = value;
        InvalidateCuePreRoll();
    }

    [ObservableProperty]
    private bool _headphonesCueEnabled;

    [ObservableProperty]
    private HeadphonesCueTargetOption? _selectedHeadphonesCueTarget;

    [ObservableProperty]
    private HeadphonesCueTapPoint _headphonesCueTapPoint = HeadphonesCueTapPoint.PreFader;

    [ObservableProperty]
    private double _headphonesCueGainDb;

    [ObservableProperty]
    private bool _isMediaLoaded;

    /// <summary>Phase C.5 (§6.9) — true while a live item is offline / disconnected and the retry loop
    /// is waiting for the source to come back. The transport bar shows the waiting banner via
    /// <see cref="WaitingForSourceMessage"/> and the loop timer drives reconnect attempts on the item's
    /// <see cref="NDIInputPlaylistItem.RetrySeconds"/> cadence.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    private bool _isWaitingForSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    private string? _waitingForSourceMessage;

    /// <summary>True whenever the header should show the slim indeterminate bar — a media open is in
    /// flight, the waveform is still being analysed, or a live source is waiting to (re)connect.</summary>
    public bool IsBusy => IsLoadingMedia || IsExtractingWaveform || IsWaitingForSource;

    /// <summary>Status text shown beside the busy bar, picked by priority (waiting &gt; loading &gt;
    /// analysing). Empty when <see cref="IsBusy"/> is false.</summary>
    public string BusyStatusText =>
        IsWaitingForSource
            ? (string.IsNullOrWhiteSpace(WaitingForSourceMessage)
                ? Resources.Strings.LoadingMediaLabel
                : WaitingForSourceMessage!)
        : IsLoadingMedia ? Resources.Strings.LoadingMediaLabel
        : IsExtractingWaveform ? Resources.Strings.ExtractingWaveformLabel
        : string.Empty;

    /// <summary>Phase C.5 — wall-clock deadline for the next reconnect attempt. The loop timer reopens
    /// the session via <see cref="OpenOrReloadAsync"/> as soon as <see cref="DateTime.UtcNow"/> reaches
    /// this value. Cleared by <see cref="ExitWaitingForSource"/>.</summary>
    private DateTime _nextRetryAt;

    /// <summary>The item currently in waiting state. Held separately from <see cref="_currentPlaylistItem"/>
    /// because the latter is set to <see langword="null"/> on close, but we still want to keep retrying.</summary>
    private PlaylistItem? _waitingItem;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private TimeSpan _currentPosition;

    [ObservableProperty]
    private double _seekSliderValue;

    /// <summary>True while the user is actively dragging the seek slider (or navigating it with the
    /// keyboard). The view sets it on pointer/key down and clears it once the seek is committed on
    /// release. While set — and until the resulting seek arc finishes (<see cref="_seekArcRunning"/>) —
    /// the playback clock must not write <see cref="SeekSliderValue"/>, otherwise the thumb snaps back
    /// from under the user and the committed target can be a stale clock value (the "jumps back / seeks
    /// somewhere random" symptom).</summary>
    [ObservableProperty]
    private bool _isScrubbing;

    [ObservableProperty]
    private string? _statusMessage;

    private static readonly TimeSpan StatusMessageAutoClearDelay = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _statusMessageClearCts;

    /// <summary>Structured load lifecycle for the player, distinct from the transient
    /// <see cref="StatusMessage"/>. Raises <see cref="HasLoadError"/> when it changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(IsLoadingMedia))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    [NotifyPropertyChangedFor(nameof(DeckStatusText))]
    [NotifyPropertyChangedFor(nameof(DeckStatusSeverity))]
    private PlayerLoadState _loadState = PlayerLoadState.Idle;

    /// <summary>True while a media open is in flight — drives the slim indeterminate loading bar in the
    /// transport header. Distinct from <see cref="IsWaitingForSource"/> (live source not yet connected).</summary>
    public bool IsLoadingMedia => LoadState == PlayerLoadState.Loading;

    /// <summary>Sticky last failure reason (names the failing file), kept visible until the next load
    /// attempt succeeds. Unlike <see cref="StatusMessage"/> it isn't cleared by unrelated status text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(DeckStatusText))]
    [NotifyPropertyChangedFor(nameof(DeckStatusSeverity))]
    private string? _lastLoadError;

    /// <summary>True when the last load attempt failed and an error is available to show.</summary>
    public bool HasLoadError => LoadState == PlayerLoadState.Failed && !string.IsNullOrWhiteSpace(LastLoadError);

    // ----- UI rewrite P5 (plan §1/§2.2): fixed-height deck status line ---------------------------
    // One always-present row replaces the two dock-top banners that used to push the transport
    // down mid-click. Sticky load errors win over transient status text.

    public string? DeckStatusText => HasLoadError ? LastLoadError : StatusMessage;

    public ToastSeverity DeckStatusSeverity => HasLoadError ? ToastSeverity.Error : ToastSeverity.Info;

    private void NotifyDeckStatusChanged()
    {
        OnPropertyChanged(nameof(DeckStatusText));
        OnPropertyChanged(nameof(DeckStatusSeverity));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        NotifyDeckStatusChanged();
        _statusMessageClearCts?.Cancel();
        _statusMessageClearCts?.Dispose();
        _statusMessageClearCts = null;

        if (string.IsNullOrWhiteSpace(value))
            return;

        var cts = new CancellationTokenSource();
        _statusMessageClearCts = cts;
        _ = ClearStatusMessageLaterAsync(value, cts.Token);
    }

    private async Task ClearStatusMessageLaterAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusMessageAutoClearDelay, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && string.Equals(StatusMessage, message, StringComparison.Ordinal))
                    StatusMessage = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    public TimeSpan RemainingTime =>
        Duration > CurrentPosition ? Duration - CurrentPosition : TimeSpan.Zero;

    /// <summary>Operator aid: true when a finite track is playing and within
    /// <see cref="LowTimeWarningThreshold"/> of its end. Drives the low-time clock highlight; false
    /// for live sources, idle, and paused playback.</summary>
    public bool IsNearEndOfTrack =>
        IsPlaying
        && _session is { IsLive: false }
        && Duration > TimeSpan.Zero
        && RemainingTime <= LowTimeWarningThreshold;

    private static readonly TimeSpan LowTimeWarningThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Phase C.5 (§6.5) — true when the loaded source has a finite, seekable duration. Files
    /// with non-zero duration are seekable; live items (PortAudio capture, NDI receiver) are not. The
    /// view hides the seek slider + three-clock readout when this is false.</summary>
    public bool IsTransportSeekable => Duration > TimeSpan.Zero && _session?.IsLive != true;

    /// <summary>Pre-formatted text bound by the view — Avalonia's <c>StringFormat=-{}{0:...}</c> with a leading minus
    /// is fragile (the binding silently fails). Formatting in the VM avoids the trap.</summary>
    public string CurrentPositionText => FormatClock(CurrentPosition);
    public string RemainingTimeText => "-" + FormatClock(RemainingTime);
    public string DurationText => FormatClock(Duration);

    private bool _showElapsedTime;

    public string MiddleTimeText => _showElapsedTime
        ? FormatClock(CurrentPosition)
        : "-" + FormatClock(RemainingTime);

    public string MiddleTimeLabel => _showElapsedTime
        ? Resources.Strings.ElapsedTimeLabel
        : Resources.Strings.RemainingTimeLabel;

    public void ToggleMiddleTimeDisplay()
    {
        _showElapsedTime = !_showElapsedTime;
        OnPropertyChanged(nameof(MiddleTimeText));
        OnPropertyChanged(nameof(MiddleTimeLabel));
    }

    public void ResetVolume() => MasterVolumeDb = 0;

    private void PreOpenAdjacentPlaylistItems()
    {
        _preOpenCts?.Cancel();
        _preOpenCts?.Dispose();
        _preOpenCts = new CancellationTokenSource();

        // Follow the tab that's actually playing (which may not be the selected tab) so auto-advance
        // finds the next decoder already warm.
        var items = ActivePlaybackItems();
        var current = _currentPlaylistItem;
        if (current is null || items.Count == 0) return;

        var idx = items.IndexOf(current);
        if (idx < 0) return;

        // Warm (the cache caps at MaxEntries): the item auto-advance will *actually* pick next —
        // shuffle-aware, so shuffle playback no longer advances into a cold decoder — plus the linear
        // neighbours manual Next/Previous use. Deduped so a non-shuffled next isn't opened twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<(string Path, int? AudioTrackIndex)>(3);
        void Add(PlaylistItem? item)
        {
            if (item is FilePlaylistItem f && seen.Add(f.Path))
                targets.Add((f.Path, f.AudioTrackIndex));
        }

        Add(PeekAutoAdvanceNext(items));                  // unattended auto-advance target
        if (idx + 1 < items.Count) Add(items[idx + 1]);  // manual Next (linear)
        if (idx - 1 >= 0) Add(items[idx - 1]);            // manual Previous (linear)

        if (targets.Count > 0)
            _decoderCache.PreOpenAsync(targets, _preOpenCts.Token);
    }

    private float[]? _waveformPeaks;
    private int _waveformRevision;
    private CancellationTokenSource? _waveformCts;
    private Task? _waveformTask;

    /// <summary>True while the background waveform peaks are being computed for the loaded file — drives
    /// the slim indeterminate bar's "Analysing waveform…" state once the media itself has opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    private bool _isExtractingWaveform;

    public float[]? WaveformPeaks
    {
        get => _waveformPeaks;
        private set { _waveformPeaks = value; OnPropertyChanged(); }
    }

    public int WaveformRevision
    {
        get => _waveformRevision;
        private set { _waveformRevision = value; OnPropertyChanged(); }
    }

    private void StartWaveformExtraction(string? path)
    {
        CancelWaveformExtraction();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ResetWaveformDisplay();
            return;
        }

        IsExtractingWaveform = true;
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        _waveformTask = RunWaveformExtractionAsync(path, cts);
    }

    private async Task RunWaveformExtractionAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, cts.Token).ConfigureAwait(false);
            // A superseding extraction (or a path clear) owns the flag once this token is cancelled, so
            // only the run that finishes naturally clears the "analysing" state.
            if (!cts.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_waveformCts, cts))
                        return;
                    _waveformCts = null;
                    _waveformTask = null;
                    WaveformPeaks = peaks;
                    WaveformRevision++;
                    IsExtractingWaveform = false;
                    try { cts.Dispose(); } catch { /* best effort */ }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when switching files or closing the player.
        }
        catch (Exception ex)
        {
            TransportTrace.LogWarning(ex, "Waveform extraction failed for {Path}", path);
            if (!cts.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_waveformCts, cts))
                        return;
                    _waveformCts = null;
                    _waveformTask = null;
                    ResetWaveformDisplay();
                    try { cts.Dispose(); } catch { /* best effort */ }
                });
            }
        }
    }

    private void ResetWaveformDisplay()
    {
        WaveformPeaks = null;
        WaveformRevision++;
        IsExtractingWaveform = false;
    }

    private double _peakLevelDb = double.NegativeInfinity;

    public double PeakLevelDb
    {
        get => _peakLevelDb;
        private set
        {
            if (Math.Abs(_peakLevelDb - value) > 0.5 || double.IsNegativeInfinity(value) != double.IsNegativeInfinity(_peakLevelDb))
            {
                _peakLevelDb = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PeakLevelNormalized));
            }
        }
    }

    public double PeakLevelNormalized =>
        double.IsNegativeInfinity(PeakLevelDb) ? 0
        : Math.Clamp((PeakLevelDb + 60) / 72.0, 0, 1);

    private void PollAudioMeters()
    {
        var session = _session;
        if (session is null) { PeakLevelDb = double.NegativeInfinity; return; }

        var maxDb = double.NegativeInfinity;
        foreach (var meter in session.AudioMeters)
        {
            var db = meter.ReadAndResetPeakDb();
            if (db > maxDb) maxDb = db;
        }
        PeakLevelDb = maxDb;
    }

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

    public bool CanRemove => _requestRemove is not null;

    partial void OnIsMediaLoadedChanged(bool value)
    {
        NotifyTransportCanExecuteChanged();
        OnPropertyChanged(nameof(SourceKindLabel));
        OnPropertyChanged(nameof(IsTransportSeekable));
        OnPropertyChanged(nameof(PlaybackStateLabel));
        OnPropertyChanged(nameof(PlaybackStateColor));
        SyncPlaybackSleepInhibitor();
        if (value)
            StopIdleSlate();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PlaybackStateLabel));
        OnPropertyChanged(nameof(PlaybackStateColor));
        OnPropertyChanged(nameof(IsNearEndOfTrack));
        NotifyTransportCanExecuteChanged();
        SyncPlaybackSleepInhibitor();
        if (value)
            PreOpenAdjacentPlaylistItems();
    }

    private void SyncPlaybackSleepInhibitor()
    {
        var shouldInhibit = IsPlaying && IsMediaLoaded && _session is not null;
        if (shouldInhibit)
        {
            _sleepInhibitLease ??= PlaybackSleepInhibitor.Default.Acquire($"Media player '{Name}' is playing");
            return;
        }

        ReleasePlaybackSleepInhibitor();
    }

    private void ReleasePlaybackSleepInhibitor()
    {
        var lease = Interlocked.Exchange(ref _sleepInhibitLease, null);
        try { lease?.Dispose(); }
        catch { /* best effort */ }
    }

    partial void OnMediaFilePathChanged(string? value)
    {
        if (_session is not null || _isTransportBusy)
        {
            CancelWaveformExtraction();
            ResetWaveformDisplay();
            return;
        }

        StartWaveformExtraction(value);
    }

    partial void OnHoldFallbackVideoChanging(bool value) => _ = value;

    partial void OnFallbackImagePathChanging(string? value) => _ = value;

    /// <summary>Coalesce the four transport CanExecute invalidations behind a single helper so the
    /// position-changed handler doesn't fire four binding updates per tick.</summary>
    private void NotifyTransportCanExecuteChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SeekToSliderCommand.NotifyCanExecuteChanged();
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        NextTrackCommand.NotifyCanExecuteChanged();
        PreviousTrackCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsWaitingForSourceChanged(bool value)
    {
        _ = value;
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        _ = value;
        SeekToSliderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(IsTransportSeekable));
        OnPropertyChanged(nameof(IsNearEndOfTrack));
    }

    partial void OnIsMediaLoadedChanging(bool value) => _ = value;

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        _ = value;
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(MiddleTimeText));
        OnPropertyChanged(nameof(IsNearEndOfTrack));
    }

    partial void OnSelectedPlaylistItemChanged(PlaylistItem? value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.SelectedItem = value;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        _ = RefreshSelectedItemAudioTrackChoicesAsync(value);
    }

    /// <summary>Audio-track entries for the playlist context menu ("Audio track" submenu). Filled
    /// asynchronously when a multi-track file item is selected; empty otherwise (submenu hidden).</summary>
    public ObservableCollection<PlaylistAudioTrackChoiceViewModel> SelectedItemAudioTrackChoices { get; } = new();

    [ObservableProperty]
    private bool _selectedItemHasMultipleAudioTracks;

    private async Task RefreshSelectedItemAudioTrackChoicesAsync(PlaylistItem? value)
    {
        if (value is not FilePlaylistItem file)
        {
            SelectedItemAudioTrackChoices.Clear();
            SelectedItemHasMultipleAudioTracks = false;
            return;
        }

        var tracks = await Playback.CueMediaProbe.TryProbeAudioTracksAsync(file.Path).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Selection may have moved on while probing — only publish for the still-selected item.
            if (!ReferenceEquals(SelectedPlaylistItem, value))
                return;

            SelectedItemAudioTrackChoices.Clear();
            if (tracks.Count >= 2)
            {
                SelectedItemAudioTrackChoices.Add(new PlaylistAudioTrackChoiceViewModel(
                    this, null, Strings.AudioTrackAutomaticLabel, file.AudioTrackIndex is null));
                foreach (var track in tracks)
                    SelectedItemAudioTrackChoices.Add(new PlaylistAudioTrackChoiceViewModel(
                        this, track.Index, track.ToDisplayString(), file.AudioTrackIndex == track.Index));
            }

            SelectedItemHasMultipleAudioTracks = SelectedItemAudioTrackChoices.Count > 0;
        });
    }

    /// <summary>Replaces the selected file item with a copy carrying the chosen audio track. Applies on
    /// the next open of the item (reload if it is currently playing).</summary>
    internal void SetSelectedPlaylistItemAudioTrack(int? audioTrackIndex)
    {
        if (SelectedPlaylistItem is not FilePlaylistItem file || file.AudioTrackIndex == audioTrackIndex)
            return;

        var replacement = file with { AudioTrackIndex = audioTrackIndex };
        var idx = PlaylistItems.IndexOf(file);
        if (idx < 0)
            return;

        PlaylistItems[idx] = replacement;
        if (ReferenceEquals(_currentPlaylistItem, file))
            _currentPlaylistItem = replacement;
        SelectedPlaylistItem = replacement;
        // A neighbouring pre-open may hold a decoder keyed on the old track — drop it so the next
        // open uses the new choice.
        _decoderCache.InvalidateAll();
        StatusMessage = Strings.Format(nameof(Strings.AudioTrackChangedStatusFormat), replacement.DisplayName);
    }

    partial void OnSelectedPlaylistTabChanged(PlaylistTabViewModel? oldValue, PlaylistTabViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.Items.CollectionChanged -= OnSelectedTabItemsCollectionChanged;
        if (newValue is not null)
            newValue.Items.CollectionChanged += OnSelectedTabItemsCollectionChanged;

        _syncingPlaylistTabState = true;
        try
        {
            SelectedPlaylistItem = newValue?.SelectedItem is { } si && newValue.Items.Contains(si)
                ? si
                : newValue?.Items.FirstOrDefault();
            IsLooping = newValue?.IsLooping ?? false;
            AutoAdvancePlaylist = newValue?.AutoAdvance ?? false;
            ShufflePlaylist = newValue?.Shuffle ?? false;
            RepeatAllPlaylist = newValue?.RepeatAll ?? false;
        }
        finally
        {
            _syncingPlaylistTabState = false;
        }

        // The shuffle bag is per playing-tab; switching tabs invalidates it.
        InvalidateShuffleBag();

        OnPropertyChanged(nameof(PlaylistItems));
        OnPlaylistItemsChanged();
    }

    private void OnSelectedTabItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPlaylistItemsChanged();

    partial void OnIsLoopingChanged(bool value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.IsLooping = value;
    }

    partial void OnAutoAdvancePlaylistChanged(bool value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.AutoAdvance = value;
    }

    partial void OnShufflePlaylistChanged(bool value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.Shuffle = value;
        // A toggle change invalidates the current shuffle bag (different traversal).
        InvalidateShuffleBag();
    }

    partial void OnRepeatAllPlaylistChanged(bool value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.RepeatAll = value;
    }

    partial void OnMasterVolumeDbChanged(double value)
    {
        _ = value;
        ApplyAllOutputGainsToSession();
        RebuildAudioMatrixRouteRows();
    }

    partial void OnMasterMutedChanged(bool value)
    {
        _ = value;
        ApplyAllOutputGainsToSession();
        RebuildAudioMatrixRouteRows();
    }

    partial void OnAudioMatrixSourceChannelsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 64);
        if (value != clamped)
        {
            SetAudioMatrixSourceChannels(clamped, explicitValue: true, resize: true);
            return;
        }

        if (!_updatingAudioMatrixSourceChannels)
            _audioMatrixSourceChannelsExplicit = true;

        ResizeSelectedAudioMatrices(_session);
        RebuildAudioMatrixRows();
        ApplyAllOutputMatricesToSession();
    }

    /// <summary>Mirror the shared outputs list into per-player bindings, preserving selection on the survivors.
    /// Clones (§3.4) are deliberately excluded — their routing is mirrored from the parent's checkbox by
    /// <see cref="SelectedOutputLines"/>, so showing them as separate checkboxes would be misleading.</summary>
    private void SyncOutputsCollection()
    {
        var keep = Outputs.ToDictionary(b => b.Line);
        foreach (var b in Outputs)
        {
            b.PropertyChanged -= OnOutputBindingPropertyChanged;
            UnwatchMatrixCells(b);
        }
        Outputs.Clear();
        foreach (var line in _outputs.Outputs)
        {
            if (!line.SupportsMediaPlayerRouting)
                continue; // skip clones — handled via the parent's binding
            if (!keep.TryGetValue(line, out var binding))
                binding = new PlayerOutputBinding(line);
            binding.PropertyChanged += OnOutputBindingPropertyChanged;
            WatchMatrixCells(binding);
            Outputs.Add(binding);
        }
        OnPropertyChanged(nameof(HasNoOutputs));
        OnPropertyChanged(nameof(RoutingSummary));
        RefreshHeadphonesCueTargets();
    }

    private void RefreshHeadphonesCueTargets()
    {
        var selectedKind = SelectedHeadphonesCueTarget?.Kind;
        var selectedId = SelectedHeadphonesCueTarget?.Identity;

        HeadphonesCueTargets.Clear();
        foreach (var line in _outputs.PortAudioOutputLines)
            HeadphonesCueTargets.Add(HeadphonesCueTargetOption.ForDirect(line));
        foreach (var bus in _outputs.SharedHeadphonesBuses)
            HeadphonesCueTargets.Add(HeadphonesCueTargetOption.ForBus(bus, _outputs.ResolveSharedBusOutput(bus.Id)));

        SelectedHeadphonesCueTarget =
            HeadphonesCueTargets.FirstOrDefault(t => t.Kind == selectedKind && t.Identity == selectedId)
            ?? HeadphonesCueTargets.FirstOrDefault();
        OnPropertyChanged(nameof(HasHeadphonesCueOutputs));
    }

    private void SelectHeadphonesCueTarget(Guid? directOutputId, Guid? sharedBusId)
    {
        if (sharedBusId is { } busId)
        {
            SelectedHeadphonesCueTarget = HeadphonesCueTargets.FirstOrDefault(
                t => t.Kind == HeadphonesCueTargetOption.TargetKind.SharedBus && t.Identity == busId);
            return;
        }

        if (directOutputId is { } outputId)
        {
            SelectedHeadphonesCueTarget = HeadphonesCueTargets.FirstOrDefault(
                t => t.Kind == HeadphonesCueTargetOption.TargetKind.Direct && t.Identity == outputId);
            return;
        }

        SelectedHeadphonesCueTarget = HeadphonesCueTargets.FirstOrDefault();
    }

    private int SanitizedCustomOutputWidth() => Math.Clamp(CustomOutputWidth, 16, 7680);

    private int SanitizedCustomOutputHeight() => Math.Clamp(CustomOutputHeight, 16, 4320);

    /// <summary>Phase C (§4.3.4) — subscribe to per-cell PropertyChanged so any matrix edit pushes the new
    /// route layout into the session. New cells added by <see cref="AudioMatrixViewModel.Resize"/> get
    /// re-subscribed via the CollectionChanged hook.</summary>
    private void WatchMatrixCells(PlayerOutputBinding binding)
    {
        binding.Matrix.Cells.CollectionChanged += OnBindingMatrixCellsCollectionChanged;
        foreach (var c in binding.Matrix.Cells)
            c.PropertyChanged += OnBindingMatrixCellChanged;
    }

    private void UnwatchMatrixCells(PlayerOutputBinding binding)
    {
        binding.Matrix.Cells.CollectionChanged -= OnBindingMatrixCellsCollectionChanged;
        foreach (var c in binding.Matrix.Cells)
            c.PropertyChanged -= OnBindingMatrixCellChanged;
    }

    private void OnBindingMatrixCellsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (AudioMatrixCellViewModel c in e.OldItems)
                c.PropertyChanged -= OnBindingMatrixCellChanged;
        if (e.NewItems is not null)
            foreach (AudioMatrixCellViewModel c in e.NewItems)
                c.PropertyChanged += OnBindingMatrixCellChanged;
    }

    private void OnBindingMatrixCellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AudioMatrixCellViewModel.GainDb) or nameof(AudioMatrixCellViewModel.Muted)))
            return;
        if (sender is not AudioMatrixCellViewModel cell)
            return;
        var binding = Outputs.FirstOrDefault(b => b.Matrix.Cells.Contains(cell));
        if (binding is null) return;
        ApplyOutputMatrixToSession(binding);
        RebuildAudioMatrixRouteRows();
    }

    private void WatchInputTrimRows()
    {
        foreach (var trim in AudioMatrixInputTrims)
            trim.PropertyChanged += OnInputTrimChanged;
    }

    private void UnwatchInputTrimRows()
    {
        foreach (var trim in AudioMatrixInputTrims)
            trim.PropertyChanged -= OnInputTrimChanged;
    }

    private void OnInputTrimChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AudioMatrixInputTrimViewModel.GainDb) or nameof(AudioMatrixInputTrimViewModel.Muted)))
            return;
        ApplyAllOutputMatricesToSession();
        RebuildAudioMatrixRouteRows();
    }

    private bool TryGetVideoOutputRouteConflict(
        PlayerOutputBinding binding,
        [NotNullWhen(true)] out VideoOutputRouteConflict? conflict)
    {
        conflict = null;
        if (!IsVideoOutputLine(binding.Line))
            return false;

        var players = _outputs.ActivePlayersProbe?.Invoke();
        if (players is null || players.Count == 0)
            return false;

        var existing = players
            .Where(p => !ReferenceEquals(p, this) && p.IsVideoOutputSelected(binding.Line.Definition.Id))
            .ToList();
        if (existing.Count == 0)
            return false;

        conflict = new VideoOutputRouteConflict(this, binding.Line, existing);
        return true;
    }

    private bool WouldConflictWithAnotherPlayer(PlayerOutputBinding binding) =>
        TryGetVideoOutputRouteConflict(binding, out _);

    private bool IsVideoOutputSelected(Guid outputLineId) =>
        Outputs.Any(b => b.Line.Definition.Id == outputLineId && b.IsSelected && IsVideoOutputLine(b.Line));

    internal void DeselectVideoOutputForRewire(Guid outputLineId)
    {
        var binding = Outputs.FirstOrDefault(b => b.Line.Definition.Id == outputLineId);
        if (binding is { IsSelected: true } && IsVideoOutputLine(binding.Line))
            binding.IsSelected = false;
    }

    private static bool IsVideoOutputLine(OutputLineViewModel line) =>
        line.Definition switch
        {
            LocalVideoOutputDefinition => true,
            NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.AudioOnly } => true,
            _ => false,
        };

    private void SetBindingSelectedWithoutVideoConflictPrompt(PlayerOutputBinding binding, bool selected)
    {
        _suppressVideoRouteConflictPrompt++;
        try
        {
            binding.IsSelected = selected;
        }
        finally
        {
            _suppressVideoRouteConflictPrompt--;
        }
    }

    private void SuppressVideoRouteConflictPrompt(Action action)
    {
        _suppressVideoRouteConflictPrompt++;
        try
        {
            action();
        }
        finally
        {
            _suppressVideoRouteConflictPrompt--;
        }
    }

    private async Task PromptAndApplyVideoOutputRewireAsync(
        PlayerOutputBinding binding,
        VideoOutputRouteConflict conflict)
    {
        bool confirmed;
        try
        {
            confirmed = await VideoOutputRouteConflictPrompt(conflict).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = ex.Message);
            return;
        }

        if (!confirmed)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var player in conflict.ExistingPlayers)
                player.DeselectVideoOutputForRewire(conflict.OutputLine.Definition.Id);

            if (Outputs.Contains(binding))
            {
                binding.IsSelected = true;
                StatusMessage = Strings.Format(
                    nameof(Strings.VideoOutputRouteConflictStatusFormat),
                    conflict.OutputLine.Definition.EffectiveName,
                    Name);
            }
        });
    }

    private static async Task<bool> DefaultVideoOutputRouteConflictPromptAsync(VideoOutputRouteConflict conflict)
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return false;

        var existingNames = string.Join(", ", conflict.ExistingPlayers.Select(p => p.Name));
        var dlg = new Window
        {
            Title = Strings.VideoOutputRouteConflictTitle,
            Width = 520,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var rewire = new Button { Content = Strings.VideoOutputRouteConflictRewireButton, IsDefault = true };
        var cancel = new Button { Content = Strings.CancelButton, IsCancel = true };

        var tcs = new TaskCompletionSource<bool>();
        rewire.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(rewire);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var message = new TextBlock
        {
            Text = Strings.Format(
                nameof(Strings.VideoOutputRouteConflictMessageFormat),
                conflict.OutputLine.Definition.EffectiveName,
                existingNames,
                conflict.TargetPlayer.Name),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        root.Children.Add(buttons);
        root.Children.Add(message);
        dlg.Content = root;

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private void OnOutputBindingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerOutputBinding binding)
            return;

        if (e.PropertyName == nameof(PlayerOutputBinding.IsSelected))
        {
            if (_suppressVideoRouteConflictPrompt == 0
                && binding.IsSelected
                && TryGetVideoOutputRouteConflict(binding, out var conflict))
            {
                SetBindingSelectedWithoutVideoConflictPrompt(binding, false);
                _ = PromptAndApplyVideoOutputRewireAsync(binding, conflict);
                return;
            }

            OnPropertyChanged(nameof(RoutingSummary));
            if (binding.IsSelected)
                binding.Matrix.Resize(MatrixInputChannelCountFor(_session), OutputChannelCountOrZero(binding.Line));
            RebuildAudioMatrixRows();
            // Hot toggle: if a session is running, mirror the checkbox change into the playback graph so
            // routing takes effect without reload. Without this, ticking a new output mid-play did nothing
            // until next Open / Play, and unticking left the route alive until the session was torn down.
            if (_session is not null)
                _ = HotApplyRoutingToggleAsync(binding);
            SyncIdleSlate();
            return;
        }

        if (e.PropertyName is nameof(PlayerOutputBinding.GainDb) or nameof(PlayerOutputBinding.IsMuted))
        {
            ApplyOutputCompoundGainToSession(binding.Line);
            RebuildAudioMatrixRouteRows();
            return;
        }

        if (e.PropertyName == nameof(PlayerOutputBinding.MixMode))
        {
            ApplyOutputMixModeToSession(binding);
            return;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — apply the mix-mode preset by rebuilding the matrix cells; the per-cell push
    /// then re-installs router routes via <see cref="ApplyOutputMatrixToSession"/>. Falls back to the
    /// single-route <c>ChannelMap</c> path on lines whose matrix hasn't been sized yet (no session open).
    /// </summary>
    private void ApplyOutputMixModeToSession(PlayerOutputBinding binding)
    {
        var session = _session;
        if (binding.Matrix.OutputChannelCount > 0 && binding.Matrix.InputChannelCount > 0)
        {
            binding.Matrix.ApplyPreset(binding.MixMode);
            ApplyOutputMatrixToSession(binding);
            return;
        }
        if (session is null) return;
        var gain = EffectiveGain(binding);
        if (!session.TrySetOutputChannelMap(binding.Line, binding.MixMode, gain, out var err) && !string.IsNullOrWhiteSpace(err))
            StatusMessage = err;
    }

    /// <summary>
    /// Phase C (§4.3.4) — push the binding's matrix cells down into the playback session, installing
    /// one router route per audible cell. Re-applies after any cell edit; click-free gain rides for
    /// master/per-output changes go through <see cref="ApplyOutputCompoundGainToSession"/> instead.
    /// </summary>
    private void ApplyOutputMatrixToSession(PlayerOutputBinding binding)
    {
        var session = _session;
        if (session is null) return;
        var compound = CompoundEnvelope(binding);
        if (!session.TrySetOutputMatrix(binding.Line, BuildEffectiveRouteCells(binding), compound, out var err) &&
            !string.IsNullOrWhiteSpace(err))
            StatusMessage = err;
    }

    private IReadOnlyList<AudioMatrixCellConfig> BuildEffectiveRouteCells(PlayerOutputBinding binding)
    {
        var routed = new List<AudioMatrixCellConfig>();
        foreach (var c in binding.Matrix.Cells)
        {
            var (trimDb, trimMuted) = InputTrimValues(c.InputChannel);
            if (c.Muted || trimMuted)
                continue;

            routed.Add(new AudioMatrixCellConfig
            {
                InputChannel = c.InputChannel,
                OutputChannel = c.OutputChannel,
                GainDb = c.GainDb + trimDb,
                Muted = false,
            });
        }
        return routed;
    }

    /// <summary>Linear master × per-output gain (envelope) applied on top of every cell's own gain.</summary>
    private float CompoundEnvelope(PlayerOutputBinding binding)
    {
        if (MasterMuted || binding.IsMuted) return 0f;
        var db = Math.Clamp(MasterVolumeDb + binding.GainDb, -80.0, 24.0);
        return (float)Math.Pow(10.0, db / 20.0) * _cueEnvelope;
    }

    /// <summary>
    /// Phase C (§4.3.4) — click-free gain ride across the line's cell routes. Falls back to the legacy
    /// single-route gain path when the line has no cell routes installed.
    /// </summary>
    private void ApplyOutputCompoundGainToSession(OutputLineViewModel line)
    {
        var session = _session;
        if (session is null) return;
        var binding = Outputs.FirstOrDefault(b => b.Line == line);
        if (binding is null) return;

        var compound = CompoundEnvelope(binding);
        // Matrix path: ride per-cell. Returns false when no cells installed → fall through to legacy path.
        if (session.TrySetOutputMatrixCompoundGain(line, compound, out var err))
            return;
        if (!string.IsNullOrWhiteSpace(err))
            StatusMessage = err;
        ApplyOutputGainToSession(line);
    }

    private void ApplyAllOutputGainsToSession()
    {
        foreach (var binding in Outputs)
            ApplyOutputCompoundGainToSession(binding.Line);
    }

    /// <summary>
    /// Phase C (§4.3.4) — re-stamp every wired route's <c>ChannelMap</c> from the binding's mix mode.
    /// Used at session-open (after WireAudio's default identity map is in place) so a player whose
    /// outputs were saved with non-default mix modes comes back identically on next open.
    /// </summary>
    private void ApplyAllOutputMixModesToSession()
    {
        var session = _session;
        if (session is null) return;
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            var gain = EffectiveGain(binding);
            if (!session.TrySetOutputChannelMap(binding.Line, binding.MixMode, gain, out var err) &&
                !string.IsNullOrWhiteSpace(err))
                StatusMessage = err;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — push the full per-cell matrix into the session for every selected output.
    /// Replaces the legacy <see cref="ApplyAllOutputMixModesToSession"/> path at the per-cell layer.
    /// </summary>
    private void ApplyAllOutputMatricesToSession()
    {
        var session = _session;
        if (session is null) return;
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount == 0) continue; // matrix not yet sized
            ApplyOutputMatrixToSession(binding);
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — rebuild <see cref="AudioMatrixRows"/> from the currently-selected bindings.
    /// Each ticked output contributes one row per output channel. The view's code-behind watches this list
    /// + <see cref="AudioMatrixInputChannelCount"/> to add / remove dynamic input-channel columns.
    /// </summary>
    private void RebuildAudioMatrixRows()
    {
        AudioMatrixRows.Clear();
        AudioMatrixOutputSummaries.Clear();
        var inputChannels = 0;
        var summarized = new HashSet<PlayerOutputBinding>();
        foreach (var slot in BuildVirtualOutputMap())
        {
            if (summarized.Add(slot.Binding))
            {
                var channels = slot.Binding.Matrix.OutputChannelCount;
                AudioMatrixOutputSummaries.Add(new AudioMatrixOutputSummary(
                    slot.Binding.Line.KindLabel,
                    slot.Binding.Line.Definition.EffectiveName,
                    channels == 1 ? "1 channel" : $"{channels} channels"));
            }

            inputChannels = Math.Max(inputChannels, slot.Binding.Matrix.InputChannelCount);
            var label = $"{slot.Binding.Line.Definition.EffectiveName} · {OutputChannelSuffix(slot.Binding.Matrix.OutputChannelCount, slot.OutputChannel)}";
            AudioMatrixRows.Add(new AudioMatrixRow(slot.Binding, slot.OutputChannel, slot.VirtualOutputChannel, label));
        }

        AudioMatrixInputChannelCount = inputChannels;
        RebuildInputTrimRows(inputChannels);
        RebuildAudioMatrixRouteRows();
        OnPropertyChanged(nameof(HasAudioMatrix));
        OnPropertyChanged(nameof(HasAudioMatrixOutputs));
        AudioMatrixLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<VirtualOutputSlot> BuildVirtualOutputMap()
    {
        // UI rewrite P2: rows are simply (line, channel) in alias order — the operator-managed
        // "VOut" numbering is gone; the ordinal is just the 1-based row number.
        var rows = new List<VirtualOutputSlot>();
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount == 0 || binding.Matrix.OutputChannelCount == 0) continue;
            for (var oc = 0; oc < binding.Matrix.OutputChannelCount; oc++)
                rows.Add(new VirtualOutputSlot(0, binding, oc));
        }

        return rows
            .OrderBy(r => r.Binding.Line.Definition.EffectiveName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.OutputChannel)
            .Select((r, i) => r with { VirtualOutputChannel = i + 1 });
    }

    private readonly record struct VirtualOutputSlot(
        int VirtualOutputChannel,
        PlayerOutputBinding Binding,
        int OutputChannel);

    private void RebuildInputTrimRows(int inputChannels)
    {
        var preserved = AudioMatrixInputTrims.ToDictionary(
            t => t.InputChannel,
            t => new InputChannelTrimConfig { InputChannel = t.InputChannel, GainDb = t.GainDb, Muted = t.Muted });

        UnwatchInputTrimRows();
        AudioMatrixInputTrims.Clear();

        for (var ic = 0; ic < inputChannels; ic++)
        {
            if (!preserved.TryGetValue(ic, out var cfg) && !_pendingInputTrimConfigs.TryGetValue(ic, out cfg))
                cfg = new InputChannelTrimConfig { InputChannel = ic, GainDb = 0.0, Muted = false };
            AudioMatrixInputTrims.Add(new AudioMatrixInputTrimViewModel(ic, inputChannels, cfg.GainDb, cfg.Muted));
        }

        WatchInputTrimRows();
        OnPropertyChanged(nameof(HasAudioMatrixInputTrims));
    }

    /// <summary>
    /// Rebuild the active-route list from audible matrix cells. Keeps deterministic VOut numbering aligned
    /// with <see cref="RebuildAudioMatrixRows"/> ordering.
    /// </summary>
    private void RebuildAudioMatrixRouteRows()
    {
        AudioMatrixRouteRows.Clear();
        var inputChannels = Math.Max(1, AudioMatrixInputChannelCount);
        var vout = 0;
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount == 0 || binding.Matrix.OutputChannelCount == 0) continue;

            for (var oc = 0; oc < binding.Matrix.OutputChannelCount; oc++)
            {
                vout++;
                var outLabel = $"{binding.Line.Definition.DisplayName} · {OutputChannelSuffix(binding.Matrix.OutputChannelCount, oc)}";
                var active = binding.Matrix.Cells
                    .Where(c => c.OutputChannel == oc && c.IsAudible)
                    .OrderBy(c => c.InputChannel);
                foreach (var cell in active)
                {
                    AudioMatrixRouteRows.Add(new AudioMatrixRouteRow(
                        virtualOutputChannel: vout,
                        outputLabel: outLabel,
                        inputChannel: cell.InputChannel,
                        inputChannelCount: inputChannels,
                        cell: cell,
                        effectiveGainText: EffectiveCellGainText(binding, cell)));
                }
            }
        }
        OnPropertyChanged(nameof(HasAudioMatrixRoutes));
    }

    private void ApplyOutputGainToSession(OutputLineViewModel line)
    {
        var session = _session;
        if (session is null)
            return;

        var binding = Outputs.FirstOrDefault(b => b.Line == line);
        if (binding is null)
            return;

        var gain = EffectiveGain(binding);
        if (!session.TrySetOutputGain(line, gain, out var err) && !string.IsNullOrWhiteSpace(err))
            StatusMessage = err;
    }

    private float EffectiveGain(PlayerOutputBinding binding)
    {
        if (MasterMuted || binding.IsMuted)
            return 0.0f;
        var db = Math.Clamp(MasterVolumeDb + binding.GainDb, -80.0, 24.0);
        return (float)Math.Pow(10.0, db / 20.0);
    }

    private bool ShouldRouteLine(OutputLineViewModel line)
    {
        if (line.SupportsMediaPlayerRouting)
            return Outputs.FirstOrDefault(b => b.Line == line)?.IsSelected == true;

        if (line.Definition is LocalVideoOutputDefinition { CloneOfId: { } parentId })
            return Outputs.FirstOrDefault(b => b.Line.Definition.Id == parentId)?.IsSelected == true;

        return false;
    }

    private async Task OnOutputLineReconfiguringAsync(OutputLineViewModel line)
    {
        await WithPlaybackArcAsync(() =>
        {
            _session?.TryRemoveOutput(line, out _);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private async Task OnOutputLineReconfiguredAsync(OutputLineViewModel line)
    {
        await WithPlaybackArcAsync(() =>
        {
            if (_session is not null && ShouldRouteLine(line))
            {
                if (_session.TryAddOutput(line, out var err))
                {
                    // Re-stamp the matrix on the freshly-wired route; falls back to the legacy single-route
                    // ChannelMap when the matrix hasn't been sized yet (first hot-add before session play).
                        var binding = Outputs.FirstOrDefault(b => b.Line == line);
                        if (binding is not null)
                        {
                            SyncMatrixSourceChannelsFromSession(_session);
                            binding.Matrix.Resize(MatrixInputChannelCountFor(_session),
                                OutputChannelCountOrZero(binding.Line));
                            ApplyOutputMatrixToSession(binding);
                        }
                    ApplyOutputCompoundGainToSession(line);
                }
                else if (!string.IsNullOrWhiteSpace(err))
                    Dispatcher.UIThread.Post(() => StatusMessage = err);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void ApplyBindingGainFromConfig(IReadOnlyDictionary<string, OutputGainConfig> gains)
    {
        foreach (var binding in Outputs)
        {
            if (!gains.TryGetValue(binding.Line.Definition.DisplayName, out var gain))
                continue;
            binding.GainDb = gain.GainDb;
            binding.IsMuted = gain.Muted;
            binding.MixMode = gain.MixMode;
            // Persisted cells must be usable before media is opened and before the output is selected.
            // Size the binding now so a saved 5.1 matrix can be edited immediately and survives later toggles.
            if (gain.MatrixCells.Count > 0 && binding.Matrix.InputChannelCount == 0)
                binding.Matrix.Resize(MatrixInputChannelCountFor(_session), OutputChannelCountOrZero(binding.Line));
            if (gain.MatrixCells.Count > 0)
                binding.Matrix.ApplyConfig(gain.MatrixCells);
        }
    }

    /// <summary>
    /// Phase B follow-up (§4.3.3) — call the session's hot route APIs from a checkbox toggle.
    /// Includes the line's clones (PlayerRoutingMirror) so a parent tick wires every child as well.
    /// Routes through the playback arc semaphore so a toggle can't race with Stop / Seek / Dispose.
    /// </summary>
    private async Task HotApplyRoutingToggleAsync(PlayerOutputBinding binding)
    {
        var line = binding.Line;
        var add = binding.IsSelected;

        // Mirror the parent's tick onto clones (§3.4) so the playback graph stays consistent with the
        // logical "route to parent" intent. Clones don't have their own checkbox.
        var targets = new List<OutputLineViewModel> { line };
        targets.AddRange(_outputs.GetClonesOf(line.Definition.Id));

        await WithPlaybackArcAsync(() =>
        {
            var session = _session;
            if (session is null)
                return Task.CompletedTask;

            foreach (var target in targets)
            {
                if (add)
                {
                    if (!session.TryAddOutput(target, out var err))
                    {
                        // Common error: line not yet acquirable (preview not running, NDI carrier missing).
                        // Surface as a banner so the user knows the route didn't take.
                        if (!string.IsNullOrEmpty(err))
                            Dispatcher.UIThread.Post(() => StatusMessage = err);
                    }
                    else
                    {
                        // Size + push the matrix so cell routes install before the first chunk; fall back to
                        // legacy compound-gain path when the matrix hasn't been sized yet.
                        var b = Outputs.FirstOrDefault(o => o.Line == target);
                        if (b is not null)
                        {
                            SyncMatrixSourceChannelsFromSession(session);
                            b.Matrix.Resize(MatrixInputChannelCountFor(session), OutputChannelCountOrZero(b.Line));
                            ApplyOutputMatrixToSession(b);
                        }
                        ApplyOutputCompoundGainToSession(target);
                    }
                }
                else
                {
                    session.TryRemoveOutput(target, out _);
                }
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void OnOutputLineRemoving(object? sender, OutputLineViewModel line)
    {
        // Synchronous: the management VM is about to dispose the runtime. Drop our route now so the
        // router doesn't keep submitting to a output that's seconds away from disposal.
        var session = _session;
        if (session is null) return;
        try { session.TryRemoveOutput(line, out _); }
        catch { /* best effort — removal must not block teardown */ }

        // Clones tied to this parent are removed alongside (Outputs.Remove fires separate events for
        // each clone, so they'll route through this handler in turn).
    }

    /// <summary>
    /// Returns the lines the user has ticked, PLUS every clone of each ticked parent (§3.4
    /// PlayerRoutingMirror). Clones don't appear as separate checkboxes (see <see cref="SyncOutputsCollection"/>);
    /// their selection state is derived from their parent's tick. Order: parent immediately followed by
    /// its clones so the playback wiring path picks the parent as the negotiation lead.
    /// </summary>
    private IReadOnlyList<OutputLineViewModel> SelectedOutputLines()
    {
        var result = new List<OutputLineViewModel>();
        foreach (var binding in Outputs.Where(b => b.IsSelected))
        {
            result.Add(binding.Line);
            foreach (var clone in _outputs.GetClonesOf(binding.Line.Definition.Id))
                result.Add(clone);
        }
        return result;
    }

    /// <summary>Phase B (§3.6) — true when this player has the line wired into its session AND the
    /// session is currently in <see cref="IsPlaying"/> state. Used by the Edit confirm prompt.</summary>
    public bool IsActivelyPlayingThroughLine(OutputLineViewModel line) =>
        IsPlaying && _session?.HasWiredLine(line) == true;

    public bool IsHoldingAnyOutputLine(IReadOnlySet<Guid> outputLineIds)
    {
        if (_session is null || outputLineIds.Count == 0)
            return false;

        return _outputs.Outputs.Any(line =>
            outputLineIds.Contains(line.Definition.Id) && _session.HasWiredLine(line));
    }

    public Task ReleaseSessionForExternalPlaybackAsync() =>
        WithPlaybackArcAsync(() => CloseSessionCoreInnerAsync(deferIdleSync: false));

    /// <summary>
    /// Apply cue-level audio routing overrides onto this player's matrix model.
    /// Uses cue virtual output channel numbers (VOut 1..N) mapped in current selected-output order.
    /// </summary>
    /// <summary>Maps saved output display names that are missing on this machine to replacements.</summary>
    public void RemapSelectedOutputs(IReadOnlyDictionary<string, string> missingToReplacement)
    {
        if (missingToReplacement.Count == 0)
            return;

        SuppressVideoRouteConflictPrompt(() =>
        {
            foreach (var (_, replacement) in missingToReplacement)
            {
                var binding = Outputs.FirstOrDefault(b =>
                    string.Equals(b.Line.Definition.DisplayName, replacement, StringComparison.OrdinalIgnoreCase));
                if (binding is not null)
                    binding.IsSelected = true;
            }
        });

        RebuildAudioMatrixRows();
        ApplyAllOutputMatricesToSession();
        ApplyAllOutputGainsToSession();
    }

    /// <summary>Phase 4 retired the cue-as-MediaPlayer-overlay path — cue audio routing now lives on
    /// <c>MediaCueNode.AudioRoutes</c> and is honored by the future <c>CuePlaybackEngine</c> (Phase 4.6).
    /// This stub stays so the existing executor wiring compiles until 4.6 lands and removes the call site.</summary>
    public void ApplyCueRouteOverrides(MediaCueNode cue)
    {
        _ = cue;
    }

    /// <summary>Phase A — public snapshot for project save (§7). Internally still calls the same builder
    /// used by the per-player Save Player command, so the JSON shape stays identical.</summary>
    public MediaPlayerConfig BuildPlayerConfigSnapshot() => BuildPlayerConfig();

    /// <summary>Phase A — public companion to <see cref="BuildPlayerConfigSnapshot"/>; applies a player
    /// config in-place. Same semantics as the per-player Load Player command.</summary>
    public void ApplyPlayerConfigSnapshot(MediaPlayerConfig config) => ApplyPlayerConfig(config);

    private MediaPlayerConfig BuildPlayerConfig() => new()
    {
        Name = Name,
        PlaylistTabs = PlaylistTabs.Select(t => t.ToConfig()).ToList(),
        SelectedPlaylistTabIndex = SelectedPlaylistTab is null ? 0 : Math.Max(0, PlaylistTabs.IndexOf(SelectedPlaylistTab)),
        // Phase C.5 — the discriminated items live in PlaylistTabs[*].Items now. Keep the legacy flat
        // file-path projection for v1 readers (HaPlay builds older than the Items field) so they don't
        // silently lose the playlist on round-trip through an older build.
        PlaylistPaths = PlaylistItems.OfType<FilePlaylistItem>().Select(f => f.Path).ToList(),
        MediaFilePath = MediaFilePath,
        SelectedPlaylistPath = _currentPlaylistItem is FilePlaylistItem cf ? cf.Path : null,
        FallbackImagePath = FallbackImagePath,
        ChannelPresetRules = ChannelPresetRules.ToList(),
        IsLooping = IsLooping,
        AutoAdvancePlaylist = AutoAdvancePlaylist,
        HoldFallbackVideo = HoldFallbackVideo,
        MasterVolumeDb = MasterVolumeDb,
        MasterMuted = MasterMuted,
        OutputPreset = OutputPreset,
        TransitionMode = TransitionMode,
        TransitionDurationMs = TransitionDurationMs,
        CustomOutputWidth = SanitizedCustomOutputWidth(),
        CustomOutputHeight = SanitizedCustomOutputHeight(),
        HeadphonesCueEnabled = HeadphonesCueEnabled,
        HeadphonesCueOutputId = SelectedHeadphonesCueTarget?.Kind == HeadphonesCueTargetOption.TargetKind.Direct
            ? SelectedHeadphonesCueTarget.Identity
            : null,
        HeadphonesCueSharedBusId = SelectedHeadphonesCueTarget?.Kind == HeadphonesCueTargetOption.TargetKind.SharedBus
            ? SelectedHeadphonesCueTarget.Identity
            : null,
        HeadphonesCueTapPoint = HeadphonesCueTapPoint,
        HeadphonesCueGainDb = Math.Clamp(HeadphonesCueGainDb, -60.0, 12.0),
        SelectedOutputDisplayNames = Outputs
            .Where(b => b.IsSelected)
            .Select(b => b.Line.Definition.DisplayName)
            .ToList(),
        OutputGains = Outputs
            .Where(b => Math.Abs(b.GainDb) > 0.0001 || b.IsMuted || b.MixMode != AudioRouteMixMode.Stereo
                        || HasNonDefaultMatrix(b))
            .Select(b => new OutputGainConfig
            {
                OutputDisplayName = b.Line.Definition.DisplayName,
                GainDb = b.GainDb,
                Muted = b.IsMuted,
                MixMode = b.MixMode,
                MatrixCells = HasNonDefaultMatrix(b) ? b.Matrix.ToPersistableCells().ToList() : new(),
            })
            .ToList(),
        AudioMatrixInputChannels = ShouldPersistAudioMatrixInputChannels()
            ? Math.Clamp(AudioMatrixSourceChannels, 1, 64)
            : 0,
        InputTrims = AudioMatrixInputTrims
            .Where(t => Math.Abs(t.GainDb) > 0.0001 || t.Muted)
            .Select(t => new InputChannelTrimConfig
            {
                InputChannel = t.InputChannel,
                GainDb = t.GainDb,
                Muted = t.Muted,
            })
            .ToList(),
    };

    private bool ShouldPersistAudioMatrixInputChannels() =>
        _audioMatrixSourceChannelsExplicit
        || AudioMatrixInputTrims.Any(t => Math.Abs(t.GainDb) > 0.0001 || t.Muted)
        || Outputs.Any(HasNonDefaultMatrix);

    /// <summary>Phase C — a matrix is non-default when any cell deviates from the identity layout
    /// produced by <see cref="AudioMatrixViewModel.Resize"/> (audible diagonal cells at 0 dB; everything
    /// else muted). We persist only non-default matrices to keep saved configs compact.</summary>
    private static bool HasNonDefaultMatrix(PlayerOutputBinding b)
    {
        if (b.Matrix.Cells.Count == 0) return false;
        foreach (var c in b.Matrix.Cells)
        {
            var isDiagonal = b.Matrix.InputChannelCount >= 2
                ? c.InputChannel == c.OutputChannel
                : c.InputChannel == 0;
            var expectedMuted = !isDiagonal;
            var expectedGain = isDiagonal ? AudioMatrixDefaults.IdentityGainDb : AudioMatrixDefaults.MutedFloorDb;
            if (c.Muted != expectedMuted) return true;
            if (Math.Abs(c.GainDb - expectedGain) > 0.001) return true;
        }
        return false;
    }

    private void ApplyPlayerConfig(MediaPlayerConfig config)
    {
        Name = string.IsNullOrWhiteSpace(config.Name) ? Name : config.Name;

        ChannelPresetRules.Clear();
        foreach (var rule in config.ChannelPresetRules)
            ChannelPresetRules.Add(rule);

        PlaylistTabs.Clear();
        var tabs = config.PlaylistTabs.Count > 0
            ? config.PlaylistTabs
            : new List<PlaylistConfig>
            {
                new()
                {
                    Name = "Set A",
                    // v1 player-config fallback: project the top-level flat path list onto the
                    // PlaylistConfig.Paths legacy field; PlaylistTabViewModel.FromConfig promotes those to
                    // FilePlaylistItem entries (§6.8).
                    Paths = config.PlaylistPaths.Count > 0 ? config.PlaylistPaths : null,
                    SelectedPath = config.SelectedPlaylistPath,
                    IsLooping = config.IsLooping,
                    AutoAdvance = config.AutoAdvancePlaylist,
                },
            };

        foreach (var tabConfig in tabs)
            PlaylistTabs.Add(PlaylistTabViewModel.FromConfig(tabConfig));
        if (PlaylistTabs.Count == 0)
            PlaylistTabs.Add(new PlaylistTabViewModel("Set A"));

        MediaFilePath = config.MediaFilePath;
        _currentPlaylistItem = null;
        OnPropertyChanged(nameof(CurrentMediaDisplay));
        var selectedIndex = Math.Clamp(config.SelectedPlaylistTabIndex, 0, PlaylistTabs.Count - 1);
        SelectedPlaylistTab = PlaylistTabs[selectedIndex];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
        FallbackImagePath = config.FallbackImagePath;
        HoldFallbackVideo = config.HoldFallbackVideo;
        MasterVolumeDb = config.MasterVolumeDb;
        MasterMuted = config.MasterMuted;
        OutputPreset = config.OutputPreset;
        TransitionMode = config.TransitionMode;
        TransitionDurationMs = config.TransitionDurationMs <= 0 ? 500 : config.TransitionDurationMs;
        CustomOutputWidth = Math.Clamp(config.CustomOutputWidth, 16, 7680);
        CustomOutputHeight = Math.Clamp(config.CustomOutputHeight, 16, 4320);
        HeadphonesCueEnabled = config.HeadphonesCueEnabled;
        HeadphonesCueTapPoint = config.HeadphonesCueTapPoint;
        HeadphonesCueGainDb = Math.Clamp(config.HeadphonesCueGainDb, -60.0, 12.0);

        var wanted = new HashSet<string>(config.SelectedOutputDisplayNames, StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        SuppressVideoRouteConflictPrompt(() =>
        {
            foreach (var binding in Outputs)
            {
                var name = binding.Line.Definition.DisplayName;
                var selected = wanted.Contains(name);
                binding.IsSelected = selected;
                if (selected) missing.Remove(name);
            }
        });

        var savedInputChannels = config.AudioMatrixInputChannels > 0
            ? config.AudioMatrixInputChannels
            : InferSavedAudioMatrixInputChannels(config);
        SetAudioMatrixSourceChannels(savedInputChannels > 0 ? savedInputChannels : 2,
            explicitValue: savedInputChannels > 0,
            resize: true);

        ApplyBindingGainFromConfig(config.OutputGains
            .Where(g => !string.IsNullOrWhiteSpace(g.OutputDisplayName))
            .GroupBy(g => g.OutputDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase));
        _pendingInputTrimConfigs = config.InputTrims
            .Where(t => t.InputChannel >= 0)
            .GroupBy(t => t.InputChannel)
            .ToDictionary(g => g.Key, g => g.Last());
        RebuildInputTrimRows(AudioMatrixInputChannelCount);
        RebuildAudioMatrixRouteRows();
        RefreshHeadphonesCueTargets();
        SelectHeadphonesCueTarget(config.HeadphonesCueOutputId, config.HeadphonesCueSharedBusId);
        if (HeadphonesCueEnabled && SelectedHeadphonesCueTarget?.ResolvedLine is null)
            HeadphonesCueEnabled = false;

        StatusMessage = missing.Count > 0
            ? $"Loaded. Missing outputs: {string.Join(", ", missing)}."
            : null;
    }

    private static int InferSavedAudioMatrixInputChannels(MediaPlayerConfig config)
    {
        var fromCells = config.OutputGains
            .SelectMany(g => g.MatrixCells)
            .Where(c => c.InputChannel >= 0)
            .Select(c => c.InputChannel + 1)
            .DefaultIfEmpty(0)
            .Max();
        var fromTrims = config.InputTrims
            .Where(t => t.InputChannel >= 0)
            .Select(t => t.InputChannel + 1)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(fromCells, fromTrims);
    }

    private static string SanitizeFileName(string name, string extension = "haplay.json")
    {
        if (string.IsNullOrWhiteSpace(name))
            return "player." + extension;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return clean + "." + extension.TrimStart('.');
    }

    private async Task CloseSessionCoreInnerAsync(bool deferIdleSync, bool resetPlayingUi = true)
    {
        SDebug.ChangeTrace.Step("CloseSession: UI detach begin");
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CancelCueEnvelope();
            StopHoldPumpTimer();
            _loopTimer?.Stop();
            _loopTimer = null;
            var snap = _session;
            if (snap is not null)
            {
                try { snap.Player.PlayClock.PositionChanged -= OnClockPositionChanged; }
                catch { /* best effort */ }
                UnhookVideoFaultRecovery(snap);
                _session = null;
                _throughputDiagnostics.Reset();
            }
            return snap;
        });
        SDebug.ChangeTrace.Step(snapshot is null ? "CloseSession: no session" : "CloseSession: UI detach done");

        if (snapshot is not null)
        {
            await CancelSpeculativeMediaWorkBeforeSessionDisposeAsync().ConfigureAwait(false);

            // Pause is bounded, but session disposal is a required boundary before opening another file.
            // Leaving a half-disposed FFmpeg/D3D11 graph in the background can race the next open.
            await RunRequiredTransportAsync(() =>
            {
                try
                {
                    SDebug.ChangeTrace.Step("CloseSession: Router.Pause begin");
                    using var pauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { snapshot.Router.PauseSkippingSharedMuxFlush(pauseCts.Token); }
                    catch (OperationCanceledException) { /* bounded */ }
                    catch (ObjectDisposedException) { /* already torn down */ }
                    SDebug.ChangeTrace.Step("CloseSession: Router.Pause done");
                }
                catch { /* best effort */ }

                try
                {
                    SDebug.ChangeTrace.Step("CloseSession: Dispose begin");
                    snapshot.Dispose();
                    SDebug.ChangeTrace.Step("CloseSession: Dispose done");
                }
                catch { /* best effort */ }
            }, TimeSpan.FromSeconds(8), "CloseSession transport dispose");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsMediaLoaded = false;
            if (resetPlayingUi) IsPlaying = false;
            CurrentPosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            SeekSliderValue = 0;
            // Tearing down a session returns the player to idle. A prior Failed state is set after this
            // runs (on the next open), and the WaitingForSource retry state owns its own lifecycle, so
            // only reset when we're not actively waiting for a live source to return.
            if (LoadState != PlayerLoadState.WaitingForSource)
                LoadState = PlayerLoadState.Idle;
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SeekToSliderCommand.NotifyCanExecuteChanged();
            if (!deferIdleSync) SyncIdleSlate();
        });
        SDebug.ChangeTrace.Step("CloseSession: UI cleanup done");
    }

    private async Task CancelSpeculativeMediaWorkBeforeSessionDisposeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(CancelPreOpen);
        await CancelWaveformExtractionAndWaitAsync().ConfigureAwait(false);
        await Task.Run(_decoderCache.InvalidateAll).ConfigureAwait(false);
    }

    private bool CanLoadMedia()
    {
        // Phase C.5 — live items go through TryCreate(PlaylistItem) which dispatches to TryCreateLive.
        // File items need a readable path; live items are accepted unconditionally (the playback path
        // will surface its own error if the device / source can't be resolved).
        if (_currentPlaylistItem is { IsLive: true })
            return true;
        if (_currentPlaylistItem is FilePlaylistItem f && File.Exists(f.Path))
            return true;
        return _currentPlaylistItem is null
               && !string.IsNullOrWhiteSpace(MediaFilePath)
               && File.Exists(MediaFilePath!);
    }

    private async Task AdoptPreRolledSessionAsync(
        HaPlayPlaybackSession session,
        PlaylistItem item,
        MediaCueNode cueRoutes,
        CancellationToken ct)
    {
        _ = ct;
        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: false).ConfigureAwait(false);

            var holdFb = await Dispatcher.UIThread.InvokeAsync<bool>(() =>
            {
                StopIdleSlate();
                _outputs.StopPreviewsForPlayback(SelectedOutputLines());
                _currentPlaylistItem = item;
                MediaFilePath = item is FilePlaylistItem f ? f.Path : null;
                OnPropertyChanged(nameof(CurrentMediaDisplay));

                _session = session;
                IsMediaLoaded = true;
                StatusMessage = null;
                Duration = session.Player.HasContainerDecoder
                           && session.Player.Decoder.Audio is ISeekableSource a
                    ? a.Duration
                    : TimeSpan.Zero;

                session.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    session.ApplyFallbackImage(FallbackImagePath);
                session.SetHoldFallback(HoldFallbackVideo);

                ApplyCueRouteOverrides(cueRoutes);
                SyncMatrixSourceChannelsFromSession(session);
                ResizeSelectedAudioMatrices(session);
                RebuildAudioMatrixRows();
                ApplyAllOutputMatricesToSession();
                ApplyAllOutputGainsToSession();

                if (HoldFallbackVideo)
                {
                    try { session.PumpHoldFrames(session.Player.PlayClock.CurrentPosition); }
                    catch { /* best effort */ }
                }

                EnsureLoopTimerStarted();
                return HoldFallbackVideo;
            });

            if (holdFb)
                await Dispatcher.UIThread.InvokeAsync(StartHoldPumpTimer);
        });
    }

    private async Task OpenOrReloadAsync()
    {
        SDebug.ChangeTrace.Step("OpenOrReloadAsync entered");
        if (!CanLoadMedia())
        {
            SDebug.ChangeTrace.Step("OpenOrReload: CanLoadMedia=false");
            return;
        }

        SDebug.ChangeTrace.Step("OpenOrReload: CanLoadMedia=true");
        var resumeAfterOpen = await Dispatcher.UIThread.InvokeAsync(() => IsPlaying);
        SDebug.ChangeTrace.Step($"OpenOrReload: IsPlaying={resumeAfterOpen} (UI thread)");

        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: !resumeAfterOpen);

            var (item, selected) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                LoadState = PlayerLoadState.Loading;
                var lines = SelectedOutputLines();
                _outputs.StopPreviewsForPlayback(lines);
                PlaylistItem? effective = _currentPlaylistItem;
                if (effective is null && !string.IsNullOrWhiteSpace(MediaFilePath))
                    effective = new FilePlaylistItem(MediaFilePath!);
                return (effective, lines);
            });
            SDebug.ChangeTrace.Step($"OpenOrReload: outputs selected (count={selected.Count})");

            if (item is null) return;

            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            var fileOpts = _pendingCueFilePlayback ?? CurrentFilePlaybackOptions();
            _cuePreRoll.InvalidateAll();
            SDebug.ChangeTrace.Step("OpenOrReload: cue pre-roll invalidated");

            var decoderCacheHit = false;
            await Task.Run(() =>
            {
                SDebug.ChangeTrace.Step("OpenOrReload: TryCreate begin (thread pool)");
                var preOpened = item is FilePlaylistItem fi ? _decoderCache.TryTake(fi.Path, fi.AudioTrackIndex) : null;
                decoderCacheHit = preOpened is not null;
                if (!HaPlayPlaybackSession.TryCreate(item, selected, _outputs, out created, out createErr, fileOpts, preOpened))
                    created = null;
                SDebug.ChangeTrace.Step(
                    $"OpenOrReload: TryCreate end (cache={(decoderCacheHit ? "hit" : "miss")}, ok={created is not null})");
            }).ConfigureAwait(false);

            var holdFbAfterOpen = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SDebug.ChangeTrace.Step("OpenOrReload: UI bind session begin");
                if (created is null)
                {
                    if (item.IsLive && GetRetrySeconds(item) > 0)
                    {
                        EnterWaitingForSource(item, createErr ?? "source unavailable");
                    }
                    else
                    {
                        StatusMessage = createErr ?? "Failed to open media.";
                        LastLoadError = $"{item.DisplayName}: {createErr ?? "failed to open media"}";
                        LoadState = PlayerLoadState.Failed;
                    }
                    SyncIdleSlate();
                    SDebug.ChangeTrace.Step($"OpenOrReload: create failed ({createErr ?? "unknown"})");
                    return false;
                }

                ExitWaitingForSource();
                _session = created;
                IsMediaLoaded = true;
                // Play-what-you-can: a session can open with some lines skipped (dead NDI carrier,
                // held PortAudio device) — keep playing but tell the operator which lines are silent.
                StatusMessage = created.OpenWarnings.Count > 0 ? string.Join(" ", created.OpenWarnings) : null;
                LastLoadError = null;
                LoadState = PlayerLoadState.Ready;
                Duration = item.IsLive
                    ? TimeSpan.Zero
                    : (created.Player.HasContainerDecoder
                        && created.Player.Decoder.Audio is ISeekableSource a
                        ? a.Duration
                        : TimeSpan.Zero);

                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                // Normal (non-cue) file playback: recover automatically if the decode loop faults (e.g. a flaky
                // hardware decoder) by latching to software decode and reloading. Cue-opened sessions are watched
                // by the session itself (gate latch) but not auto-reloaded here — the cue engine owns their lifecycle.
                HookVideoFaultRecovery(created);
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    created.ApplyFallbackImage(FallbackImagePath);
                created.SetHoldFallback(HoldFallbackVideo);

                SyncMatrixSourceChannelsFromSession(created);
                ResizeSelectedAudioMatrices(created);
                SDebug.ChangeTrace.Step($"OpenOrReload: matrix resized (srcCh={MatrixInputChannelCountFor(created)})");

                RebuildAudioMatrixRows();
                SDebug.ChangeTrace.Step("OpenOrReload: RebuildAudioMatrixRows");

                ApplyAllOutputMatricesToSession();
                SDebug.ChangeTrace.Step("OpenOrReload: ApplyAllOutputMatricesToSession");

                ApplyAllOutputGainsToSession();
                SDebug.ChangeTrace.Step("OpenOrReload: ApplyAllOutputGainsToSession");

                if (HoldFallbackVideo)
                {
                    try { created.PumpHoldFrames(created.Player.PlayClock.CurrentPosition); }
                    catch { /* best effort */ }
                }

                EnsureLoopTimerStarted();
                SDebug.ChangeTrace.Step("OpenOrReload: UI bind session done");
                return HoldFallbackVideo;
            });

            if (created is null) return;

            if (resumeAfterOpen)
            {
                var s = created;
                var hf = holdFbAfterOpen;
                var ok = await RunBoundedAsync(() =>
                {
                    SDebug.ChangeTrace.Step("OpenOrReload: resume Play begin");
                    s.PrepareOutputsBeforePlay(hf);
                    SDebug.ChangeTrace.Step("OpenOrReload: PrepareOutputsBeforePlay");
                    s.PrepareLiveTransportBeforePlay();
                    SDebug.ChangeTrace.Step("OpenOrReload: PrepareLiveTransportBeforePlay");
                    s.ResetAllUnderrunBaselines();
                    SDebug.ChangeTrace.Step("OpenOrReload: ResetAllUnderrunBaselines");
                    s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
                    SDebug.ChangeTrace.Step("OpenOrReload: Router.Play (resume)");
                }, PlayWallTimeout, "OpenOrReload resume Play");

                if (!ok)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPlaying = false;
                        StatusMessage = "Playback failed to resume after loading.";
                    });
                    SDebug.ChangeTrace.Step("OpenOrReload: resume Play TIMED OUT");
                }
            }

            if (holdFbAfterOpen)
            {
                await Dispatcher.UIThread.InvokeAsync(StartHoldPumpTimer);
                SDebug.ChangeTrace.Step("OpenOrReload: StartHoldPumpTimer");
            }

            await Dispatcher.UIThread.InvokeAsync(() => StartWaveformExtraction(MediaFilePath));
            SDebug.ChangeTrace.Step("OpenOrReload: waveform extraction started");
        }).ConfigureAwait(false);
    }

    private void EnsureLoopTimerStarted()
    {
        if (_loopTimer is not null)
            return;
        _loopTimer = new DispatcherTimer { Interval = LoopPollRelaxed };
        _loopTimer.Tick += OnLoopTimerTick;
        _loopTimer.Start();
    }

    private void StartHoldPumpTimer()
    {
        if (_session is { RequiresHoldPump: false })
            return;
        if (_holdPumpTimer is not null)
            return;
        var period = TimeSpan.FromSeconds(1.0 / 30.0);
        _holdPumpTimer = new Timer(OnHoldPumpTick, null, period, period);
    }

    private void StopHoldPumpTimer()
    {
        var t = Interlocked.Exchange(ref _holdPumpTimer, null);
        t?.Dispose();
    }

    private void OnHoldPumpTick(object? state)
    {
        // Drop-not-queue if a previous tick is still in flight (output Submit can briefly block on NDI
        // SDK lock). Better to skip a frame than to stack tick handlers.
        if (Interlocked.CompareExchange(ref _holdPumpReentry, 1, 0) != 0)
            return;
        try
        {
            var session = _session;
            if (session is null || !IsMediaLoaded || !HoldFallbackVideo)
                return;
            var pt = session.Player.PlayClock.CurrentPosition;
            try { session.PumpHoldFrames(pt); }
            catch { /* best effort — output torn down mid-tick */ }
        }
        finally
        {
            Interlocked.Exchange(ref _holdPumpReentry, 0);
        }
    }

    private void OnClockPositionChanged(object? sender, TimeSpan e) =>
        Dispatcher.UIThread.Post(() =>
        {
            CurrentPosition = e;
            // Don't fight the user: while they're scrubbing the slider (or the committed seek arc is
            // still running) the thumb position is owned by the drag, not the playhead. Resuming clock
            // write-back once the arc finishes snaps the slider to the (correct, post-seek) position.
            if (Duration > TimeSpan.Zero && !IsScrubbing && !_seekArcRunning)
                SeekSliderValue = e.Ticks * 1000.0 / Duration.Ticks;
            PollAudioMeters();
        }, DispatcherPriority.Normal);

    // The loop timer also drives natural-end detection for loop wrap + playlist auto-advance, so a
    // fixed 500 ms poll means a track can run up to ~500 ms past its end before the next one starts.
    // Near the end of a finite file we tighten the cadence so the boundary fires within ~one fast
    // tick; live/idle stay relaxed to keep the dispatcher quiet.
    private static readonly TimeSpan LoopPollRelaxed = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LoopPollNearEnd = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan LoopPollNearEndWindow = TimeSpan.FromSeconds(1.5);

    private void OnLoopTimerTick(object? sender, EventArgs e)
    {
        AdjustLoopTimerCadence();
        _ = ProcessLoopTimerTickAsync();
    }

    /// <summary>Speeds up the loop poll when a finite file is within <see cref="LoopPollNearEndWindow"/>
    /// of its end so loop wrap / auto-advance fires promptly; relaxes back otherwise. Runs on the UI
    /// thread (DispatcherTimer tick).</summary>
    private void AdjustLoopTimerCadence()
    {
        if (_loopTimer is null) return;
        var nearEnd = IsPlaying
            && _session is { IsLive: false }
            && Duration > TimeSpan.Zero
            && RemainingTime <= LoopPollNearEndWindow;
        var target = nearEnd ? LoopPollNearEnd : LoopPollRelaxed;
        if (_loopTimer.Interval != target)
            _loopTimer.Interval = target;
    }

    private async Task ProcessLoopTimerTickAsync()
    {
        // Phase C.5 (§6.9) — drive the reconnect retry loop. Has priority over the playback advance
        // logic below since no _session exists while we're waiting. The retry tries to re-open the
        // last-known live item; on success the normal play path continues.
        if (IsWaitingForSource && _waitingItem is not null && DateTime.UtcNow >= _nextRetryAt)
        {
            var item = _waitingItem;
            _currentPlaylistItem = item;
            // Push the next deadline forward immediately so a slow open doesn't fire a retry storm
            // when the dispatcher catches up.
            var retrySec = GetRetrySeconds(item);
            _nextRetryAt = DateTime.UtcNow.AddSeconds(Math.Max(retrySec, 1));
            await OpenOrReloadAsync().ConfigureAwait(false);

            // If the open succeeded, we exited waiting AND _session is set — start the source naturally.
            if (_session is not null && !IsPlaying)
                await StartPlaybackAsync().ConfigureAwait(false);
            return;
        }

        if (_session is null || !IsMediaLoaded || !IsPlaying)
            return;

        var statsSession = _session;
        var statsLines = await Dispatcher.UIThread.InvokeAsync(SelectedOutputLines);
        _throughputDiagnostics.TryLogPeriodic(statsSession, statsLines);

        // Phase C.5 — live sessions never playlist-auto-advance (§6.5). Cue AutoFollow still fires when
        // the operator stops transport or the capture/receiver disconnects.
        if (_session.IsLive)
        {
            if (IsPlaying && _cuePlaybackActive && _session.IsLiveSourceDisconnected)
                await NotifyCuePlaybackNaturallyEndedAsync().ConfigureAwait(false);
            return;
        }

        var holdFb = HoldFallbackVideo;

        if (!await _playbackArc.WaitAsync(0).ConfigureAwait(false))
            return;

        var advancePlaylist = false;
        var resumePlayForPlaylist = false;
        try
        {
            var session = _session;
            if (session is null) return;

            if ((_activePlaybackTab?.IsLooping ?? IsLooping))
            {
                // Use audio's natural completion when an audio router is present (covers both audio-only
                // and audio+video sources). Falls back to video for video-only files where audio is null.
                var loopReady = session.Player.AudioRouter is { } loopAr
                    ? !loopAr.IsRunning && loopAr.CompletedNaturally
                    : session.Player.Video.CompletedNaturally;
                if (!loopReady) return;
                await RunBoundedCancelableAsync(ct =>
                    {
                        session.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, ct);
                        // No NDI warmup on loop wrap — receivers are already locked on and a silence gap would
                        // be audible between the last and first samples of the loop.
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.PrepareLiveTransportBeforePlay();
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    },
                    innerTimeout: TimeSpan.FromSeconds(3),
                    outerTimeout: TimeSpan.FromSeconds(5));

                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
                return;
            }

            var fileEnded = session.Player.AudioRouter is { } ar
                ? !ar.IsRunning && ar.CompletedNaturally
                : session.Player.Video.CompletedNaturally;
            if (!fileEnded) return;

            var (cuePlaybackActive, endBehavior) = await Dispatcher.UIThread.InvokeAsync(() =>
                (_cuePlaybackActive, _activeCueEndBehavior));
            if (cuePlaybackActive)
            {
                await NotifyCuePlaybackNaturallyEndedAsync(endBehavior, session).ConfigureAwait(false);
                return;
            }

            resumePlayForPlaylist = IsPlaying;
            advancePlaylist = _activePlaybackTab?.AutoAdvance ?? AutoAdvancePlaylist;
            await RunBoundedCancelableAsync(session.Router.PauseSkippingSharedMuxFlush,
                innerTimeout: TimeSpan.FromSeconds(1.5),
                outerTimeout: TimeSpan.FromSeconds(2.5));
        }
        finally
        {
            _playbackArc.Release();
        }

        if (!advancePlaylist)
        {
            // Router is paused but UI's IsPlaying still says "playing" — sync so the toggle reflects state.
            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);
            return;
        }

        var shouldLoadNext = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_session is null || !IsMediaLoaded) return false;
            IsPlaying = false;
            if (!TryGetAutoAdvanceItem(out var nextItem)) return false;
            _ = PrepareCurrentItemAsync(nextItem);
            if (_activePlaybackTab is not null)
                _activePlaybackTab.SelectedItem = nextItem;
            if (ReferenceEquals(_activePlaybackTab, SelectedPlaylistTab))
                SelectedPlaylistItem = nextItem;
            return true;
        });
        if (!shouldLoadNext) return;

        await OpenOrReloadAsync();

        if (!resumePlayForPlaylist) return;

        await WithPlaybackArcAsync(async () =>
        {
            var (s, holdForPrime) = await Dispatcher.UIThread.InvokeAsync(() => (_session, HoldFallbackVideo));
            if (s is null) return;

            var ok = await RunBoundedAsync(() =>
            {
                // Playlist advance — receivers may have drained between tracks.
                s.PrepareOutputsBeforePlay(holdForPrime);
                s.PrepareLiveTransportBeforePlay();
                s.ResetAllUnderrunBaselines();
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
            }, PlayWallTimeout, "Playlist advance Play");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok) return;
                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

}
