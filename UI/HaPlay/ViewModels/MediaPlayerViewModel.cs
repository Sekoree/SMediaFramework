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

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel : ViewModelBase
{
    private readonly OutputManagementViewModel _outputs;
    private readonly Action<MediaPlayerViewModel>? _requestRemove;
    private HaPlayPlaybackSession? _session;
    private DispatcherTimer? _loopTimer;
    private IdleLogoSlateSession? _idleSlate;
    private string? _idleSlateSig;
    private readonly DispatcherTimer _idleSlateSyncTimer;
    /// <summary>Phase 2B — runs on a threadpool tick instead of the UI dispatcher so transport gate
    /// holds don't stall the hold-image pump (NDI receivers would briefly freeze on every Pause/Play).</summary>
    private Timer? _holdPumpTimer;
    private int _holdPumpReentry;
    private PlaylistTabViewModel? _activePlaybackTab;
    private bool _syncingPlaylistTabState;
    private readonly ObservableCollection<string> _emptyPlaylistPaths = new();

    /// <summary>Serializes load/unload/stop/pause/play/seek and loop-timer Router use so Dispose cannot overlap transport.</summary>
    private readonly SemaphoreSlim _playbackArc = new(1, 1);

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
        // Phase B (§3.4) — also resync on definition changes (Edit) so clone-of transitions update
        // the routing checkbox list. CollectionChanged alone misses Edit-driven topology changes.
        _outputs.RoutingTopologyChanged += OnRoutingTopologyChanged;
        // Phase B follow-up — unwire from the active session BEFORE the runtime is disposed (§4.3.3).
        // Without this the AudioRouter pump keeps Submit'ing to a disposed PortAudioOutput and spams
        // ObjectDisposedException until the session is torn down.
        _outputs.OutputLineRemoving += OnOutputLineRemoving;
        _outputs.OutputLineReconfiguringAsync += OnOutputLineReconfiguringAsync;
        _outputs.OutputLineReconfiguredAsync += OnOutputLineReconfiguredAsync;
        _idleSlateSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _idleSlateSyncTimer.Tick += (_, _) => SyncIdleSlate();
        _idleSlateSyncTimer.Start();
        var initialTab = new PlaylistTabViewModel("Set A");
        PlaylistTabs.Add(initialTab);
        SelectedPlaylistTab = initialTab;
        Dispatcher.UIThread.Post(() => SyncIdleSlate(), DispatcherPriority.Loaded);
    }

    private void OnPlaylistPathsChanged()
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

    private void OnRoutingTopologyChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SyncOutputsCollection();
            SyncIdleSlate();
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

    /// <summary>One-word kind label for the source-state chip ("Video", "Audio", "Idle").</summary>
    public string SourceKindLabel
    {
        get
        {
            if (!IsMediaLoaded || _session is null) return "Idle";
            var hasVid = _session.Player.Decoder.HasVideo;
            var hasAud = _session.Player.Decoder.HasAudio;
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

    /// <summary>Paths queued for sequential playback on the visible playlist tab.</summary>
    public ObservableCollection<string> PlaylistPaths => SelectedPlaylistTab?.Paths ?? _emptyPlaylistPaths;

    public IReadOnlyList<PlayerOutputPreset> OutputPresets { get; } = Enum.GetValues<PlayerOutputPreset>();

    public IReadOnlyList<PlayerTransitionMode> TransitionModes { get; } = Enum.GetValues<PlayerTransitionMode>();

    /// <summary>Phase C (§4.3.4) — combobox choices for the per-output channel-mix mode.</summary>
    public IReadOnlyList<AudioRouteMixMode> MixModes { get; } = Enum.GetValues<AudioRouteMixMode>();

    /// <summary>Phase C (§4.3.4) — TreeDataGrid rows. One row per (selected device × output channel),
    /// rebuilt whenever the selection set or the sized input channel count changes. Bound by the view's
    /// code-behind, which also installs dynamic input-channel columns.</summary>
    public ObservableCollection<AudioMatrixRow> AudioMatrixRows { get; } = new();

    /// <summary>Phase C (§4.3.4) — current source channel count for the TreeDataGrid's input columns.
    /// 0 until a session opens. Watched by the view's code-behind to rebuild input columns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioMatrix))]
    private int _audioMatrixInputChannelCount;

    /// <summary>True once the matrix has been sized and at least one routed output exists — gates the
    /// TreeDataGrid's visibility and the empty-state hint.</summary>
    public bool HasAudioMatrix => AudioMatrixInputChannelCount > 0 && AudioMatrixRows.Count > 0;

    /// <summary>Phase C (§4.3.4) — change-stamp the view can hook to rebuild columns/rows after the matrix
    /// rows or channel counts change. Fires once per coalesced rebuild.</summary>
    public event EventHandler? AudioMatrixLayoutChanged;

    [ObservableProperty]
    private string _name = "Player";

    [ObservableProperty]
    private string? _mediaFilePath;

    [ObservableProperty]
    private string? _fallbackImagePath;

    [ObservableProperty]
    private string? _selectedPlaylistPath;

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
    private PlayerOutputPreset _outputPreset = PlayerOutputPreset.AsSource;

    [ObservableProperty]
    private PlayerTransitionMode _transitionMode = PlayerTransitionMode.Cut;

    [ObservableProperty]
    private int _transitionDurationMs = 500;

    [ObservableProperty]
    private bool _isMediaLoaded;

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

    /// <summary>Pre-formatted text bound by the view — Avalonia's <c>StringFormat=-{}{0:...}</c> with a leading minus
    /// is fragile (the binding silently fails). Formatting in the VM avoids the trap.</summary>
    public string CurrentPositionText => FormatClock(CurrentPosition);
    public string RemainingTimeText => "-" + FormatClock(RemainingTime);
    public string DurationText => FormatClock(Duration);

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

    public bool CanRemove => _requestRemove is not null;

    partial void OnIsMediaLoadedChanged(bool value)
    {
        NotifyTransportCanExecuteChanged();
        OnPropertyChanged(nameof(SourceKindLabel));
        if (value)
            StopIdleSlate();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PlayPauseLabel));
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

    partial void OnDurationChanged(TimeSpan value)
    {
        _ = value;
        SeekToSliderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        _ = value;
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(CurrentPositionText));
    }

    partial void OnSelectedPlaylistPathChanged(string? value)
    {
        if (!_syncingPlaylistTabState && SelectedPlaylistTab is not null)
            SelectedPlaylistTab.SelectedPath = value;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPlaylistTabChanged(PlaylistTabViewModel? oldValue, PlaylistTabViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.Paths.CollectionChanged -= OnSelectedTabPathsCollectionChanged;
        if (newValue is not null)
            newValue.Paths.CollectionChanged += OnSelectedTabPathsCollectionChanged;

        _syncingPlaylistTabState = true;
        try
        {
            SelectedPlaylistPath = newValue?.SelectedPath is { } sp && newValue.Paths.Contains(sp, StringComparer.Ordinal)
                ? sp
                : newValue?.Paths.FirstOrDefault();
            IsLooping = newValue?.IsLooping ?? false;
            AutoAdvancePlaylist = newValue?.AutoAdvance ?? false;
        }
        finally
        {
            _syncingPlaylistTabState = false;
        }

        OnPropertyChanged(nameof(PlaylistPaths));
        OnPlaylistPathsChanged();
    }

    private void OnSelectedTabPathsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPlaylistPathsChanged();

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
    }

    partial void OnMasterMutedChanged(bool value)
    {
        _ = value;
        ApplyAllOutputGainsToSession();
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
    }

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
        if (!session.TrySetOutputMatrix(binding.Line, binding.Matrix.ToRouteCells(), compound, out var err) &&
            !string.IsNullOrWhiteSpace(err))
            StatusMessage = err;
    }

    /// <summary>Linear master × per-output gain (envelope) applied on top of every cell's own gain.</summary>
    private float CompoundEnvelope(PlayerOutputBinding binding)
    {
        if (MasterMuted || binding.IsMuted) return 0f;
        var db = Math.Clamp(MasterVolumeDb + binding.GainDb, -80.0, 24.0);
        return (float)Math.Pow(10.0, db / 20.0);
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
        foreach (var binding in Outputs)
        {
            if (!binding.IsSelected) continue;
            if (binding.Matrix.InputChannelCount == 0) continue;
            inputChannels = Math.Max(inputChannels, binding.Matrix.InputChannelCount);
            for (var oc = 0; oc < binding.Matrix.OutputChannelCount; oc++)
            {
                var label = binding.Matrix.OutputChannelCount == 2
                    ? $"{binding.Line.Definition.DisplayName} · Out {(oc == 0 ? "L" : "R")}"
                    : $"{binding.Line.Definition.DisplayName} · Out {oc + 1}";
                AudioMatrixRows.Add(new AudioMatrixRow(binding, oc, label));
            }
        }

        AudioMatrixInputChannelCount = inputChannels;
        OnPropertyChanged(nameof(HasAudioMatrix));
        AudioMatrixLayoutChanged?.Invoke(this, EventArgs.Empty);
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
                        var srcCh = _session.Player.Decoder.Audio?.Format.Channels ?? 2;
                        binding.Matrix.Resize(srcCh, 2);
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
                            var srcCh = session.Player.Decoder.Audio?.Format.Channels ?? 2;
                            b.Matrix.Resize(srcCh, 2);
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
        // router doesn't keep submitting to a sink that's seconds away from disposal.
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
                if (!PlaylistPaths.Contains(path, StringComparer.Ordinal))
                {
                    PlaylistPaths.Add(path);
                    added++;
                }
                else
                {
                    skipped++;
                }
            }
            Log($"foreach done added={added} skipped={skipped} count={PlaylistPaths.Count}");

            if (SelectedPlaylistPath is null && PlaylistPaths.Count > 0)
            {
                Log("setting initial SelectedPlaylistPath");
                SelectedPlaylistPath = PlaylistPaths[0];
                Log("set initial SelectedPlaylistPath done");
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
        if (string.IsNullOrEmpty(SelectedPlaylistPath))
            return;
        var i = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        if (i < 0) return;
        PlaylistPaths.RemoveAt(i);
        SelectedPlaylistPath = PlaylistPaths.Count > 0
            ? PlaylistPaths[Math.Min(i, PlaylistPaths.Count - 1)]
            : null;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistItem() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.Contains(SelectedPlaylistPath);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemUp))]
    private void MovePlaylistItemUp()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path)) return;
        var i = PlaylistPaths.IndexOf(path);
        if (i <= 0) return;
        (PlaylistPaths[i - 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i - 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemUp() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.IndexOf(SelectedPlaylistPath!) > 0;

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemDown))]
    private void MovePlaylistItemDown()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path)) return;
        var i = PlaylistPaths.IndexOf(path);
        if (i < 0 || i >= PlaylistPaths.Count - 1) return;
        (PlaylistPaths[i + 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i + 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemDown()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath)) return false;
        var idx = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        return idx >= 0 && idx < PlaylistPaths.Count - 1;
    }

    /// <summary>Invoked from the view when the user double-clicks a playlist item — load it and start playing.</summary>
    public async Task PlayPlaylistItemAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        _activePlaybackTab = SelectedPlaylistTab;
        SelectedPlaylistPath = path;
        MediaFilePath = path;
        await OpenOrReloadAsync().ConfigureAwait(false);
        if (_session is not null && !IsPlaying)
            await StartPlaybackAsync().ConfigureAwait(false);
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
        PlaylistPaths = PlaylistPaths.ToList(),
        MediaFilePath = MediaFilePath,
        SelectedPlaylistPath = SelectedPlaylistPath,
        FallbackImagePath = FallbackImagePath,
        IsLooping = IsLooping,
        AutoAdvancePlaylist = AutoAdvancePlaylist,
        HoldFallbackVideo = HoldFallbackVideo,
        MasterVolumeDb = MasterVolumeDb,
        MasterMuted = MasterMuted,
        OutputPreset = OutputPreset,
        TransitionMode = TransitionMode,
        TransitionDurationMs = TransitionDurationMs,
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
                    Paths = config.PlaylistPaths,
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
            StopHoldPumpTimer();
            _loopTimer?.Stop();
            _loopTimer = null;
            var snap = _session;
            if (snap is not null)
            {
                try { snap.Player.PlayClock.PositionChanged -= OnClockPositionChanged; }
                catch { /* best effort */ }
                _session = null;
            }
            return snap;
        });

        if (snapshot is not null)
        {
            // Two-tier wall: 2s inner ct keeps Pause from hanging; 8s outer wall lets Dispose finish even on slow
            // sinks. A previous 50s outer cap would freeze the UI for nearly a minute if a sink blocked.
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

    private bool CanLoadMedia() =>
        !string.IsNullOrWhiteSpace(MediaFilePath) && File.Exists(MediaFilePath!);

    private async Task OpenOrReloadAsync()
    {
        if (!CanLoadMedia())
            return;

        var resumeAfterOpen = await Dispatcher.UIThread.InvokeAsync(() => IsPlaying);

        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: !resumeAfterOpen);

            var (path, selected) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                var lines = SelectedOutputLines();
                _outputs.StopPreviewsForPlayback(lines);
                return (MediaFilePath!, lines);
            });

            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreate(path, selected, _outputs, out created, out createErr))
                    created = null;
            }).ConfigureAwait(false);

            // Never use InvokeAsync(async () => await Task.Run(...)): the UI dispatcher can deadlock with the
            // threadpool continuation that is waiting for InvokeAsync to complete.
            var holdFbAfterOpen = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    StatusMessage = createErr ?? "Failed to open media.";
                    SyncIdleSlate();
                    return false;
                }

                _session = created;
                IsMediaLoaded = true;
                StatusMessage = null;
                Duration = created.Player.Decoder.Audio is ISeekableSource a ? a.Duration : TimeSpan.Zero;

                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    created.ApplyFallbackImage(FallbackImagePath);
                created.SetHoldFallback(HoldFallbackVideo);
                // Size every binding's matrix to the source channel count (sink is currently always 2; see
                // HaPlayPlaybackSession WireAudio downmix), then push matrices into the session so cell
                // routes are installed before the first chunk runs.
                var srcCh = created.Player.Decoder.Audio?.Format.Channels ?? 2;
                foreach (var binding in Outputs)
                {
                    binding.Matrix.Resize(srcCh, 2);
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
        // Drop-not-queue if a previous tick is still in flight (sink Submit can briefly block on NDI
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
            catch { /* best effort — sink torn down mid-tick */ }
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
        if (_session is null || !IsMediaLoaded || !IsPlaying)
            return;

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
                var loopReady = session.Player.Audio?.Router is { } loopAr
                    ? !loopAr.IsRunning && loopAr.CompletedNaturally
                    : session.Player.Video.CompletedNaturally;
                if (!loopReady) return;
                await RunBoundedCancelableAsync(ct =>
                    {
                        session.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, ct);
                        // No NDI warmup on loop wrap — receivers are already locked on and a silence gap would
                        // be audible between the last and first samples of the loop.
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    },
                    innerTimeout: TimeSpan.FromSeconds(3),
                    outerTimeout: TimeSpan.FromSeconds(5));

                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
                return;
            }

            var fileEnded = session.Player.Audio?.Router is { } ar
                ? !ar.IsRunning && ar.CompletedNaturally
                : session.Player.Video.CompletedNaturally;
            if (!fileEnded) return;

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
            if (!TryGetNextPlaylistPath(out var nextPath)) return false;
            MediaFilePath = nextPath;
            if (_activePlaybackTab is not null)
                _activePlaybackTab.SelectedPath = nextPath;
            if (ReferenceEquals(_activePlaybackTab, SelectedPlaylistTab))
                SelectedPlaylistPath = nextPath;
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
        if (_session is null && !string.IsNullOrEmpty(SelectedPlaylistPath) && File.Exists(SelectedPlaylistPath))
        {
            _activePlaybackTab = SelectedPlaylistTab;
            MediaFilePath = SelectedPlaylistPath;
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
        (_session is null && !string.IsNullOrEmpty(SelectedPlaylistPath));

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextTrackAsync()
    {
        if (!TryGetNextPlaylistPath(out var next)) return;
        await PlayPlaylistItemAsync(next).ConfigureAwait(false);
    }

    private bool CanGoNext() => TryGetNextPlaylistPath(out _);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousTrackAsync()
    {
        if (!TryGetPreviousPlaylistPath(out var prev)) return;
        await PlayPlaylistItemAsync(prev).ConfigureAwait(false);
    }

    private bool CanGoPrevious() => TryGetPreviousPlaylistPath(out _);

    private bool TryGetPreviousPlaylistPath([NotNullWhen(true)] out string? prevPath)
    {
        prevPath = null;
        var paths = ActivePlaybackPaths();
        if (paths.Count == 0 || string.IsNullOrEmpty(MediaFilePath))
            return false;
        var idx = IndexOfPath(paths, MediaFilePath);
        if (idx <= 0) return false;
        prevPath = paths[idx - 1];
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
        var path = SelectedPlaylistPath ?? MediaFilePath;
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
        (_session is null && !string.IsNullOrEmpty(SelectedPlaylistPath));

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

    [RelayCommand(CanExecute = nameof(CanTransport))]
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
                return (_session, HoldFallbackVideo);
            });
            if (snap is null) return;

            // One coordinated pause+seek so the freeze never exceeds the outer cap. Three nested
            // Task.Run/.WaitAsync blocks (the previous shape) could stack to ~11s on slow codecs.
            await RunBoundedCancelableAsync(ct =>
                {
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
            });
        }).ConfigureAwait(false);
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

    private bool TryGetNextPlaylistPath([NotNullWhen(true)] out string? nextPath)
    {
        nextPath = null;
        var paths = ActivePlaybackPaths();
        if (paths.Count == 0 || string.IsNullOrEmpty(MediaFilePath))
            return false;
        var idx = IndexOfPath(paths, MediaFilePath);
        if (idx < 0)
            return false;
        var n = idx + 1;
        if (n >= paths.Count)
            return false;
        nextPath = paths[n];
        return true;
    }

    private IReadOnlyList<string> ActivePlaybackPaths() =>
        _activePlaybackTab?.Paths ?? PlaylistPaths;

    private static int IndexOfPath(IReadOnlyList<string> paths, string path)
    {
        for (var i = 0; i < paths.Count; i++)
            if (string.Equals(paths[i], path, StringComparison.Ordinal))
                return i;
        return -1;
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
