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
using S.Media.Core.Diagnostics;
using S.Media.NDI;
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel
{
    private static readonly ILogger TransportTrace =
        MediaDiagnostics.CreateLogger("HaPlay.ViewModels.MediaPlayerViewModel.Transport");

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        // 8a.4 re-back: when a file is already playing through the per-player ShowSession, Play resumes it
        // (no re-open); a fresh play falls through to OpenOrReloadAsync, which diverts into the ShowSession.
        if (ShowSessionActive)
        {
            if (!IsPlaying)
                await ShowSessionPauseAsync(false);
            return;
        }

        // Stays on the dispatcher context: the code between awaits sets observable properties
        // (StatusMessage / MediaFilePath / IsPlaying) whose change notifications must be raised on
        // the UI thread. The helpers do their own Task.Run/InvokeAsync marshalling.

        // Phase 1C — auto-route: if the user clicks Play with no outputs selected, pick a sensible
        // default (first compatible output) so playback isn't silent on first run.
        await TryAutoRouteAsync();

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
            await PrepareCurrentItemAsync(selected);
            IsPlaying = true; // signals OpenOrReloadAsync to resume after open
            await OpenOrReloadAsync();
            return;
        }

        await StartPlaybackAsync();
    }

    /// <summary>One-button transport: pause if playing, play otherwise.</summary>
    [RelayCommand(CanExecute = nameof(CanTogglePlayPause))]
    private Task TogglePlayPauseAsync() =>
        IsPlaying && (_session is not null || ShowSessionActive) ? PauseAsync() : PlayAsync();

    private bool CanTogglePlayPause() =>
        !_isTransportBusy &&
        ((_session is not null && IsMediaLoaded) ||
         (_session is null && SelectedPlaylistItem is not null));

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextTrackAsync()
    {
        if (!TryGetNextPlaylistItem(out var next)) return;
        if (!SDebug.ChangeTrace.IsActive)
            SDebug.ChangeTrace.Begin("next track");
        await PlayPlaylistItemAsync(next).ConfigureAwait(false);
    }

    private bool CanGoNext() => TryGetNextPlaylistItem(out _);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousTrackAsync()
    {
        if (!TryGetPreviousPlaylistItem(out var prev)) return;
        if (!SDebug.ChangeTrace.IsActive)
            SDebug.ChangeTrace.Begin("previous track");
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

    [RelayCommand(CanExecute = nameof(CanAddNDIOutput))]
    private Task AddNDIOutputAsync() => AddOutputAndSelectAsync(() => _outputs.AddNDICommand.ExecuteAsync(null));

    private bool CanAddNDIOutput() => IsNdiAvailable;

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
                var dec = await Task.Run(() => S.Media.Decode.FFmpeg.MediaContainerDecoder.Open(path))
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
        PlayerOutputBinding? FirstAvailable(Func<PlayerOutputBinding, bool> predicate) =>
            Outputs.FirstOrDefault(b => predicate(b) && !WouldConflictWithAnotherPlayer(b));

        // First compatible match wins. Video outputs cover video; PortAudio covers audio; NDI covers both.
        var ndi = FirstAvailable(b => b.Line.Definition is Models.NDIOutputDefinition);
        if (ndi is not null) return ndi;
        if (preferVideo)
        {
            var localVideo = FirstAvailable(b => b.Line.Definition is Models.LocalVideoOutputDefinition);
            if (localVideo is not null) return localVideo;
        }
        if (preferAudio)
        {
            var portAudio = FirstAvailable(b => b.Line.Definition is Models.PortAudioOutputDefinition);
            if (portAudio is not null) return portAudio;
        }
        return Outputs.FirstOrDefault(b => !WouldConflictWithAnotherPlayer(b));
    }

    private async Task StartPlaybackAsync()
    {
        var ownedTrace = !SDebug.ChangeTrace.IsActive;
        if (ownedTrace)
            SDebug.ChangeTrace.Begin("StartPlayback");
        SDebug.ChangeTrace.Step("StartPlaybackAsync entered");

        await WithPlaybackArcAsync(async () =>
        {
            var (s, holdFb) = await Dispatcher.UIThread.InvokeAsync(() => (_session, HoldFallbackVideo));
            if (s is null)
            {
                SDebug.ChangeTrace.Step("StartPlayback: no session");
                return;
            }

            SDebug.ChangeTrace.Step("StartPlayback: session snapshot (UI)");
            var ok = await RunBoundedAsync(() =>
            {
                SDebug.ChangeTrace.Step("StartPlayback: Play begin (thread pool)");
                s.PrepareOutputsBeforePlay(holdFb);
                SDebug.ChangeTrace.Step("StartPlayback: PrepareOutputsBeforePlay");
                s.PrepareLiveTransportBeforePlay();
                SDebug.ChangeTrace.Step("StartPlayback: PrepareLiveTransportBeforePlay");
                s.ResetAllUnderrunBaselines();
                SDebug.ChangeTrace.Step("StartPlayback: ResetAllUnderrunBaselines");
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
                SDebug.ChangeTrace.Step("StartPlayback: Router.Play");
            }, PlayWallTimeout, "StartPlayback Play");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok)
                {
                    SDebug.ChangeTrace.Step("StartPlayback: Play TIMED OUT");
                    StatusMessage = "Playback failed to start. See debug log for details.";
                    return;
                }

                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
                SDebug.ChangeTrace.Step("StartPlayback: UI flags updated");
            });
        }).ConfigureAwait(false);

        if (ownedTrace)
            SDebug.ChangeTrace.End("StartPlaybackAsync");
    }

    private bool CanPlay() =>
        !_isTransportBusy &&
        ((_session is not null && IsMediaLoaded) ||
         (_session is null && SelectedPlaylistItem is not null));

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task PauseAsync()
    {
        if (ShowSessionActive)
        {
            await ShowSessionPauseAsync(true);
            return;
        }
        SDebug.ChangeTrace.Begin("Pause");
        await WithPlaybackArcAsync(async () =>
        {
            var s = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!HoldFallbackVideo) StopHoldPumpTimer();
                return _session;
            });
            if (s is null)
            {
                SDebug.ChangeTrace.End("Pause (no session)");
                return;
            }

            SDebug.ChangeTrace.Step("Pause: UI flags updated (optimistic)");
            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);

            SDebug.ChangeTrace.Step("Pause: Router.Pause begin");
            // Do not pass a cancellable token into video pause — cancelling mid-join marks the player
            // terminal while the UI still shows a loaded session. Bound only the outer wall.
            await RunBoundedAsync(
                () => s.Router.PauseSkippingSharedMuxFlush(CancellationToken.None),
                TimeSpan.FromSeconds(5),
                "Pause transport");
            SDebug.ChangeTrace.Step("Pause: Router.Pause done");

            SDebug.ChangeTrace.End("Pause");
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (ShowSessionActive)
        {
            await ShowSessionStopAsync();
            return;
        }
        SDebug.ChangeTrace.Begin("Stop");
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
            if (snap is null)
            {
                SDebug.ChangeTrace.End("Stop (no session)");
                return;
            }

            SDebug.ChangeTrace.Step($"Stop: transport begin (live={snap.IsLive})");
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
            SDebug.ChangeTrace.Step("Stop: transport done");

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
            SDebug.ChangeTrace.End("Stop");
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
    /// true when the action completed within the budget. Logs transport teardown noise and lets the caller decide
    /// what to do based on the result, never on exceptions.
    /// </summary>
    private static async Task<bool> RunBoundedAsync(
        Action action,
        TimeSpan outerTimeout,
        string operationName = "transport operation")
    {
        var task = Task.Run(action);
        try
        {
            await task.WaitAsync(outerTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException ex)
        {
            TransportTrace.LogWarning(ex,
                "{Operation}: did not complete within {TimeoutMs:0}ms; leaving background work to finish",
                operationName, outerTimeout.TotalMilliseconds);
            _ = task.ContinueWith(t =>
                TransportTrace.LogWarning(t.Exception,
                    "{Operation}: background work faulted after UI timeout",
                    operationName),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception ex)
        {
            TransportTrace.LogWarning(ex,
                "{Operation}: failed before {TimeoutMs:0}ms timeout",
                operationName, outerTimeout.TotalMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Runs required teardown work off the UI thread. It warns at <paramref name="slowWarningTimeout"/>,
    /// but does not let the caller continue until the action has completed.
    /// </summary>
    private static async Task RunRequiredTransportAsync(
        Action action,
        TimeSpan slowWarningTimeout,
        string operationName)
    {
        var task = Task.Run(action);
        try
        {
            await task.WaitAsync(slowWarningTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            TransportTrace.LogWarning(ex,
                "{Operation}: still running after {TimeoutMs:0}ms; waiting for teardown before continuing",
                operationName, slowWarningTimeout.TotalMilliseconds);

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                TransportTrace.LogWarning(inner,
                    "{Operation}: failed after slow teardown wait",
                    operationName);
            }
        }
        catch (Exception ex)
        {
            TransportTrace.LogWarning(ex,
                "{Operation}: failed before {TimeoutMs:0}ms timeout",
                operationName, slowWarningTimeout.TotalMilliseconds);
        }
    }

    private void HookVideoFaultRecovery(HaPlayPlaybackSession session) =>
        session.VideoDecodeFaulted += OnSessionVideoDecodeFaulted;

    private void UnhookVideoFaultRecovery(HaPlayPlaybackSession session) =>
        session.VideoDecodeFaulted -= OnSessionVideoDecodeFaulted;

    /// <summary>
    /// Fires on the decode thread when a file session's video decode loop faults. <paramref name="switchedToSoftware"/>
    /// is true only on the fault that flipped the process from hardware to software decode; on that one fault we
    /// reload the current item once so it comes back via the (now software) decode path. Cue-driven playback is
    /// skipped — the cue engine owns that lifecycle. The single-trip flag prevents a reload loop if software also fails.
    /// </summary>
    private void OnSessionVideoDecodeFaulted(HaPlayPlaybackSession faulted, bool switchedToSoftware)
    {
        if (!switchedToSoftware)
            return;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_session, faulted) || _cuePlaybackActive)
                return;
            TransportTrace.LogWarning(
                "Video decode faulted; reloading '{Item}' with software decode.",
                SelectedPlaylistItem?.DisplayName ?? MediaFilePath ?? "current item");
            await OpenOrReloadAsync();
        });
    }

    /// <summary>
    /// File seek + optional resume. Uses <see cref="CancellationToken.None"/> for the demux prime so a short
    /// UI cancel cannot abort H.264 GOP catch-up while audio is already at the target.
    /// </summary>
    private static async Task RunFileSeekTransportAsync(
        HaPlayPlaybackSession session,
        TimeSpan target,
        bool resumePlayback,
        bool holdFallbackVideo)
    {
        await Task.Run(() =>
        {
            session.Router.SeekCoordinatedSkippingSharedMuxFlush(target, CancellationToken.None);
            if (!resumePlayback)
                return;
            session.PrepareOutputsBeforePlay(holdFallbackVideo);
            session.PrepareLiveTransportBeforePlay();
            session.Player.PrewarmVideoAfterSeek();
            session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
        }).WaitAsync(FileSeekWallTimeout).ConfigureAwait(false);
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

    // Pause is available under either path: the legacy engine session OR the ShowSession deck (the flipped
    // default, where _session is null). PauseAsync dispatches to the right one via ShowSessionActive.
    private bool CanTransport() => !_isTransportBusy && ((_session is not null && IsMediaLoaded) || ShowSessionActive);

    /// <summary>Phase C.5 — Stop is enabled while a session is loaded OR while a live item is in the
    /// waiting-for-source state. Without the second clause, Stop can't cancel the retry loop on a
    /// manual-name NDI item whose source never came online.</summary>
    private bool CanStop() => !_isTransportBusy && ((_session is not null && IsMediaLoaded) || IsWaitingForSource || ShowSessionActive);

    private readonly object _seekGate = new();
    private double? _pendingSeekValue;
    private bool _seekArcRunning;

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task SeekToSliderAsync()
    {
        // Seek runs under either path: the legacy engine session OR the ShowSession deck (flipped default, where
        // _session is null). SeekToTargetAsync dispatches to the right one, so bail only when NEITHER is active.
        if ((_session is null && !ShowSessionActive) || Duration <= TimeSpan.Zero)
            return;

        // Coalesce rapid scrub commits (each pointer release / arrow key-up calls this) to
        // latest-wins. A seek runs a 3–5 s-bounded teardown+seek+play arc serialized behind
        // _playbackArc, so without conflation a burst of releases queues N full arcs and the
        // playhead lags. Record the newest target; if an arc is already running it re-reads the
        // pending target when it finishes and drops the intermediate ones.
        lock (_seekGate)
        {
            _pendingSeekValue = SeekSliderValue;
            if (_seekArcRunning)
                return;
            _seekArcRunning = true;
        }

        try
        {
            while (true)
            {
                double target;
                lock (_seekGate)
                {
                    if (_pendingSeekValue is not { } pending)
                    {
                        _seekArcRunning = false; // atomic with the emptiness check — no lost wakeup
                        return;
                    }
                    target = pending;
                    _pendingSeekValue = null;
                }

                await SeekToTargetAsync(target).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_seekGate) _seekArcRunning = false;
            throw;
        }
    }

    private async Task SeekToTargetAsync(double sliderValue)
    {
        if (ShowSessionActive)
        {
            if (Duration > TimeSpan.Zero)
                await ShowSessionSeekAsync(TimeSpan.FromTicks((long)(Duration.Ticks * sliderValue / 1000.0)));
            return;
        }
        await WithPlaybackArcAsync(async () =>
        {
            var (session, playing, holdFb) = await Dispatcher.UIThread.InvokeAsync(() =>
                (_session, IsPlaying, HoldFallbackVideo));
            if (session is null) return;

            var t = TimeSpan.FromTicks((long)(sliderValue * Duration.Ticks / 1000.0));

            await RunFileSeekTransportAsync(session, t, playing, holdFb).ConfigureAwait(false);

            if (!playing) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    // Seek is available under either playback path: the legacy engine session OR the ShowSession deck path
    // (the flipped default, where _session is null). SeekToTargetAsync dispatches to the right one.
    private bool CanSeek() => (_session is not null || ShowSessionActive) && IsMediaLoaded && Duration > TimeSpan.Zero;

    /// <summary>Phase C — Keyboard `,` jog backward 5 s. Routes through <see cref="SeekToSliderAsync"/>
    /// so the bounded-CT teardown timing matches a normal drag-end commit.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogBackAsync() => JogByAsync(TimeSpan.FromSeconds(-5));

    /// <summary>Phase C — Keyboard `.` jog forward 5 s.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogForwardAsync() => JogByAsync(TimeSpan.FromSeconds(5));

    /// <summary>Keyboard Home — jump to the start of the track.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task SeekToStartAsync()
    {
        if (Duration <= TimeSpan.Zero) return Task.CompletedTask;
        SeekSliderValue = 0;
        return SeekToSliderCommand.CanExecute(null)
            ? SeekToSliderCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

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

    private const double KeyboardVolumeStepDb = 1.0;

    /// <summary>Keyboard `+` — nudge master volume up, clamped to the volume slider's range.</summary>
    [RelayCommand]
    private void VolumeUp() => MasterVolumeDb = Math.Clamp(MasterVolumeDb + KeyboardVolumeStepDb, -60.0, 12.0);

    /// <summary>Keyboard `-` — nudge master volume down.</summary>
    [RelayCommand]
    private void VolumeDown() => MasterVolumeDb = Math.Clamp(MasterVolumeDb - KeyboardVolumeStepDb, -60.0, 12.0);

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

    // --- Auto-advance ordering (shuffle bag + repeat-all) ----------------------------------------
    // Shuffle/RepeatAll affect only *unattended* auto-advance; the manual Next/Previous buttons stay
    // linear so explicit navigation is predictable.

    private readonly List<PlaylistItem> _shuffleBag = new();
    private int _shuffleBagIndex;

    private void InvalidateShuffleBag()
    {
        _shuffleBag.Clear();
        _shuffleBagIndex = 0;
    }

    /// <summary>Next item for natural-end auto-advance, honoring the playing tab's Shuffle/RepeatAll.
    /// Returns false to stop at the end of the list.</summary>
    private bool TryGetAutoAdvanceItem([NotNullWhen(true)] out PlaylistItem? next)
    {
        next = null;
        var items = ActivePlaybackItems();
        if (items.Count == 0 || _currentPlaylistItem is null)
            return false;

        var shuffle = _activePlaybackTab?.Shuffle ?? ShufflePlaylist;
        var repeatAll = _activePlaybackTab?.RepeatAll ?? RepeatAllPlaylist;

        if (shuffle && items.Count > 1)
            return TryGetShuffleNext(items, repeatAll, out next);

        var idx = items.IndexOf(_currentPlaylistItem);
        if (idx < 0)
            return false;
        var n = idx + 1;
        if (n >= items.Count)
        {
            if (!repeatAll)
                return false;
            n = 0; // repeat-all wraps to the top
        }
        next = items[n];
        return true;
    }

    /// <summary>Peeks the item natural-end auto-advance will pick next, *without* consuming shuffle-bag
    /// state, so the warm-decoder pre-open targets the real next track in shuffle mode too. Returns null
    /// when the next track isn't decided yet (the shuffle bag is built lazily on the first advance, and
    /// an exhausted repeat-all bag reshuffles an unpredictable order) — those fall back to the linear
    /// neighbours warmed alongside.</summary>
    private PlaylistItem? PeekAutoAdvanceNext(IList<PlaylistItem> items)
    {
        if (items.Count == 0 || _currentPlaylistItem is null)
            return null;

        var shuffle = _activePlaybackTab?.Shuffle ?? ShufflePlaylist;
        var repeatAll = _activePlaybackTab?.RepeatAll ?? RepeatAllPlaylist;

        if (shuffle && items.Count > 1)
        {
            if (!ShuffleBagMatches(items))
                return null; // bag not built for this list yet — nothing reliable to warm
            for (var i = _shuffleBagIndex; i < _shuffleBag.Count; i++)
            {
                var candidate = _shuffleBag[i];
                if (items.Contains(candidate) && !ReferenceEquals(candidate, _currentPlaylistItem))
                    return candidate;
            }
            return null; // end of bag — repeat-all reshuffles at advance time
        }

        var idx = items.IndexOf(_currentPlaylistItem);
        if (idx < 0)
            return null;
        var n = idx + 1;
        if (n >= items.Count)
            return repeatAll ? items[0] : null;
        return items[n];
    }

    private bool TryGetShuffleNext(IList<PlaylistItem> items, bool repeatAll, [NotNullWhen(true)] out PlaylistItem? next)
    {
        next = null;
        if (!ShuffleBagMatches(items))
            RebuildShuffleBag(items, _currentPlaylistItem);

        // At most two passes: drain the current bag, then (if repeat-all) one reshuffled bag.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            while (_shuffleBagIndex < _shuffleBag.Count)
            {
                var candidate = _shuffleBag[_shuffleBagIndex++];
                if (items.Contains(candidate) && !ReferenceEquals(candidate, _currentPlaylistItem))
                {
                    next = candidate;
                    return true;
                }
            }
            if (!repeatAll)
                return false;
            RebuildShuffleBag(items, _currentPlaylistItem);
        }
        return false;
    }

    private void RebuildShuffleBag(IList<PlaylistItem> items, PlaylistItem? current)
    {
        _shuffleBag.Clear();
        _shuffleBag.AddRange(items);
        for (var i = _shuffleBag.Count - 1; i > 0; i--) // Fisher–Yates
        {
            var j = Random.Shared.Next(i + 1);
            (_shuffleBag[i], _shuffleBag[j]) = (_shuffleBag[j], _shuffleBag[i]);
        }
        // Don't replay the current track first if it shuffled to the front.
        _shuffleBagIndex = current is not null && _shuffleBag.Count > 0 && ReferenceEquals(_shuffleBag[0], current)
            ? 1
            : 0;
    }

    private bool ShuffleBagMatches(IList<PlaylistItem> items)
    {
        if (_shuffleBag.Count != items.Count)
            return false;
        foreach (var it in items)
            if (!_shuffleBag.Contains(it))
                return false;
        return true;
    }

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
        // Loaded media normally owns the outputs: the engine path holds its own LogoFallback wrappers and the
        // ShowSession deck covers its composition with the hold top-layer. The EXCEPTION is ShowSession
        // audio-only playback — it acquires no video lines, so the idle slate stays responsible for showing
        // the hold image on them (legacy parity: audio track + logo on the video outputs).
        if (IsMediaLoaded && !(ShowSessionActive && _playerAcquiredLines.Count == 0))
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
