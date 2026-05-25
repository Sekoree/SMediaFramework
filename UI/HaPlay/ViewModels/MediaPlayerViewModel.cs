using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Audio;
using S.Media.NDI;
using S.Media.PortAudio;

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel : ViewModelBase
{
    private readonly OutputManagementViewModel _outputs;
    private readonly Action<MediaPlayerViewModel>? _requestRemove;
    private HaPlayPlaybackSession? _session;
    private readonly PlaybackThroughputDiagnostics _throughputDiagnostics = new();

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
            _activePlaybackTab = SelectedPlaylistTab;
            SelectedPlaylistItem = item;
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
                var srcCh = SourceChannelCountOrFallback(created);
                foreach (var binding in Outputs)
                    binding.Matrix.Resize(srcCh, OutputChannelCountOrZero(binding.Line));
                RebuildAudioMatrixRows();
                ApplyAllOutputMatricesToSession();
                ApplyAllOutputGainsToSession();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private async Task ApplyCueTransportAsync(MediaCueNode cue, CancellationToken ct)
    {
        if (cue.StartOffsetMs > 0 && _session?.IsLive != true)
            await SeekToTimeAsync(TimeSpan.FromMilliseconds(cue.StartOffsetMs), ct).ConfigureAwait(false);

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

            await RunBoundedCancelableAsync(innerCt =>
                {
                    session.Router.SeekCoordinatedSkippingSharedMuxFlush(t, innerCt);
                    if (playing)
                    {
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.PrepareLiveTransportBeforePlay();
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    }
                },
                innerTimeout: TimeSpan.FromSeconds(3),
                outerTimeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);

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
                var srcCh = SourceChannelCountOrFallback(created);
                foreach (var binding in Outputs)
                    binding.Matrix.Resize(srcCh, OutputChannelCountOrZero(binding.Line));
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
                if (!HaPlayPlaybackSession.TryCreate(item, lines, _outputs, out created, out err, fileOpts))
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
    /// file sessions still have a real <c>Decoder</c> and can read it from there. Falls back to 2 when
    /// nothing is loaded so the audio matrix sizes sanely on early calls.</summary>
    private static int SourceChannelCountOrFallback(HaPlayPlaybackSession? session)
    {
        if (session is null) return 2;
        if (session.IsLive)
            return session.SourceAudioFormat.Channels > 0 ? session.SourceAudioFormat.Channels : 2;
        if (session.Player.HasContainerDecoder && session.Player.Decoder.Audio is { } a)
            return a.Format.Channels;
        return 2;
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
        await _playbackArc.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _playbackArc.Release();
        }
    }

    public MediaPlayerViewModel(OutputManagementViewModel outputs, string name,
        Action<MediaPlayerViewModel>? requestRemove = null)
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
        _outputs.VirtualAudioChannelMapChanged += OnVirtualAudioChannelMapChanged;
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
        Dispatcher.UIThread.Post(() =>
        {
            SyncOutputsCollection();
            SyncIdleSlate();
        });
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

    private void OnVirtualAudioChannelMapChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            RebuildAudioMatrixRows();
            ApplyAllOutputMatricesToSession();
        });
    }

    public OutputManagementViewModel OutputsRepository => _outputs;

    /// <summary>Per-player checkbox bindings. Each player tracks its own selection so two players can route
    /// to overlapping subsets of outputs (e.g. main → NDI, preview → local SDL).</summary>
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

    /// <summary>
    /// One row per active matrix connection (audible cell). Backing source for the route list TreeDataGrid.
    /// Uses the same cell objects as <see cref="AudioMatrixRows"/>, so edits are fully synchronized.
    /// </summary>
    public ObservableCollection<AudioMatrixRouteRow> AudioMatrixRouteRows { get; } = new();

    /// <summary>
    /// Per-input-channel trims (column attenuation). Applied on top of every matrix cell from that input.
    /// </summary>
    public ObservableCollection<AudioMatrixInputTrimViewModel> AudioMatrixInputTrims { get; } = new();

    /// <summary>Phase C (§4.3.4) — current source channel count for the TreeDataGrid's input columns.
    /// 0 until a session opens. Watched by the view's code-behind to rebuild input columns.</summary>
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
    private bool _isLooping;

    /// <summary>When true, the loop timer auto-loads the next playlist entry on natural end of file.
    /// Defaults to false — auto-advance is rarely wanted in performance contexts where each track is cued.</summary>
    [ObservableProperty]
    private bool _autoAdvancePlaylist;

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
    private bool _isWaitingForSource;

    [ObservableProperty]
    private string? _waitingForSourceMessage;

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

    [ObservableProperty]
    private string? _statusMessage;

    public TimeSpan RemainingTime =>
        Duration > CurrentPosition ? Duration - CurrentPosition : TimeSpan.Zero;

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
        if (value)
            StopIdleSlate();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PlaybackStateLabel));
        OnPropertyChanged(nameof(PlaybackStateColor));
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
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
    }

    partial void OnIsMediaLoadedChanging(bool value) => _ = value;

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        _ = value;
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(MiddleTimeText));
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
        }
        finally
        {
            _syncingPlaylistTabState = false;
        }

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

    private void OnOutputBindingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PlayerOutputBinding binding)
            return;

        if (e.PropertyName == nameof(PlayerOutputBinding.IsSelected))
        {
            OnPropertyChanged(nameof(RoutingSummary));
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
        var inputChannels = 0;
        foreach (var slot in BuildVirtualOutputMap())
        {
            inputChannels = Math.Max(inputChannels, slot.Binding.Matrix.InputChannelCount);
            var label = $"VOut {slot.VirtualOutputChannel} · {slot.Binding.Line.Definition.DisplayName} · {OutputChannelSuffix(slot.Binding.Matrix.OutputChannelCount, slot.OutputChannel)}";
            AudioMatrixRows.Add(new AudioMatrixRow(slot.Binding, slot.OutputChannel, slot.VirtualOutputChannel, label));
        }

        AudioMatrixInputChannelCount = inputChannels;
        RebuildInputTrimRows(inputChannels);
        RebuildAudioMatrixRouteRows();
        OnPropertyChanged(nameof(HasAudioMatrix));
        AudioMatrixLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<VirtualOutputSlot> BuildVirtualOutputMap()
    {
        var rows = new List<VirtualOutputSlot>();
        var fallback = 1;
        var used = new HashSet<int>();
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount == 0 || binding.Matrix.OutputChannelCount == 0) continue;
            for (var oc = 0; oc < binding.Matrix.OutputChannelCount; oc++)
            {
                var assigned = _outputs.GetAssignedVirtualAudioChannel(binding.Line.Definition.Id, oc);
                var vout = assigned is > 0 ? assigned.Value : 0;
                if (vout <= 0)
                {
                    while (used.Contains(fallback))
                        fallback++;
                    vout = fallback++;
                }
                used.Add(vout);
                rows.Add(new VirtualOutputSlot(vout, binding, oc));
            }
        }
        return rows
            .OrderBy(r => r.VirtualOutputChannel)
            .ThenBy(r => r.Binding.Line.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.OutputChannel);
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
                            var srcCh = SourceChannelCountOrFallback(_session);
                            binding.Matrix.Resize(srcCh, OutputChannelCountOrZero(binding.Line));
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
            // Persisted cells apply once the matrix has been sized (which happens on session open / hot-add).
            // Until then, MixMode acts as the placeholder preset — when the matrix Resize runs it picks the
            // identity layout, and a subsequent ApplyConfig overlays the saved cells.
            if (gain.MatrixCells.Count > 0 && binding.Matrix.InputChannelCount > 0)
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
                            var srcCh = SourceChannelCountOrFallback(session);
                            b.Matrix.Resize(srcCh, OutputChannelCountOrZero(b.Line));
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

    [RelayCommand(CanExecute = nameof(CanRemovePlayer))]
    private void RemovePlayer()
    {
        if (_requestRemove is null) return;
        _ = CloseSessionAsync();
        _requestRemove(this);
    }

    private bool CanRemovePlayer() => _requestRemove is not null;

    partial void OnPreferLiveUyvyPassthroughChanged(bool value)
    {
        PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = value;
        try
        {
            var settings = AppSettings.Load();
            settings.PreferLiveUyvyPassthrough = value;
            settings.Save();
        }
        catch
        {
            /* best effort */
        }
    }

    partial void OnHoldFallbackVideoChanged(bool value)
    {
        OnPropertyChanged(nameof(HoldImageSummary));
        if (value && _session is not null && IsMediaLoaded && !string.IsNullOrWhiteSpace(FallbackImagePath))
        {
            // Phase 3 — toggling hold on (with an image path already set) must re-apply the image so
            // outputs reconfigure to the image's native size.
            try { _session.ApplyFallbackImage(FallbackImagePath); }
            catch { /* best effort */ }
        }

        _session?.SetHoldFallback(value);
        if (_session is not null && IsMediaLoaded)
        {
            if (value)
            {
                StartHoldPumpTimer();
            }
            else
            {
                StopHoldPumpTimer();
                // Restore the last real decoded frame at the current playhead so single-frame sources
                // (attached_pic / album cover art) come back instead of leaving receivers stuck on the
                // no-longer-pumped template.
                try
                {
                    var pt = _session.Player.PlayClock.CurrentPosition;
                    _session.ResubmitLastCachedFramesAt(pt);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        SyncIdleSlate();
    }

    partial void OnFallbackImagePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HoldImageSummary));
        if (_session is not null && !string.IsNullOrWhiteSpace(value))
            _session.ApplyFallbackImage(value);
        if (_session is not null && IsMediaLoaded && HoldFallbackVideo && !string.IsNullOrWhiteSpace(value))
        {
            try
            {
                _session.PumpHoldFrames(_session.Player.PlayClock.CurrentPosition);
            }
            catch
            {
                /* best effort */
            }

            StartHoldPumpTimer();
        }

        SyncIdleSlate();
    }

    [RelayCommand]
    private void AddPlaylistTab()
    {
        var tab = new PlaylistTabViewModel($"Set {PlaylistTabs.Count + 1}");
        PlaylistTabs.Add(tab);
        SelectedPlaylistTab = tab;
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistTab))]
    private void RemovePlaylistTab()
    {
        if (SelectedPlaylistTab is null || PlaylistTabs.Count <= 1)
            return;
        var idx = PlaylistTabs.IndexOf(SelectedPlaylistTab);
        if (idx < 0)
            return;
        PlaylistTabs.RemoveAt(idx);
        SelectedPlaylistTab = PlaylistTabs[Math.Clamp(idx, 0, PlaylistTabs.Count - 1)];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistTab() => PlaylistTabs.Count > 1;

    [RelayCommand]
    private async Task SavePlaylistTabAsync()
    {
        var top = TryGetMainWindow();
        var tab = SelectedPlaylistTab;
        if (top is null || tab is null)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save playlist tab",
            DefaultExtension = PlaylistIO.FileExtension,
            SuggestedFileName = SanitizeFileName(tab.Name, PlaylistIO.FileExtension),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay playlist") { Patterns = ["*." + PlaylistIO.FileExtension] },
            ],
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(opts);
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await PlaylistIO.SaveAsync(tab.ToConfig(), path).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = null);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Save playlist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadPlaylistTabAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Load playlist tab",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay playlist") { Patterns = ["*." + PlaylistIO.FileExtension, "*.json"] },
                new FilePickerFileType("M3U playlist") { Patterns = ["*.m3u", "*.m3u8"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            var config = await PlaylistIO.LoadAsync(path).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var tab = PlaylistTabViewModel.FromConfig(config);
                if (string.IsNullOrWhiteSpace(tab.Name))
                    tab.Name = Path.GetFileNameWithoutExtension(path);
                PlaylistTabs.Add(tab);
                SelectedPlaylistTab = tab;
                RemovePlaylistTabCommand.NotifyCanExecuteChanged();
                StatusMessage = null;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Load playlist failed: {ex.Message}");
        }
    }

    /// <summary>Phase C.5 (§6.4) — open the PortAudio-input dialog and, on commit, add the produced
    /// <see cref="PortAudioInputPlaylistItem"/> to the active playlist tab. Live items sit alongside
    /// file items in the same list and round-trip through the project file (§6.8).</summary>
    [RelayCommand]
    private async Task AddPortAudioInputAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddPortAudioInputDialogViewModel();
        dialogVm.ReloadHostApis();
        var dialog = new Views.Dialogs.AddPortAudioInputDialog { DataContext = dialogVm };

        var result = await dialog.ShowDialog<PortAudioInputPlaylistItem?>(top);
        if (result is null) return;

        PlaylistItems.Add(result);
        SelectedPlaylistItem = result;
    }

    /// <summary>Phase C.5 (§6.3) — open the NDI-input dialog and add the produced
    /// <see cref="NDIInputPlaylistItem"/>. The discovery list + manual-name path land alongside the
    /// dialog VM in task #3; until then the menu entry surfaces a banner so users know the data
    /// model is ready but the dialog isn't wired yet.</summary>
    [RelayCommand]
    private async Task AddNDIInputAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var dialogVm = new Dialogs.AddNDIInputDialogViewModel();
        await dialogVm.StartDiscoveryAsync();
        var dialog = new Views.Dialogs.AddNDIInputDialog { DataContext = dialogVm };

        try
        {
            var result = await dialog.ShowDialog<NDIInputPlaylistItem?>(top);
            if (result is null) return;
            PlaylistItems.Add(result);
            SelectedPlaylistItem = result;
        }
        finally
        {
            dialogVm.StopDiscovery();
        }
    }

    /// <summary>§8.5 quick-play — load and play the first dropped file without mutating the playlist.</summary>
    public async Task QuickPlayDroppedFilesAsync(IEnumerable<string> paths)
    {
        var path = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (path is null)
            return;
        await PlayPlaylistItemAsync(new FilePlaylistItem(path)).ConfigureAwait(false);
    }

    [RelayCommand]
    public void AddDroppedFilesToPlaylist(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            if (PlaylistItems.OfType<FilePlaylistItem>().Any(f =>
                    string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            PlaylistItems.Add(new FilePlaylistItem(path));
            added++;
        }

        if (added > 0 && SelectedPlaylistItem is null)
            SelectedPlaylistItem = PlaylistItems[0];
        if (added > 0)
            StatusMessage = $"Added {added} file(s) to playlist.";
    }

    [RelayCommand]
    private async Task AddFilesToPlaylistAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log("enter");
        try
        {
            var top = TryGetMainWindow();
            if (top is null) { Log("no main window — abort"); return; }

            var opts = new FilePickerOpenOptions { Title = "Add files to playlist", AllowMultiple = true };
            opts.FileTypeFilter =
            [
                new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm", "*.m4v", "*.avi", "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ];

            Log("calling OpenFilePickerAsync");
            var files = await top.StorageProvider.OpenFilePickerAsync(opts);
            Log($"picker returned count={files.Count}");

            int added = 0, skipped = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                Log($"file path='{path}'");
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    skipped++;
                    continue;
                }
                // Dedup against existing file items (same-path live items don't make sense and don't exist here).
                if (PlaylistItems.OfType<FilePlaylistItem>().Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)))
                {
                    skipped++;
                    continue;
                }
                PlaylistItems.Add(new FilePlaylistItem(path));
                added++;
            }
            Log($"foreach done added={added} skipped={skipped} count={PlaylistItems.Count}");

            if (SelectedPlaylistItem is null && PlaylistItems.Count > 0)
            {
                Log("setting initial SelectedPlaylistItem");
                SelectedPlaylistItem = PlaylistItems[0];
                Log("set initial SelectedPlaylistItem done");
            }
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            Log("exit");
        }

        void Log(string msg) =>
            Console.WriteLine($"[AddFiles {sw.ElapsedMilliseconds,5}ms tid={Environment.CurrentManagedThreadId} ui={Dispatcher.UIThread.CheckAccess()}] {msg}");
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistItem))]
    private void RemoveFromPlaylist()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i < 0) return;
        PlaylistItems.RemoveAt(i);
        SelectedPlaylistItem = PlaylistItems.Count > 0
            ? PlaylistItems[Math.Min(i, PlaylistItems.Count - 1)]
            : null;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistItem() =>
        SelectedPlaylistItem is not null && PlaylistItems.Contains(SelectedPlaylistItem);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemUp))]
    private void MovePlaylistItemUp()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i <= 0) return;
        (PlaylistItems[i - 1], PlaylistItems[i]) = (PlaylistItems[i], PlaylistItems[i - 1]);
        SelectedPlaylistItem = item;
    }

    private bool CanMovePlaylistItemUp() =>
        SelectedPlaylistItem is not null && PlaylistItems.IndexOf(SelectedPlaylistItem) > 0;

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemDown))]
    private void MovePlaylistItemDown()
    {
        var item = SelectedPlaylistItem;
        if (item is null) return;
        var i = PlaylistItems.IndexOf(item);
        if (i < 0 || i >= PlaylistItems.Count - 1) return;
        (PlaylistItems[i + 1], PlaylistItems[i]) = (PlaylistItems[i], PlaylistItems[i + 1]);
        SelectedPlaylistItem = item;
    }

    private bool CanMovePlaylistItemDown()
    {
        if (SelectedPlaylistItem is null) return false;
        var idx = PlaylistItems.IndexOf(SelectedPlaylistItem);
        return idx >= 0 && idx < PlaylistItems.Count - 1;
    }

    public void MovePlaylistItem(PlaylistItem item, int targetIndex)
    {
        var sourceIndex = PlaylistItems.IndexOf(item);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= PlaylistItems.Count || sourceIndex == targetIndex)
            return;
        PlaylistItems.Move(sourceIndex, targetIndex);
        SelectedPlaylistItem = item;
    }

    /// <summary>Invoked from the view when the user double-clicks a playlist item — load it and start playing.
    /// Routes both file items and live items through the open path; live playback wiring lands in Phase C.5
    /// (currently surfaces "live items not yet supported" on play).</summary>
    public async Task PlayPlaylistItemAsync(PlaylistItem item)
    {
        if (item is null) return;
        if (_pendingCueFilePlayback is null)
            CancelCueEnvelope();
        _activePlaybackTab = SelectedPlaylistTab;
        SelectedPlaylistItem = item;
        await PrepareCurrentItemAsync(item).ConfigureAwait(false);
        await OpenOrReloadAsync().ConfigureAwait(false);
        if (_session is not null && !IsPlaying)
            await StartPlaybackAsync().ConfigureAwait(false);
    }

    /// <summary>Phase C.5 — sets <see cref="_currentPlaylistItem"/> and the file-item path projection that
    /// the existing file-based open path consumes. Live items leave <see cref="MediaFilePath"/> null and are
    /// short-circuited by <see cref="OpenOrReloadAsync"/> until live wiring lands (task #4).</summary>
    private Task PrepareCurrentItemAsync(PlaylistItem? item)
    {
        _currentPlaylistItem = item;
        MediaFilePath = item is FilePlaylistItem f ? f.Path : null;
        OnPropertyChanged(nameof(CurrentMediaDisplay));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task BrowseFallbackAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerOpenOptions { Title = "Fallback image (PNG / JPEG)", AllowMultiple = false };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FallbackImagePath = path;
    }

    [RelayCommand]
    private async Task OpenPlayerSettingsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var dialog = new Views.Dialogs.PlayerSettingsDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private async Task OpenAudioMatrixAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var dialog = new Views.Dialogs.AudioMatrixDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private async Task OpenRoutingAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var dialog = new Views.Dialogs.RoutingDialog { DataContext = this };
        await dialog.ShowDialog<object?>(owner);
    }

    [RelayCommand]
    private async Task SavePlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save player configuration",
            DefaultExtension = "haplay.json",
            SuggestedFileName = SanitizeFileName(Name),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
            ],
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(opts);
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var config = BuildPlayerConfig();
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, config, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = null);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadPlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Load player configuration",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        MediaPlayerConfig? config;
        try
        {
            await using var stream = File.OpenRead(path);
            config = await JsonSerializer
                .DeserializeAsync(stream, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Load failed: {ex.Message}");
            return;
        }

        if (config is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() => ApplyPlayerConfig(config));
    }

    /// <summary>Phase B (§3.6) — true when this player has the line wired into its session AND the
    /// session is currently in <see cref="IsPlaying"/> state. Used by the Edit confirm prompt.</summary>
    public bool IsActivelyPlayingThroughLine(OutputLineViewModel line) =>
        IsPlaying && _session?.HasWiredLine(line) == true;

    /// <summary>
    /// Apply cue-level audio routing overrides onto this player's matrix model.
    /// Uses cue virtual output channel numbers (VOut 1..N) mapped in current selected-output order.
    /// </summary>
    /// <summary>Maps saved output display names that are missing on this machine to replacements.</summary>
    public void RemapSelectedOutputs(IReadOnlyDictionary<string, string> missingToReplacement)
    {
        if (missingToReplacement.Count == 0)
            return;

        foreach (var (_, replacement) in missingToReplacement)
        {
            var binding = Outputs.FirstOrDefault(b =>
                string.Equals(b.Line.Definition.DisplayName, replacement, StringComparison.OrdinalIgnoreCase));
            if (binding is not null)
                binding.IsSelected = true;
        }

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
        foreach (var binding in Outputs)
        {
            var name = binding.Line.Definition.DisplayName;
            var selected = wanted.Contains(name);
            binding.IsSelected = selected;
            if (selected) missing.Remove(name);
        }
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
                _session = null;
                _throughputDiagnostics.Reset();
            }
            return snap;
        });

        if (snapshot is not null)
        {
            // Two-tier wall: 2s inner ct keeps Pause from hanging; 8s outer wall lets Dispose finish even on slow
            // outputs. A previous 50s outer cap would freeze the UI for nearly a minute if a output blocked.
            await RunBoundedAsync(() =>
            {
                try
                {
                    using var pauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { snapshot.Router.PauseSkippingSharedMuxFlush(pauseCts.Token); }
                    catch (OperationCanceledException) { /* bounded */ }
                    catch (ObjectDisposedException) { /* already torn down */ }
                }
                catch { /* best effort */ }

                try { snapshot.Dispose(); }
                catch { /* best effort */ }
            }, TimeSpan.FromSeconds(8));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsMediaLoaded = false;
            if (resetPlayingUi) IsPlaying = false;
            CurrentPosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            SeekSliderValue = 0;
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SeekToSliderCommand.NotifyCanExecuteChanged();
            if (!deferIdleSync) SyncIdleSlate();
        });
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
                var srcCh = SourceChannelCountOrFallback(session);
                foreach (var binding in Outputs)
                    binding.Matrix.Resize(srcCh, OutputChannelCountOrZero(binding.Line));
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
        if (!CanLoadMedia())
            return;

        var resumeAfterOpen = await Dispatcher.UIThread.InvokeAsync(() => IsPlaying);

        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: !resumeAfterOpen);

            var (item, selected) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                var lines = SelectedOutputLines();
                _outputs.StopPreviewsForPlayback(lines);
                // Synthesize a FilePlaylistItem if MediaFilePath is set but _currentPlaylistItem isn't
                // (legacy: ApplyPlayerConfig sets MediaFilePath alone).
                PlaylistItem? effective = _currentPlaylistItem;
                if (effective is null && !string.IsNullOrWhiteSpace(MediaFilePath))
                    effective = new FilePlaylistItem(MediaFilePath!);
                return (effective, lines);
            });

            if (item is null) return;

            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            var fileOpts = _pendingCueFilePlayback ?? CurrentFilePlaybackOptions();
            _cuePreRoll.InvalidateAll();
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreate(item, selected, _outputs, out created, out createErr, fileOpts))
                    created = null;
            }).ConfigureAwait(false);

            // Never use InvokeAsync(async () => await Task.Run(...)): the UI dispatcher can deadlock with the
            // threadpool continuation that is waiting for InvokeAsync to complete.
            var holdFbAfterOpen = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    // Phase C.5 (§6.9) — for a live item with retries enabled, enter the waiting state so the
                    // loop timer re-attempts to open. Manual-name NDI items and recently-unplugged USB capture
                    // devices both end up here. Files / disabled-retry items just surface the error.
                    if (item.IsLive && GetRetrySeconds(item) > 0)
                        EnterWaitingForSource(item, createErr ?? "source unavailable");
                    else
                        StatusMessage = createErr ?? "Failed to open media.";
                    SyncIdleSlate();
                    return false;
                }

                ExitWaitingForSource();

                _session = created;
                IsMediaLoaded = true;
                StatusMessage = null;
                // Live items have no finite duration (§6.5) — the transport hides the seek bar via DurationText.
                // File items pull Duration from the decoder's seekable audio (or video) source as before.
                Duration = item.IsLive
                    ? TimeSpan.Zero
                    : (created.Player.HasContainerDecoder
                        && created.Player.Decoder.Audio is ISeekableSource a
                        ? a.Duration
                        : TimeSpan.Zero);

                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    created.ApplyFallbackImage(FallbackImagePath);
                created.SetHoldFallback(HoldFallbackVideo);
                // Size every binding's matrix to the source channel count and the configured output-channel
                // count of that output line, then push matrices into the session so cell
                // routes are installed before the first chunk runs. Live items (§6.5) use the negotiated
                // live-source channel count surfaced via SourceAudioFormat.
                var srcCh = SourceChannelCountOrFallback(created);
                foreach (var binding in Outputs)
                {
                    binding.Matrix.Resize(srcCh, OutputChannelCountOrZero(binding.Line));
                    // First open after a config-load installs the saved mix mode preset; on a freshly-created
                    // binding the matrix already sits at the identity defaults from Resize.
                }
                RebuildAudioMatrixRows();
                ApplyAllOutputMatricesToSession();
                ApplyAllOutputGainsToSession();

                if (HoldFallbackVideo)
                {
                    try { created.PumpHoldFrames(created.Player.PlayClock.CurrentPosition); }
                    catch { /* best effort */ }
                }

                EnsureLoopTimerStarted();
                return HoldFallbackVideo;
            });

            if (created is null) return;

            if (resumeAfterOpen)
            {
                var s = created;
                var hf = holdFbAfterOpen;
                var ok = await RunBoundedAsync(() =>
                {
                    s.PrepareOutputsBeforePlay(hf);
                    s.PrepareLiveTransportBeforePlay();
                    s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
                }, TimeSpan.FromSeconds(8));

                if (!ok)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPlaying = false;
                        StatusMessage = "Playback failed to resume after loading.";
                    });
            }

            if (holdFbAfterOpen)
                await Dispatcher.UIThread.InvokeAsync(StartHoldPumpTimer);
        }).ConfigureAwait(false);
    }

    private void EnsureLoopTimerStarted()
    {
        if (_loopTimer is not null)
            return;
        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _loopTimer.Tick += OnLoopTimerTick;
        _loopTimer.Start();
    }

    private void StartHoldPumpTimer()
    {
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
            if (Duration > TimeSpan.Zero)
                SeekSliderValue = e.Ticks * 1000.0 / Duration.Ticks;
        }, DispatcherPriority.Normal);

    private void OnLoopTimerTick(object? sender, EventArgs e) =>
        _ = ProcessLoopTimerTickAsync();

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
            if (!TryGetNextPlaylistItem(out var nextItem)) return false;
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
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
            }, TimeSpan.FromSeconds(6));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok) return;
                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        // Phase 1C — auto-route: if the user clicks Play with no outputs selected, pick a sensible
        // default (first compatible output) so playback isn't silent on first run.
        await TryAutoRouteAsync().ConfigureAwait(false);

        // Auto-load: if nothing's loaded yet but the user has selected a playlist row, load + play in one click.
        if (_session is null && SelectedPlaylistItem is { } selected)
        {
            // File items must point at a readable path; live items short-circuit inside OpenOrReloadAsync
            // until task #4 (live wiring) lands.
            if (selected is FilePlaylistItem f && !File.Exists(f.Path))
            {
                StatusMessage = $"File missing: {f.Path}";
                return;
            }
            _activePlaybackTab = SelectedPlaylistTab;
            await PrepareCurrentItemAsync(selected).ConfigureAwait(false);
            IsPlaying = true; // signals OpenOrReloadAsync to resume after open
            await OpenOrReloadAsync().ConfigureAwait(false);
            return;
        }

        await StartPlaybackAsync().ConfigureAwait(false);
    }

    /// <summary>One-button transport: pause if playing, play otherwise.</summary>
    [RelayCommand(CanExecute = nameof(CanTogglePlayPause))]
    private Task TogglePlayPauseAsync() =>
        IsPlaying && _session is not null ? PauseAsync() : PlayAsync();

    private bool CanTogglePlayPause() =>
        (_session is not null && IsMediaLoaded) ||
        (_session is null && SelectedPlaylistItem is not null);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextTrackAsync()
    {
        if (!TryGetNextPlaylistItem(out var next)) return;
        await PlayPlaylistItemAsync(next).ConfigureAwait(false);
    }

    private bool CanGoNext() => TryGetNextPlaylistItem(out _);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousTrackAsync()
    {
        if (!TryGetPreviousPlaylistItem(out var prev)) return;
        await PlayPlaylistItemAsync(prev).ConfigureAwait(false);
    }

    private bool CanGoPrevious() => TryGetPreviousPlaylistItem(out _);

    private bool TryGetPreviousPlaylistItem([NotNullWhen(true)] out PlaylistItem? prevItem)
    {
        prevItem = null;
        var items = ActivePlaybackItems();
        if (items.Count == 0 || _currentPlaylistItem is null)
            return false;
        var idx = items.IndexOf(_currentPlaylistItem);
        if (idx <= 0) return false;
        prevItem = items[idx - 1];
        return true;
    }

    [RelayCommand]
    private Task AddPortAudioOutputAsync() => AddOutputAndSelectAsync(() => _outputs.AddPortAudioCommand.ExecuteAsync(null));

    [RelayCommand]
    private Task AddLocalVideoOutputAsync() => AddOutputAndSelectAsync(() => _outputs.AddLocalVideoCommand.ExecuteAsync(null));

    [RelayCommand]
    private Task AddNDIOutputAsync() => AddOutputAndSelectAsync(() => _outputs.AddNDICommand.ExecuteAsync(null));

    private async Task AddOutputAndSelectAsync(Func<Task> addOutputAsync)
    {
        var before = _outputs.Outputs.Select(o => o.Definition.Id).ToHashSet();
        await addOutputAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SyncOutputsCollection();
            foreach (var binding in Outputs)
            {
                if (!before.Contains(binding.Line.Definition.Id))
                    binding.IsSelected = true;
            }
        });
    }

    /// <summary>
    /// First-Play helper: if the user hasn't ticked any outputs yet, pick the first compatible one and
    /// surface a one-line banner so they know what we chose.
    /// </summary>
    private async Task TryAutoRouteAsync()
    {
        var anySelected = await Dispatcher.UIThread.InvokeAsync(() => Outputs.Any(b => b.IsSelected));
        if (anySelected || Outputs.Count == 0)
            return;

        // Probe the source so we know whether to prefer a video or audio output. When nothing is loaded
        // yet (Play with a playlist row), assume audio+video so we'll happily route to either.
        var preferVideo = true;
        var preferAudio = true;
        // File items probe via the decoder; live items don't have a file path — fall back to
        // "prefer both" so we still pick a compatible output.
        var path = (SelectedPlaylistItem as FilePlaylistItem)?.Path
                   ?? (_currentPlaylistItem as FilePlaylistItem)?.Path
                   ?? MediaFilePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            // Best-effort probe — failures fall back to "pick anything compatible".
            try
            {
                var dec = await Task.Run(() => S.Media.FFmpeg.MediaContainerDecoder.Open(path))
                    .ConfigureAwait(false);
                try
                {
                    preferVideo = dec.HasVideo;
                    preferAudio = dec.HasAudio;
                }
                finally { dec.Dispose(); }
            }
            catch { /* unreadable file — caller will surface the open error */ }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var picked = PickAutoRoute(preferVideo, preferAudio);
            if (picked is null) return;
            picked.IsSelected = true;
            StatusMessage = $"Auto-routed to {picked.Line.KindLabel} — {picked.Line.Definition.DisplayName}. " +
                            "Change in Routing below.";
        });
    }

    private PlayerOutputBinding? PickAutoRoute(bool preferVideo, bool preferAudio)
    {
        // First compatible match wins. Video outputs cover video; PortAudio covers audio; NDI covers both.
        foreach (var b in Outputs)
        {
            if (b.Line.Definition is Models.NDIOutputDefinition) return b;
        }
        if (preferVideo)
        {
            foreach (var b in Outputs)
                if (b.Line.Definition is Models.LocalVideoOutputDefinition) return b;
        }
        if (preferAudio)
        {
            foreach (var b in Outputs)
                if (b.Line.Definition is Models.PortAudioOutputDefinition) return b;
        }
        return Outputs.FirstOrDefault();
    }

    private async Task StartPlaybackAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var (s, holdFb) = await Dispatcher.UIThread.InvokeAsync(() => (_session, HoldFallbackVideo));
            if (s is null) return;

            var ok = await RunBoundedAsync(() =>
            {
                // Play from a non-playing state — NDI receivers may have drained their buffers since the last
                // Pause/Stop, so push silence ahead of the first real samples.
                s.PrepareOutputsBeforePlay(holdFb);
                // Live sessions only: discard any audio/video the receivers buffered between connect and
                // Play so we don't start the router on stale FIFO samples (audio) or stale PTS-counter
                // frames (video). No-op for file sessions. Must run before Router.Play so VideoPlayer.Play
                // doesn't kick off DecodeLoop on stale-PTS frames.
                s.PrepareLiveTransportBeforePlay();
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
            }, TimeSpan.FromSeconds(6));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok) return;
                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private bool CanPlay() =>
        (_session is not null && IsMediaLoaded) ||
        (_session is null && SelectedPlaylistItem is not null);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task PauseAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var s = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!HoldFallbackVideo) StopHoldPumpTimer();
                return _session;
            });
            if (s is null) return;

            await RunBoundedCancelableAsync(s.Router.PauseSkippingSharedMuxFlush,
                innerTimeout: TimeSpan.FromSeconds(1.5),
                outerTimeout: TimeSpan.FromSeconds(2.5));

            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var (snap, doPump) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopHoldPumpTimer();
                _loopTimer?.Stop();
                _loopTimer = null;
                IsPlaying = false;
                // Phase C.5 — Stop also cancels the retry loop. A waiting NDI input is "stopped" the
                // moment the user clicks Stop; Play would re-arm both the load and the retry.
                ExitWaitingForSource();
                StatusMessage = null;
                return (_session, HoldFallbackVideo);
            });
            if (snap is null) return;

            // One coordinated pause+seek so the freeze never exceeds the outer cap. Three nested
            // Task.Run/.WaitAsync blocks (the previous shape) could stack to ~11s on slow codecs.
            await RunBoundedCancelableAsync(ct =>
                {
                    if (snap.IsLive)
                        snap.Router.PauseSkippingSharedMuxFlush(ct);
                    else
                        snap.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, ct);
                    if (doPump)
                    {
                        try { snap.PumpHoldFrames(TimeSpan.Zero); }
                        catch { /* best effort */ }
                    }
                },
                innerTimeout: TimeSpan.FromSeconds(2),
                outerTimeout: TimeSpan.FromSeconds(3));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_session != snap) return;
                CurrentPosition = TimeSpan.Zero;
                SeekSliderValue = 0;
                if (_cuePlaybackActive && snap.IsLive)
                {
                    _cuePlaybackActive = false;
                    NaturalPlaybackEnded?.Invoke(this, EventArgs.Empty);
                }
            });
        }).ConfigureAwait(false);
    }

    /// <summary>Raises <see cref="NaturalPlaybackEnded"/> for cue AutoFollow (file natural end, live stop, or live disconnect).</summary>
    private async Task NotifyCuePlaybackNaturallyEndedAsync(
        CueEndBehavior? endBehavior = null,
        HaPlayPlaybackSession? session = null)
    {
        var (cueActive, behavior, liveSession) = await Dispatcher.UIThread.InvokeAsync(() =>
            (_cuePlaybackActive, endBehavior ?? _activeCueEndBehavior, session ?? _session));
        if (!cueActive || liveSession is null)
            return;

        if (behavior == CueEndBehavior.FreezeLastFrame)
        {
            await RunBoundedCancelableAsync(liveSession.Router.PauseSkippingSharedMuxFlush,
                innerTimeout: TimeSpan.FromSeconds(1.5),
                outerTimeout: TimeSpan.FromSeconds(2.5));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _cuePlaybackActive = false;
            IsPlaying = false;
            NaturalPlaybackEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the thread pool with a hard <paramref name="outerTimeout"/> wall. Returns
    /// true when the action completed within the budget. Swallows transport teardown noise — the caller decides
    /// what to do based on the result, never on exceptions.
    /// </summary>
    private static async Task<bool> RunBoundedAsync(Action action, TimeSpan outerTimeout)
    {
        try
        {
            await Task.Run(action).WaitAsync(outerTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Two-tier timeout: pass <paramref name="innerTimeout"/> as a CancellationToken to <paramref name="action"/> (so framework
    /// joins exit cooperatively), then enforce <paramref name="outerTimeout"/> with <see cref="Task.WaitAsync(TimeSpan)"/> as a
    /// last-resort wall. Without the outer wall, a stuck native call would freeze the UI indefinitely.
    /// </summary>
    private static async Task<bool> RunBoundedCancelableAsync(Action<CancellationToken> action, TimeSpan innerTimeout, TimeSpan outerTimeout)
    {
        try
        {
            await Task.Run(() =>
            {
                using var cts = new CancellationTokenSource(innerTimeout);
                try { action(cts.Token); }
                catch (OperationCanceledException) { /* bounded */ }
            }).WaitAsync(outerTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CanTransport() => _session is not null && IsMediaLoaded;

    /// <summary>Phase C.5 — Stop is enabled while a session is loaded OR while a live item is in the
    /// waiting-for-source state. Without the second clause, Stop can't cancel the retry loop on a
    /// manual-name NDI item whose source never came online.</summary>
    private bool CanStop() => (_session is not null && IsMediaLoaded) || IsWaitingForSource;

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task SeekToSliderAsync()
    {
        if (_session is null || Duration <= TimeSpan.Zero)
            return;
        await WithPlaybackArcAsync(async () =>
        {
            var (session, playing, holdFb, sliderValue) = await Dispatcher.UIThread.InvokeAsync(() =>
                (_session, IsPlaying, HoldFallbackVideo, SeekSliderValue));
            if (session is null) return;

            var t = TimeSpan.FromTicks((long)(sliderValue * Duration.Ticks / 1000.0));

            await RunBoundedCancelableAsync(ct =>
                {
                    session.Router.SeekCoordinatedSkippingSharedMuxFlush(t, ct);
                    if (playing)
                    {
                        // No NDI warmup on seek — silence at the seek target would be obviously wrong audio.
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.PrepareLiveTransportBeforePlay();
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    }
                },
                innerTimeout: TimeSpan.FromSeconds(3),
                outerTimeout: TimeSpan.FromSeconds(5));

            if (!playing) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private bool CanSeek() => _session is not null && IsMediaLoaded && Duration > TimeSpan.Zero;

    /// <summary>Phase C — Keyboard `,` jog backward 5 s. Routes through <see cref="SeekToSliderAsync"/>
    /// so the bounded-CT teardown timing matches a normal drag-end commit.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogBackAsync() => JogByAsync(TimeSpan.FromSeconds(-5));

    /// <summary>Phase C — Keyboard `.` jog forward 5 s.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogForwardAsync() => JogByAsync(TimeSpan.FromSeconds(5));

    private Task JogByAsync(TimeSpan delta)
    {
        if (Duration <= TimeSpan.Zero) return Task.CompletedTask;
        var target = CurrentPosition + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > Duration) target = Duration;
        SeekSliderValue = target.Ticks * 1000.0 / Duration.Ticks;
        return SeekToSliderCommand.CanExecute(null)
            ? SeekToSliderCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private Task CloseSessionAsync() => WithPlaybackArcAsync(() => CloseSessionCoreInnerAsync(false));

    private bool TryGetNextPlaylistItem([NotNullWhen(true)] out PlaylistItem? nextItem)
    {
        nextItem = null;
        var items = ActivePlaybackItems();
        if (items.Count == 0 || _currentPlaylistItem is null)
            return false;
        var idx = items.IndexOf(_currentPlaylistItem);
        if (idx < 0)
            return false;
        var n = idx + 1;
        if (n >= items.Count)
            return false;
        nextItem = items[n];
        return true;
    }

    private IList<PlaylistItem> ActivePlaybackItems() =>
        _activePlaybackTab?.Items ?? (IList<PlaylistItem>)PlaylistItems;

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            return d.MainWindow;
        return null;
    }

    private void StopIdleSlate()
    {
        _idleSlate?.Dispose();
        _idleSlate = null;
        _idleSlateSig = null;
    }

    private void SyncIdleSlate()
    {
        if (IsMediaLoaded)
        {
            StopIdleSlate();
            return;
        }

        var selected = SelectedOutputLines();
        var sig = IdleLogoSlateSession.BuildSignature(HoldFallbackVideo, FallbackImagePath, selected);
        if (_idleSlate is not null && _idleSlateSig == sig)
            return;

        StopIdleSlate();

        if (!HoldFallbackVideo || string.IsNullOrWhiteSpace(FallbackImagePath) ||
            !File.Exists(FallbackImagePath!) || selected.Count == 0)
            return;

        if (!IdleLogoSlateSession.TryStart(selected, _outputs, FallbackImagePath!, out var slate, out var err))
        {
            if (!string.IsNullOrWhiteSpace(err))
                StatusMessage = err;
            return;
        }

        _idleSlate = slate;
        _idleSlateSig = sig;
    }
}
