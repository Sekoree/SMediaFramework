using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
        // When a source is already playing through the per-player ShowSession, Play resumes it (no re-open);
        // a fresh play opens the selected item, which fires immediately.
        if (ShowSessionActive)
        {
            if (!IsPlaying)
                await ShowSessionPauseAsync(false);
            return;
        }

        // Phase 1C - auto-route: if the user clicks Play with no outputs selected, pick a sensible
        // default (first compatible output) so playback isn't silent on first run.
        await TryAutoRouteAsync();

        // Auto-load: play the selected playlist row in one click.
        if (SelectedPlaylistItem is { } selected)
        {
            if (selected is FilePlaylistItem f && !File.Exists(f.Path))
            {
                StatusMessage = $"File missing: {f.Path}";
                return;
            }
            _activePlaybackTab = SelectedPlaylistTab;
            await PrepareCurrentItemAsync(selected);
            IsPlaying = true;
            await OpenOrReloadAsync();
        }
    }

    /// <summary>One-button transport: pause if playing, play otherwise.</summary>
    [RelayCommand(CanExecute = nameof(CanTogglePlayPause))]
    private Task TogglePlayPauseAsync() =>
        IsPlaying && ShowSessionActive ? PauseAsync() : PlayAsync();

    private bool CanTogglePlayPause() =>
        !_isTransportBusy && (ShowSessionActive && IsMediaLoaded || SelectedPlaylistItem is not null);

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

    private bool CanAddNDIOutput() => IsNDIAvailable;

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
        // File items probe via the decoder; live items don't have a file path - fall back to
        // "prefer both" so we still pick a compatible output.
        var path = (SelectedPlaylistItem as FilePlaylistItem)?.Path
                   ?? (_currentPlaylistItem as FilePlaylistItem)?.Path
                   ?? MediaFilePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            // Best-effort probe - failures fall back to "pick anything compatible".
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
            catch { /* unreadable file - caller will surface the open error */ }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var picked = PickAutoRoute(preferVideo, preferAudio);
            if (picked is null) return;
            picked.IsSelected = true;
            StatusMessage = $"Auto-routed to {picked.Line.KindLabel} - {picked.Line.Definition.DisplayName}. " +
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

    private bool CanPlay() =>
        !_isTransportBusy && (ShowSessionActive && IsMediaLoaded || SelectedPlaylistItem is not null);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task PauseAsync() =>
        ShowSessionActive ? ShowSessionPauseAsync(true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        // Stop also cancels the waiting-for-source retry loop (Phase C.5): a waiting live input is
        // "stopped" the moment the user clicks Stop; Play re-arms both the load and the retry.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _loopTimer?.Stop();
            _loopTimer = null;
            ExitWaitingForSource();
            StatusMessage = null;
        });

        if (ShowSessionActive)
        {
            await ShowSessionStopAsync();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsPlaying = false;
            CurrentPosition = TimeSpan.Zero;
            SeekSliderValue = 0;
            SyncIdleSlate();
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

    private bool CanTransport() => !_isTransportBusy && ShowSessionActive;

    /// <summary>Phase C.5 - Stop is enabled while playing OR while a live item is in the waiting-for-source
    /// state. Without the second clause, Stop can't cancel the retry loop on a manual-name NDI item whose
    /// source never came online.</summary>
    private bool CanStop() => !_isTransportBusy && (IsWaitingForSource || ShowSessionActive);

    private readonly object _seekGate = new();
    private double? _pendingSeekValue;
    private bool _seekArcRunning;

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task SeekToSliderAsync()
    {
        if (!ShowSessionActive || Duration <= TimeSpan.Zero)
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
                        _seekArcRunning = false; // atomic with the emptiness check - no lost wakeup
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
        if (ShowSessionActive && Duration > TimeSpan.Zero)
            await ShowSessionSeekAsync(TimeSpan.FromTicks((long)(Duration.Ticks * sliderValue / 1000.0)));
    }

    private bool CanSeek() => ShowSessionActive && IsMediaLoaded && Duration > TimeSpan.Zero;

    /// <summary>Phase C - Keyboard `,` jog backward 5 s. Routes through <see cref="SeekToSliderAsync"/>
    /// so the bounded-CT teardown timing matches a normal drag-end commit.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogBackAsync() => JogByAsync(TimeSpan.FromSeconds(-5));

    /// <summary>Phase C - Keyboard `.` jog forward 5 s.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task JogForwardAsync() => JogByAsync(TimeSpan.FromSeconds(5));

    /// <summary>Keyboard Home - jump to the start of the track.</summary>
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

    /// <summary>Jump to a typed timecode (the deck's jump-to-position box). Accepts <c>ss</c>, <c>mm:ss</c>, or
    /// <c>hh:mm:ss</c> with an optional fractional-seconds part (<c>.mmm</c>); the value is clamped to the clip
    /// duration and committed through the same coalesced seek arc as the scrubber. A malformed value is ignored.</summary>
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task SeekToPositionTextAsync(string? text)
    {
        if (Duration <= TimeSpan.Zero || !TryParseClock(text, out var target))
            return Task.CompletedTask;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > Duration) target = Duration;
        SeekSliderValue = target.Ticks * 1000.0 / Duration.Ticks;
        return SeekToSliderCommand.CanExecute(null)
            ? SeekToSliderCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    /// <summary>Parses a timecode into a position: <c>ss[.fff]</c>, <c>mm:ss[.fff]</c>, or <c>hh:mm:ss[.fff]</c>
    /// (invariant culture). The last (seconds) component may carry a fractional part (milliseconds); every
    /// component must be a non-negative number. The inverse-ish of <see cref="FormatClock"/> (which also parses
    /// the jump box's own display format back). Returns false for anything malformed.</summary>
    internal static bool TryParseClock(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        var ci = CultureInfo.InvariantCulture;
        double h = 0, m = 0, s;
        try
        {
            s = double.Parse(parts[^1], NumberStyles.AllowDecimalPoint, ci);
            if (parts.Length >= 2) m = int.Parse(parts[^2], NumberStyles.None, ci);
            if (parts.Length == 3) h = int.Parse(parts[0], NumberStyles.None, ci);
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }

        if (h < 0 || m < 0 || s < 0 || double.IsNaN(s))
            return false;

        result = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        return true;
    }

    private const double KeyboardVolumeStepDb = 1.0;

    /// <summary>Keyboard `+` - nudge master volume up, clamped to the volume slider's range.</summary>
    [RelayCommand]
    private void VolumeUp() => MasterVolumeDb = Math.Clamp(MasterVolumeDb + KeyboardVolumeStepDb, -60.0, 12.0);

    /// <summary>Keyboard `-` - nudge master volume down.</summary>
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
    /// an exhausted repeat-all bag reshuffles an unpredictable order) - those fall back to the linear
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
                return null; // bag not built for this list yet - nothing reliable to warm
            for (var i = _shuffleBagIndex; i < _shuffleBag.Count; i++)
            {
                var candidate = _shuffleBag[i];
                if (items.Contains(candidate) && !ReferenceEquals(candidate, _currentPlaylistItem))
                    return candidate;
            }
            return null; // end of bag - repeat-all reshuffles at advance time
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
        // audio-only playback - it acquires no video lines, so the idle slate stays responsible for showing
        // the hold image on them (legacy parity: audio track + logo on the video outputs).
        if (IsMediaLoaded && !(ShowSessionActive && _playerAcquiredLines.Count == 0))
        {
            StopIdleSlate();
            return;
        }

        // This timer runs for every deck. Keep the normal HOLD-off path allocation-free: selecting routes
        // and building a signature are only useful while there is a valid slate to acquire/retry.
        if (!HoldFallbackVideo || string.IsNullOrWhiteSpace(FallbackImagePath) || !File.Exists(FallbackImagePath!))
        {
            StopIdleSlate();
            return;
        }

        var selected = SelectedOutputLines();
        var sig = IdleLogoSlateSession.BuildSignature(HoldFallbackVideo, FallbackImagePath, selected);
        if (_idleSlate is not null && _idleSlateSig == sig)
            return;

        StopIdleSlate();

        if (selected.Count == 0)
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
