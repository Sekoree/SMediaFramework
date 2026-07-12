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
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels;

internal sealed record VideoOutputRouteConflict(
    MediaPlayerViewModel TargetPlayer,
    OutputLineViewModel OutputLine,
    IReadOnlyList<MediaPlayerViewModel> ExistingPlayers);

public partial class MediaPlayerViewModel : ViewModelBase
{
    public bool IsNDIAvailable => RuntimeModules.IsNDIAvailable;

    private readonly OutputManagementViewModel _outputs;
    private readonly Func<MediaPlayerViewModel, Task>? _requestRemove;
    private IDisposable? _sleepInhibitLease;
    private int _disposeStarted;

    /// <summary>
    /// Machine-wide preference (saved in <see cref="AppSettings"/>). When on, live NDI keeps native UYVY
    /// into local video outputs; when off, frames are converted to BGRA32 before display (default).
    /// </summary>
    [ObservableProperty]
    private bool _preferLiveUyvyPassthrough;

    private DispatcherTimer? _loopTimer;
    private IdleLogoSlateSession? _idleSlate;
    private string? _idleSlateSig;
    private readonly DispatcherTimer _idleSlateSyncTimer;
    private PlaylistTabViewModel? _activePlaybackTab;
    private int _suppressVideoRouteConflictPrompt;

    internal Func<VideoOutputRouteConflict, Task<bool>> VideoOutputRouteConflictPrompt { get; set; } =
        DefaultVideoOutputRouteConflictPromptAsync;

    private bool _syncingPlaylistTabState;
    private readonly ObservableCollection<PlaylistItem> _emptyPlaylistItems = new();

    /// <summary>Phase C.5 (§6.8) - the playlist entry currently loaded into the session. Tracks the
    /// active item across <see cref="PlaylistItem"/> kinds so navigation (next/prev/end-of-track
    /// auto-advance) works for both files and live inputs.</summary>
    private PlaylistItem? _currentPlaylistItem;

    /// <summary>The playlist item currently loaded in the deck (playing or paused), or null when idle.
    /// Drives the "now playing" row highlight and the playing-Set tab marker. Distinct from
    /// <see cref="_currentPlaylistItem"/> (which the transport keeps for next/prev navigation even while
    /// stopped) only in lifetime: this one clears the moment the deck returns to idle.</summary>
    [ObservableProperty]
    private PlaylistItem? _currentPlayingItem;

    partial void OnCurrentPlayingItemChanged(PlaylistItem? value)
    {
        _ = value;
        RefreshPlayingTabIndicators();
    }

    /// <summary>Marks the Set the player is playing from (<see cref="PlaylistTabViewModel.IsPlaying"/>).
    /// Only the active-playback tab is flagged, and only while an item is actually loaded - so the marker
    /// shows on the playing Set even when the user has switched to view a different Set.</summary>
    private void RefreshPlayingTabIndicators()
    {
        var playingTab = CurrentPlayingItem is not null ? _activePlaybackTab : null;
        foreach (var tab in PlaylistTabs)
            tab.IsPlaying = ReferenceEquals(tab, playingTab);
    }

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
    /// leaving audio running with a "Play" button - the user then has to wait out the lock and retry.
    /// </summary>
    private static readonly TimeSpan PlayWallTimeout = TimeSpan.FromSeconds(11);
    private volatile bool _isTransportBusy;

    /// <summary>Phase C.5 (§6.9) - switch into the "waiting for source" state. Surfaces a status banner,
    /// stamps the next retry deadline, and ensures the loop timer is running so the deadline ticks.</summary>
    private void EnterWaitingForSource(PlaylistItem item, string reason)
    {
        _waitingItem = item;
        IsWaitingForSource = true;
        LoadState = PlayerLoadState.WaitingForSource;
        var retrySec = GetRetrySeconds(item);
        if (retrySec > 0)
        {
            _nextRetryAt = DateTime.UtcNow.AddSeconds(retrySec);
            WaitingForSourceMessage = $"WAITING: {item.DisplayName} - {reason} (retry in {retrySec}s).";
            EnsureLoopTimerStarted();
        }
        else
        {
            WaitingForSourceMessage = $"WAITING: {item.DisplayName} - {reason}.";
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

    /// <summary>Phase C.5 - per-kind retry interval. Files never retry (0). NDI inputs use their saved
    /// <see cref="NDIInputPlaylistItem.RetrySeconds"/>. PortAudio inputs retry on a fixed 2 s cadence -
    /// device disappearance is rare and PortAudio doesn't have a discovery handshake to wait on.</summary>
    private static int GetRetrySeconds(PlaylistItem item) => item switch
    {
        NDIInputPlaylistItem ndi => ndi.RetrySeconds,
        PortAudioInputPlaylistItem => 2,
        _ => 0,
    };

    /// <summary>The matrix grid's input (source) channel count: the operator's explicit override when set,
    /// else the last playing clip's channel count (pushed from the ShowSession transport snapshot via
    /// <see cref="SetAudioMatrixSourceChannels"/>), clamped to the grid's supported range.</summary>
    private int MatrixInputChannelCount => Math.Clamp(AudioMatrixSourceChannels, 1, 64);

    private void ResizeSelectedAudioMatrices()
    {
        var inputChannels = MatrixInputChannelCount;
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
            ResizeSelectedAudioMatrices();
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
        return line.Definition switch
        {
            PortAudioOutputDefinition pa => Math.Max(1, pa.ChannelCount),
            NDIOutputDefinition { StreamMode: NDIOutputStreamMode.VideoOnly } => 0,
            NDIOutputDefinition nd => Math.Max(1, nd.AudioChannelCount),
            // Encode lines expose the combined multi-track layout (concatenated per-track channels),
            // so the deck matrix can route source channels onto specific tracks.
            FileOutputDefinition f => EncodeCombinedChannelsOrZero(f.EffectiveEncode),
            LiveStreamOutputDefinition s => EncodeCombinedChannelsOrZero(s.EffectiveEncode),
            _ => 0,
        };

        static int EncodeCombinedChannelsOrZero(EncodeSettingsDefinition encode) =>
            encode.OutputMode == "VideoOnly" ? 0 : encode.AudioLegs.Sum(l => l.Channels > 0 ? l.Channels : 2);
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
        // Phase B (§3.4) - also resync on definition changes (Edit) so clone-of transitions update
        // the routing checkbox list. CollectionChanged alone misses Edit-driven topology changes.
        _outputs.RoutingTopologyChanged += OnRoutingTopologyChanged;
        _outputs.OutputNamingChanged += OnOutputNamingChanged;
        // Phase B follow-up - unwire from the active session BEFORE the runtime is disposed (§4.3.3).
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

    /// <summary>Per-deck tint (null = none) - a subtle color-code so you can tell decks apart. The view washes
    /// its background with <see cref="TintBrush"/> and offers the swatch picker. Persisted in the player config.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TintBrush))]
    [NotifyPropertyChangedFor(nameof(TintAccentBrush))]
    [NotifyPropertyChangedFor(nameof(HasTint))]
    [NotifyPropertyChangedFor(nameof(TintColorValue))]
    private Avalonia.Media.Color? _tintColor;

    /// <summary>True when a tint is set.</summary>
    public bool HasTint => TintColor is not null;

    /// <summary>Low-alpha wash for the deck background - a hint of color, not a solid block. Transparent when unset.</summary>
    public Avalonia.Media.IBrush TintBrush => TintColor is { } c
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x24, c.R, c.G, c.B))
        : Avalonia.Media.Brushes.Transparent;

    /// <summary>Full-strength tint for the swatch dot, so the chosen code-color stays legible.</summary>
    public Avalonia.Media.IBrush TintAccentBrush => TintColor is { } c
        ? new Avalonia.Media.SolidColorBrush(c)
        : Avalonia.Media.Brushes.Transparent;

    /// <summary>Non-null view of <see cref="TintColor"/> for the custom <c>ColorPicker</c> (whose Color is not
    /// nullable). Reads the current tint (gray when unset); writing it sets the tint.</summary>
    public Avalonia.Media.Color TintColorValue
    {
        get => TintColor ?? Avalonia.Media.Colors.SlateGray;
        set => TintColor = value;
    }

    /// <summary>The quick-pick tint swatches (plus a ColorPicker for custom) shown in the deck's tint menu.</summary>
    public IReadOnlyList<Models.PlayerTintSwatch> TintSwatches => Models.PlayerTintPalette.Swatches;

    /// <summary>Sets (or, with null, clears) the deck tint - bound by the swatch buttons.</summary>
    [RelayCommand]
    private void SetTint(Avalonia.Media.Color? color) => TintColor = color;

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ReleasePlaybackSleepInhibitor();
            _idleSlateSyncTimer.Stop();
            try { _statusMessageClearCts?.Cancel(); } catch { /* best effort */ }
            try { _statusMessageClearCts?.Dispose(); } catch { /* best effort */ }
            _statusMessageClearCts = null;
            CancelWaveformExtraction();
            StopIdleSlate();
            UnsubscribeOutputEvents();
            DetachPlaylistTabSelection();
            DetachOutputBindings();
            UnwatchInputTrimRows();
        });

        await CloseSessionAsync().ConfigureAwait(false);

        // 8a.4 re-back: tear down the per-player ShowSession (poll stop + lease release on the UI thread; the
        // session disposes on its own dispatcher). No observable-property writes here - the VM is going away.
        await Dispatcher.UIThread.InvokeAsync(StopShowSessionPoll);
        var playerSession = _playerShowSession;
        _playerShowSession = null;
        if (playerSession is not null)
        {
            try { await playerSession.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession dispose"); }
        }

        // The deck owns the persistent visualizer source (disposeSourceOnRemove: false) - stop its
        // continuous render thread with the deck. Session dispose above only unhooked it.
        try { _visualizerSource?.Dispose(); }
        catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: visualizer source dispose"); }
        _visualizerSource = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var held in _playerAcquiredLines)
                _outputs.ReleaseVideoOutputForLine(held);
            _playerAcquiredLines.Clear();
        });
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
        if (!ShowSessionHotSwapActive) // nothing playing
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

        // Hot-add the clone to the live composition (HotAdd is idempotent + guards already-driven).
        await WithPlaybackArcAsync(() => Dispatcher.UIThread.InvokeAsync(() =>
            ShouldRouteLine(line) ? HotAddOutputToShowSessionAsync(line) : Task.CompletedTask)).ConfigureAwait(false);
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

    /// <summary>True when no outputs are registered yet - view shows the "click Play to auto-route" hint.</summary>
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

    /// <summary>The subset of <see cref="Outputs"/> this player is actually routing to (checkbox on).
    /// Drives the deck header's aggregate output chip and its per-output flyout.</summary>
    public IEnumerable<PlayerOutputBinding> SelectedOutputs => Outputs.Where(b => b.IsSelected);

    /// <summary>True when at least one output is routed - used to swap the header flyout between the
    /// per-output list and the "not routed anywhere" warning.</summary>
    public bool HasRoutedOutputs => Outputs.Any(b => b.IsSelected);

    /// <summary>Worst-case health across the routed outputs - the aggregate dot's state. Error dominates
    /// Warning dominates Healthy; an unrouted or all-unknown set stays
    /// <see cref="OutputLineHealthState.Unknown"/> (the enum is ordered Unknown &lt; Healthy &lt; Warning &lt; Error).</summary>
    public OutputLineHealthState DeckOutputHealth
    {
        get
        {
            var worst = OutputLineHealthState.Unknown;
            foreach (var b in Outputs)
                if (b.IsSelected && b.Line.Health > worst)
                    worst = b.Line.Health;
            return worst;
        }
    }

    /// <summary>Fill for the aggregate health dot. Mirrors the semantic state tokens (StateHealthy /
    /// AccentWarn / StatePanic) as hex so it binds like the per-line
    /// <see cref="OutputLineViewModel.HealthColor"/>.</summary>
    public string DeckOutputHealthColor => DeckOutputHealth switch
    {
        OutputLineHealthState.Healthy => "#2E9E4B", // StateHealthy
        OutputLineHealthState.Warning => "#F9A825", // AccentWarn
        OutputLineHealthState.Error => "#C0392B",   // StatePanic
        _ => "#99888888",                           // TextDisabled
    };

    /// <summary>Compact aggregate label for the header chip: routed-output count plus a worst-case note
    /// when degraded ("3 outputs", "3 outputs · 1 reconnecting", "4 outputs · 1 offline").</summary>
    public string DeckOutputSummary
    {
        get
        {
            var selected = 0;
            var warnings = 0;
            var errors = 0;
            foreach (var b in Outputs)
            {
                if (!b.IsSelected) continue;
                selected++;
                switch (b.Line.Health)
                {
                    case OutputLineHealthState.Warning: warnings++; break;
                    case OutputLineHealthState.Error: errors++; break;
                }
            }
            if (selected == 0) return Resources.Strings.DeckRoutingNone;
            var text = selected == 1
                ? Resources.Strings.DeckRoutingCountOne
                : Resources.Strings.Format(nameof(Resources.Strings.DeckRoutingCountFormat), selected);
            if (errors > 0)
                text += Resources.Strings.Format(nameof(Resources.Strings.DeckRoutingErrorSuffixFormat), errors);
            else if (warnings > 0)
                text += Resources.Strings.Format(nameof(Resources.Strings.DeckRoutingWarnSuffixFormat), warnings);
            return text;
        }
    }

    /// <summary>Re-push every derived header-aggregate property. Called when the routed set changes
    /// (selection toggle / outputs resync) or when a routed line's health changes.</summary>
    private void NotifyDeckOutputAggregate()
    {
        OnPropertyChanged(nameof(SelectedOutputs));
        OnPropertyChanged(nameof(HasRoutedOutputs));
        OnPropertyChanged(nameof(DeckOutputHealth));
        OnPropertyChanged(nameof(DeckOutputHealthColor));
        OnPropertyChanged(nameof(DeckOutputSummary));
    }

    /// <summary>Watches each routed line's health so the header aggregate dot/summary refresh live
    /// (the per-player <see cref="PlayerOutputBinding"/> does not forward the shared line's changes).</summary>
    private void OnOutputLinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputLineViewModel.Health))
            NotifyDeckOutputAggregate();
    }

    /// <summary>Short header text for the hold-image expander.</summary>
    public string HoldImageSummary
    {
        get
        {
            if (!HoldFallbackVideo)
                return string.IsNullOrWhiteSpace(FallbackImagePath) ? "(off)" : "(off - image set)";
            return string.IsNullOrWhiteSpace(FallbackImagePath) ? "(on - no image)" : "(on)";
        }
    }

    /// <summary>Text label for the Play/Pause toggle (the view pairs it with the matching icon).</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

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
            if (!IsMediaLoaded || !ShowSessionActive) return "Idle";
            // Derive the label from the item kind + the probed/observed streams (the item's declared
            // stream selection for live inputs; the open-time video probe for files).
            return _currentPlaylistItem switch
            {
                NDIInputPlaylistItem { AudioOnly: true } => "Live audio",
                NDIInputPlaylistItem { VideoOnly: true } => "Live video",
                NDIInputPlaylistItem => "Live video + audio",
                PortAudioInputPlaylistItem => "Live audio",
                _ when _playerShowHasVideo => "Video + audio",
                _ => "Audio",
            };
        }
    }

    [ObservableProperty]
    private bool _isRoutingExpanded = true;

    public ObservableCollection<PlaylistTabViewModel> PlaylistTabs { get; } = new();

    [ObservableProperty]
    private PlaylistTabViewModel? _selectedPlaylistTab;

    /// <summary>Phase C.5 (§6.8) - discriminated entries (files + live inputs) queued for sequential
    /// playback on the visible playlist tab.</summary>
    public ObservableCollection<PlaylistItem> PlaylistItems => SelectedPlaylistTab?.Items ?? _emptyPlaylistItems;

    public IReadOnlyList<PlayerOutputPreset> OutputPresets { get; } = Enum.GetValues<PlayerOutputPreset>();

    public IReadOnlyList<PlayerTransitionMode> TransitionModes { get; } = Enum.GetValues<PlayerTransitionMode>();

    /// <summary>Phase C (§4.3.4) - combobox choices for the per-output channel-mix mode.</summary>
    public IReadOnlyList<AudioRouteMixMode> MixModes { get; } = Enum.GetValues<AudioRouteMixMode>();

    /// <summary>Phase C (§4.3.4) - TreeDataGrid rows. One row per (selected device × output channel),
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
    /// matrix - this is how an occasional 5.1 file folds down properly without manual cell edits.</summary>
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
        ApplyChannelPresetRuleIfMatching(MatrixInputChannelCount);
    }

    /// <summary>P5c - save this output's matrix as a shareable framework preset file (.mfmix).</summary>
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

    /// <summary>P5c - load a framework preset file into this output's matrix.</summary>
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

    /// <summary>Phase C (§4.3.4) - current source channel count for the TreeDataGrid's input columns.
    /// 0 until a matrix has been sized. Watched by the view's code-behind to rebuild input columns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrix))]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrixRoutes))]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrixInputTrims))]
    private int _audioMatrixInputChannelCount;

    /// <summary>True once the matrix has been sized and at least one routed output exists - gates the
    /// TreeDataGrid's visibility and the empty-state hint.</summary>
    public bool HasAudioMatrix => AudioMatrixInputChannelCount > 0 && AudioMatrixRows.Count > 0;

    /// <summary>True once there is at least one active (audible) route cell.</summary>
    public bool HasAudioMatrixRoutes => AudioMatrixRouteRows.Count > 0;

    public bool HasAudioMatrixInputTrims => AudioMatrixInputTrims.Count > 0;

    /// <summary>Phase C (§4.3.4) - change-stamp the view can hook to rebuild columns/rows after the matrix
    /// rows or channel counts change. Fires once per coalesced rebuild.</summary>
    public event EventHandler? AudioMatrixLayoutChanged;

    [ObservableProperty]
    private string _name = "Player";

    [ObservableProperty]
    private string? _mediaFilePath;

    [ObservableProperty]
    private string? _fallbackImagePath;

    /// <summary>Phase C.5 - selected item in the visible playlist tab (file OR live input). Replaces
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
    /// Defaults to false - auto-advance is rarely wanted in performance contexts where each track is cued.</summary>
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

    [ObservableProperty]
    private PlayerTransitionMode _transitionMode = PlayerTransitionMode.Cut;

    [ObservableProperty]
    private int _transitionDurationMs = 500;

    [ObservableProperty]
    private int _customOutputWidth = 1920;

    [ObservableProperty]
    private int _customOutputHeight = 1080;

    [ObservableProperty]
    private bool _isMediaLoaded;

    /// <summary>Phase C.5 (§6.9) - true while a live item is offline / disconnected and the retry loop
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

    /// <summary>True whenever the header should show the slim indeterminate bar - a media open is in
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

    /// <summary>Phase C.5 - wall-clock deadline for the next reconnect attempt. The loop timer reopens
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
    /// release. While set - and until the resulting seek arc finishes (<see cref="_seekArcRunning"/>) -
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

    /// <summary>True while a media open is in flight - drives the slim indeterminate loading bar in the
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
        && _currentPlaylistItem is { IsLive: false }
        && Duration > TimeSpan.Zero
        && RemainingTime <= LowTimeWarningThreshold;

    private static readonly TimeSpan LowTimeWarningThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Phase C.5 (§6.5) - true when the loaded source has a finite, seekable duration. Files
    /// with non-zero duration are seekable; live items (PortAudio capture, NDI receiver) are not (their
    /// duration stays zero). The view disables seeking (slider + jump box) when this is false.</summary>
    public bool IsTransportSeekable => Duration > TimeSpan.Zero;

    /// <summary>True while the loaded source is a live input (NDI / PortAudio capture). The view swaps
    /// the timeline slot from scrubber+clocks to a LIVE badge in place - same reserved height either
    /// way, so the transport row never moves when the media type changes.</summary>
    public bool IsLiveSource => IsMediaLoaded && _currentPlaylistItem is { IsLive: true };

    /// <summary>Pre-formatted text bound by the view - Avalonia's <c>StringFormat=-{}{0:...}</c> with a leading minus
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

    private float[]? _waveformPeaks;
    private int _waveformRevision;
    private CancellationTokenSource? _waveformCts;
    private Task? _waveformTask;

    /// <summary>True while the background waveform peaks are being computed for the loaded file - drives
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
            // Progressive display: partial snapshots land as they are analysed (throttled by the extractor),
            // so the waveform fills in left-to-right behind the scrubber instead of popping in at the end.
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, cts.Token, partial =>
            {
                if (cts.IsCancellationRequested)
                    return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_waveformCts, cts))
                        return;
                    WaveformPeaks = partial;
                    WaveformRevision++;
                });
            }).ConfigureAwait(false);
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

    /// <summary>Metering taps wrapped around every deck audio-output lease (registered by the ShowSession
    /// audio-output factory on the session thread, read by the UI poll) - the deck's VU source.</summary>
    private readonly object _meterTapGate = new();
    private readonly List<Playback.MeteringAudioOutput> _meterTaps = [];
    /// <summary>UI-poll-thread scratch snapshot of <see cref="_meterTaps"/> (reused per tick).</summary>
    private readonly List<Playback.MeteringAudioOutput> _meterTapScratch = [];

    private void RegisterMeterTap(Playback.MeteringAudioOutput tap)
    {
        lock (_meterTapGate)
            _meterTaps.Add(tap);
    }

    private void UnregisterMeterTap(Playback.MeteringAudioOutput tap)
    {
        lock (_meterTapGate)
            _meterTaps.Remove(tap);
    }

    private void PollAudioMeters()
    {
        _meterTapScratch.Clear();
        lock (_meterTapGate)
            _meterTapScratch.AddRange(_meterTaps);

        var peak = double.NegativeInfinity;
        foreach (var tap in _meterTapScratch)
        {
            var db = tap.ReadAndResetPeakDb();
            if (db > peak) peak = db;
        }

        _meterTapScratch.Clear();
        PeakLevelDb = peak;
    }

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

    public bool CanRemove => _requestRemove is not null;

    partial void OnIsMediaLoadedChanged(bool value)
    {
        NotifyTransportCanExecuteChanged();
        OnPropertyChanged(nameof(SourceKindLabel));
        OnPropertyChanged(nameof(IsTransportSeekable));
        OnPropertyChanged(nameof(IsLiveSource));
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
    }

    private void SyncPlaybackSleepInhibitor()
    {
        var shouldInhibit = IsPlaying && IsMediaLoaded && ShowSessionActive;
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
        if (ShowSessionActive || _isTransportBusy)
        {
            // The open path kicks StartWaveformExtraction explicitly once the transport settles.
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
        ShowItemPropertiesCommand.NotifyCanExecuteChanged();
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

        ResizeSelectedAudioMatrices();
        RebuildAudioMatrixRows();
        ApplyAllOutputMatricesToSession();
    }

    /// <summary>Mirror the shared outputs list into per-player bindings, preserving selection on the survivors.
    /// Clones (§3.4) are deliberately excluded - their routing is mirrored from the parent's checkbox by
    /// <see cref="SelectedOutputLines"/>, so showing them as separate checkboxes would be misleading.</summary>
    private void SyncOutputsCollection()
    {
        var keep = Outputs.ToDictionary(b => b.Line);
        foreach (var b in Outputs)
        {
            b.PropertyChanged -= OnOutputBindingPropertyChanged;
            b.Line.PropertyChanged -= OnOutputLinePropertyChanged;
            UnwatchMatrixCells(b);
        }
        Outputs.Clear();
        foreach (var line in _outputs.Outputs)
        {
            if (!line.SupportsMediaPlayerRouting)
                continue; // skip clones - handled via the parent's binding
            if (!keep.TryGetValue(line, out var binding))
                binding = new PlayerOutputBinding(line);
            binding.PropertyChanged += OnOutputBindingPropertyChanged;
            binding.Line.PropertyChanged += OnOutputLinePropertyChanged;
            WatchMatrixCells(binding);
            Outputs.Add(binding);
        }
        OnPropertyChanged(nameof(HasNoOutputs));
        OnPropertyChanged(nameof(RoutingSummary));
        NotifyDeckOutputAggregate();
    }

    private int SanitizedCustomOutputWidth() => Math.Clamp(CustomOutputWidth, 16, 7680);

    private int SanitizedCustomOutputHeight() => Math.Clamp(CustomOutputHeight, 16, 4320);

    /// <summary>Phase C (§4.3.4) - subscribe to per-cell PropertyChanged so any matrix edit pushes the new
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
        DockPanel.SetDock(buttons, global::Avalonia.Controls.Dock.Bottom);

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
            NotifyDeckOutputAggregate();
            if (binding.IsSelected)
                binding.Matrix.Resize(MatrixInputChannelCount, OutputChannelCountOrZero(binding.Line));
            RebuildAudioMatrixRows();
            // Hot toggle: if playback is running, mirror the checkbox change into the playback graph so
            // routing takes effect without reload. Without this, ticking a new output mid-play did nothing
            // until next Open / Play, and unticking left the route alive.
            if (ShowSessionHotSwapActive)
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
    /// Phase C (§4.3.4) - apply the mix-mode preset by rebuilding the matrix cells; the per-cell push
    /// then re-installs router routes via <see cref="ApplyOutputMatrixToSession"/>. Falls back to the
    /// single-route <c>ChannelMap</c> path on lines whose matrix hasn't been sized yet (no session open).
    /// </summary>
    private void ApplyOutputMixModeToSession(PlayerOutputBinding binding)
    {
        if (binding.Matrix.OutputChannelCount > 0 && binding.Matrix.InputChannelCount > 0)
        {
            binding.Matrix.ApplyPreset(binding.MixMode);
            ApplyOutputMatrixToSession(binding);
        }
        // An unsized matrix has nothing to apply yet; the preset lands when the grid sizes at the next open.
    }

    /// <summary>
    /// Phase C (§4.3.4) - push the binding's matrix cells down into the playback session, installing
    /// one router route per audible cell. Re-applies after any cell edit; click-free gain rides for
    /// master/per-output changes go through <see cref="ApplyOutputCompoundGainToSession"/> instead.
    /// </summary>
    private void ApplyOutputMatrixToSession(PlayerOutputBinding binding)
    {
        _ = binding; // the ShowSession re-apply rebuilds every line's routes from the current bindings
        ReapplyDeckAudioToShowSessionIfActive();
    }

    /// <summary>Linear master × per-output gain (envelope) applied on top of every cell's own gain.</summary>
    private float CompoundEnvelope(PlayerOutputBinding binding)
    {
        if (MasterMuted || binding.IsMuted) return 0f;
        var db = Math.Clamp(MasterVolumeDb + binding.GainDb, -80.0, 24.0);
        return (float)Math.Pow(10.0, db / 20.0);
    }

    /// <summary>Click-free gain ride for one line - the ShowSession re-apply carries the compound gain on
    /// every route, so a single full re-apply covers it.</summary>
    private void ApplyOutputCompoundGainToSession(OutputLineViewModel line)
    {
        _ = line;
        ReapplyDeckAudioToShowSessionIfActive();
    }

    private void ApplyAllOutputGainsToSession() => ReapplyDeckAudioToShowSessionIfActive();

    /// <summary>Pushes the full per-cell matrix for every selected output into the running clip (one full
    /// ShowSession route re-apply covers all lines).</summary>
    private void ApplyAllOutputMatricesToSession() => ReapplyDeckAudioToShowSessionIfActive();

    /// <summary>
    /// Phase C (§4.3.4) - rebuild <see cref="AudioMatrixRows"/> from the currently-selected bindings.
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
        // UI rewrite P2: rows are simply (line, channel) in alias order - the operator-managed
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
        // The line's runtime is about to be swapped (Edit dialog): detach it from the live composition so
        // nothing submits into a runtime that is seconds from disposal.
        if (ShowSessionHotSwapActive)
            await Dispatcher.UIThread.InvokeAsync(() => HotRemoveOutputFromShowSessionAsync(line)).ConfigureAwait(false);
    }

    private async Task OnOutputLineReconfiguredAsync(OutputLineViewModel line)
    {
        // The line came back with a fresh runtime: re-attach it (video + audio routes) if it is still routed.
        if (ShowSessionHotSwapActive && ShouldRouteLine(line))
            await Dispatcher.UIThread.InvokeAsync(() => HotAddOutputToShowSessionAsync(line)).ConfigureAwait(false);
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
                binding.Matrix.Resize(MatrixInputChannelCount, OutputChannelCountOrZero(binding.Line));
            if (gain.MatrixCells.Count > 0)
                binding.Matrix.ApplyConfig(gain.MatrixCells);
        }
    }

    /// <summary>
    /// Phase B follow-up (§4.3.3) - call the session's hot route APIs from a checkbox toggle.
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

        // Hot add/remove each target on the LIVE composition. Hold the playback arc so a toggle can't race
        // Stop/switch, and marshal the (UI-affine) acquire/attach to the UI thread - the arc runs its action
        // off-thread.
        if (!ShowSessionHotSwapActive)
            return;
        await WithPlaybackArcAsync(async () =>
        {
            foreach (var target in targets)
            {
                var t = target;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    add ? HotAddOutputToShowSessionAsync(t) : HotRemoveOutputFromShowSessionAsync(t));
            }
        }).ConfigureAwait(false);
    }

    private void OnOutputLineRemoving(object? sender, OutputLineViewModel line)
    {
        // The management VM is about to dispose the runtime: detach the line from the live composition +
        // release it now so nothing submits into an output that's seconds away from disposal. The detach hops
        // the session dispatcher (best-effort, fire-and-forget) but the runtime is only disposed "seconds
        // away", so it completes first. Clones tied to this parent route through this handler in turn.
        if (ShowSessionHotSwapActive)
            _ = HotRemoveOutputFromShowSessionAsync(line);
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

    /// <summary>Phase B (§3.6) - true when this player is playing AND drives the line (video acquisition or
    /// an audio route to its device). Used by the Edit confirm prompt.</summary>
    public bool IsActivelyPlayingThroughLine(OutputLineViewModel line) =>
        IsPlaying && ShowSessionActive
        && (_playerAcquiredLines.Contains(line.Definition.Id)
            || _playerAcquiredAudioLines.Contains(line.Definition.Id)
            || (Outputs.FirstOrDefault(b => b.Line == line)?.IsSelected ?? false));

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

    /// <summary>Phase A - public snapshot for project save (§7). Internally still calls the same builder
    /// used by the per-player Save Player command, so the JSON shape stays identical.</summary>
    public MediaPlayerConfig BuildPlayerConfigSnapshot() => BuildPlayerConfig();

    /// <summary>Phase A - public companion to <see cref="BuildPlayerConfigSnapshot"/>; applies a player
    /// config in-place. Same semantics as the per-player Load Player command.</summary>
    public void ApplyPlayerConfigSnapshot(MediaPlayerConfig config) => ApplyPlayerConfig(config);

    private MediaPlayerConfig BuildPlayerConfig() => new()
    {
        Name = Name,
        PlaylistTabs = PlaylistTabs.Select(t => t.ToConfig()).ToList(),
        SelectedPlaylistTabIndex = SelectedPlaylistTab is null ? 0 : Math.Max(0, PlaylistTabs.IndexOf(SelectedPlaylistTab)),
        // Phase C.5 - the discriminated items live in PlaylistTabs[*].Items now. Keep the legacy flat
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
        TintArgb = TintColor?.ToUInt32(),
        OutputPreset = OutputPreset,
        TransitionMode = TransitionMode,
        TransitionDurationMs = TransitionDurationMs,
        CustomOutputWidth = SanitizedCustomOutputWidth(),
        CustomOutputHeight = SanitizedCustomOutputHeight(),
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

    /// <summary>Phase C - a matrix is non-default when any cell deviates from the identity layout
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
        // Project/recovery load: the shared outputs list was typically just replaced, but each player's
        // binding rebuild is DEFERRED (OnSharedOutputsCollectionChanged posts it). Applying the saved
        // routing onto the stale bindings meant the posted rebuild wiped every restored selection
        // moments later ("outputs not connected" after a session restore). Sync NOW so the selections
        // land on the live lines - the deferred rebuild then preserves them as survivors.
        SyncOutputsCollection();

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

        // A restored deck comes back IDLE: nothing is actually open, so restoring the last-loaded path
        // here only made the header LOOK loaded (CurrentMediaDisplay falls back to MediaFilePath) and
        // kicked off a waveform extraction of the whole file. The playlist selection below still
        // restores, so the operator is one Play away from where they were. The path stays in the
        // config file for forward compatibility; the live deck just doesn't adopt it.
        MediaFilePath = null;
        _currentPlaylistItem = null;
        CurrentPlayingItem = null;
        OnPropertyChanged(nameof(CurrentMediaDisplay));
        var selectedIndex = Math.Clamp(config.SelectedPlaylistTabIndex, 0, PlaylistTabs.Count - 1);
        SelectedPlaylistTab = PlaylistTabs[selectedIndex];
        RemovePlaylistTabCommand.NotifyCanExecuteChanged();
        FallbackImagePath = config.FallbackImagePath;
        HoldFallbackVideo = config.HoldFallbackVideo;
        MasterVolumeDb = config.MasterVolumeDb;
        MasterMuted = config.MasterMuted;
        TintColor = config.TintArgb is { } argb ? Avalonia.Media.Color.FromUInt32(argb) : null;
        OutputPreset = config.OutputPreset;
        TransitionMode = config.TransitionMode;
        TransitionDurationMs = config.TransitionDurationMs <= 0 ? 500 : config.TransitionDurationMs;
        CustomOutputWidth = Math.Clamp(config.CustomOutputWidth, 16, 7680);
        CustomOutputHeight = Math.Clamp(config.CustomOutputHeight, 16, 4320);
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _loopTimer?.Stop();
            _loopTimer = null;
        });
        await CancelWaveformExtractionAndWaitAsync().ConfigureAwait(false);

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
            {
                LoadState = PlayerLoadState.Idle;
                // A hard close (not a reload arc) clears the now-playing markers; WaitingForSource keeps them
                // (the retry loop owns that state and re-lights them on each attempt).
                if (resetPlayingUi) CurrentPlayingItem = null;
            }
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SeekToSliderCommand.NotifyCanExecuteChanged();
            if (!deferIdleSync) SyncIdleSlate();
        });
        SDebug.ChangeTrace.Step("CloseSession: UI cleanup done");
    }

    private bool CanLoadMedia()
    {
        // Phase C.5 - live items go through TryCreate(PlaylistItem) which dispatches to TryCreateLive.
        // File items need a readable path; live items are accepted unconditionally (the playback path
        // will surface its own error if the device / source can't be resolved).
        if (_currentPlaylistItem is { IsLive: true })
            return true;
        if (_currentPlaylistItem is FilePlaylistItem f && File.Exists(f.Path))
            return true;
        // Registry-URI items (youtube:// prepared-cache assets, mmd:// scenes): accepted unconditionally -
        // the open path surfaces its own actionable error (reliable-mode "not prepared", missing model
        // file). Gating them here silently no-ops the play with nothing in the log (the 2026-07-03
        // "YouTube item is instantly done" report: this gate predated both item kinds).
        if (_currentPlaylistItem is YouTubePlaylistItem or MMDPlaylistItem)
            return true;
        return _currentPlaylistItem is null
               && !string.IsNullOrWhiteSpace(MediaFilePath)
               && File.Exists(MediaFilePath!);
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

            // The per-player ShowSession is the ONLY playback runtime (the legacy engine is deleted). The
            // open handles live retry internally (waiting-for-source); false = a real open failure.
            if (await TryOpenViaShowSessionAsync(item, selected))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to open {item.DisplayName}.";
                LastLoadError = $"{item.DisplayName}: failed to open media";
                LoadState = PlayerLoadState.Failed;
                SyncIdleSlate();
            });
            SDebug.ChangeTrace.Step("OpenOrReload: ShowSession open failed");
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

    private static readonly TimeSpan LoopPollRelaxed = TimeSpan.FromMilliseconds(500);

    private void OnLoopTimerTick(object? sender, EventArgs e) => _ = ProcessLoopTimerTickAsync();

    private async Task ProcessLoopTimerTickAsync()
    {
        // Phase C.5 (§6.9) - drive the reconnect retry loop; the ShowSession open fires on success (the
        // retry re-opens the last-known live item). Playback progression itself - natural end / loop /
        // playlist auto-advance - is owned by the ShowSession poll (OnShowSessionPollTick).
        if (IsWaitingForSource && _waitingItem is not null && DateTime.UtcNow >= _nextRetryAt)
        {
            var item = _waitingItem;
            _currentPlaylistItem = item;
            CurrentPlayingItem = item; // keep the marker lit while a live source retries
            // Push the next deadline forward immediately so a slow open doesn't fire a retry storm
            // when the dispatcher catches up.
            var retrySec = GetRetrySeconds(item);
            _nextRetryAt = DateTime.UtcNow.AddSeconds(Math.Max(retrySec, 1));
            await OpenOrReloadAsync().ConfigureAwait(false);
        }
    }
}
