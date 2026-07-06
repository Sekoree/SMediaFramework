using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.OutputPreview;
using HaPlay.Playback;
using HaPlay.Resources;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels;

public partial class OutputManagementViewModel : ViewModelBase
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.ViewModels.OutputManagementViewModel");

    public ObservableCollection<OutputLineViewModel> Outputs { get; } = new();

    public bool IsNDIAvailable => RuntimeModules.IsNDIAvailable;

    private volatile IReadOnlyList<OutputDefinition> _definitionsSnapshot = Array.Empty<OutputDefinition>();

    /// <summary>Thread-safe immutable snapshot of every output line's <see cref="OutputLineViewModel.Definition"/>,
    /// rebuilt on the UI thread whenever the line-up or a definition changes. Background callers (cue route
    /// sanitize / pre-roll) read this instead of marshaling onto the UI thread to enumerate
    /// <see cref="Outputs"/>: the old blocking <c>InvokeAsync(...).GetResult()</c> could pile up and starve
    /// the thread pool (it is uncancellable and the UI thread may be busy, e.g. behind a modal file dialog).</summary>
    public IReadOnlyList<OutputDefinition> DefinitionsSnapshot => _definitionsSnapshot;

    /// <summary>Acquires the real <see cref="IVideoOutput"/> for an output line so a playback engine can render
    /// onto it — the ShowSession cue re-back's video-output seam (mirrors how the cue engine acquires outputs).
    /// MUST be called on the UI thread (realizes the SDL window / NDI sender). Returns null for a non-video line
    /// or one with no realized runtime.</summary>
    public IVideoOutput? AcquireVideoOutputForLine(Guid lineId)
    {
        var line = Outputs.FirstOrDefault(o => o.Definition.Id == lineId);
        if (line is null)
            return null;
        if (_localPreviews.TryGetValue(line, out var local))
            return local.AcquireForPlayback();
        lock (_ndiOutputsGate)
            if (_ndiOutputs.TryGetValue(line, out var ndi))
                return ndi.AcquireForPlayback(needsVideo: true, needsAudio: false)?.Video;
        return null;
    }

    /// <summary>Releases a video output acquired via <see cref="AcquireVideoOutputForLine"/> (single-holder, so
    /// the ShowSession cue re-back MUST release before re-acquiring on a reload, else the line stays held). UI
    /// thread. No-op for an unknown/non-video line.</summary>
    public void ReleaseVideoOutputForLine(Guid lineId)
    {
        var line = Outputs.FirstOrDefault(o => o.Definition.Id == lineId);
        if (line is null)
            return;
        if (_localPreviews.TryGetValue(line, out var local))
        {
            local.ReleaseFromPlayback();
            return;
        }
        lock (_ndiOutputsGate)
            if (_ndiOutputs.TryGetValue(line, out var ndi))
                ndi.ReleaseFromPlayback(releaseVideo: true, releaseAudio: false);
    }

    /// <summary>Acquires the AUDIO side of an NDI output line's carrier — the SAME sender that carries the
    /// composition's video — so a ShowSession clip can route audio into that NDI stream (the ShowSession audio
    /// re-back for NDI lines). Returns the carrier's audio sink at its configured audio format, or null for a
    /// non-NDI / unknown line or an audio-less NDI mode. UI thread; single-holder like the video side — release
    /// with <see cref="ReleaseAudioOutputForLine"/>.</summary>
    public IAudioOutput? AcquireAudioOutputForLine(Guid lineId)
    {
        var line = Outputs.FirstOrDefault(o => o.Definition.Id == lineId);
        if (line is null)
            return null;
        lock (_ndiOutputsGate)
            if (_ndiOutputs.TryGetValue(line, out var ndi))
                return ndi.AcquireForPlayback(needsVideo: false, needsAudio: true)?.Audio;
        return null;
    }

    /// <summary>Releases an audio output acquired via <see cref="AcquireAudioOutputForLine"/>. UI thread. No-op
    /// for a non-NDI / unknown line.</summary>
    public void ReleaseAudioOutputForLine(Guid lineId)
    {
        var line = Outputs.FirstOrDefault(o => o.Definition.Id == lineId);
        if (line is null)
            return;
        lock (_ndiOutputsGate)
            if (_ndiOutputs.TryGetValue(line, out var ndi))
                ndi.ReleaseFromPlayback(releaseVideo: false, releaseAudio: true);
    }


    private readonly Dictionary<OutputLineViewModel, ILocalVideoPreviewRuntime> _localPreviews = new();
    private readonly Dictionary<OutputLineViewModel, NDIOutputPreviewRuntime> _ndiOutputs = new();
    private readonly Dictionary<OutputLineViewModel, PortAudioOutputRuntime> _portAudioOutputs = new();
    private readonly Lock _ndiOutputsGate = new();
    private readonly Lock _portAudioOutputsGate = new();

    /// <summary>
    /// Phase B (§3.6) — set by <c>MainViewModel</c> so the Edit flow can ask whether *any* player is
    /// currently playing audio/video *through* a given line. Returning <c>true</c> triggers the
    /// "applying will glitch your show" confirm prompt. Returning <c>false</c> (or leaving the probe
    /// unset) skips the prompt and applies the edit immediately. See §3.6 decision: hot semantics
    /// with confirm only when the line is actively in use.
    /// </summary>
    public Func<OutputLineViewModel, bool>? PlaybackUsageProbe { get; set; }

    /// <summary>Supplies players with active sessions for output-line health LEDs.</summary>
    public Func<IReadOnlyList<MediaPlayerViewModel>>? ActivePlayersProbe { get; set; }

    /// <summary>Supplies cue-engine throughput for a line (or null when the engine doesn't drive it),
    /// so cue playback lights the health LEDs/stats instead of leaving the line "Idle".</summary>
    internal Func<Guid, Playback.OutputLineHealthEvaluator.LineHealthMetrics?>? CueLineMetricsProbe { get; set; }

    /// <summary>UI rewrite (I/O master-detail): the line whose detail/stats pane is shown. Cleared
    /// automatically when that line is removed.</summary>
    [ObservableProperty]
    private OutputLineViewModel? _selectedLine;

    private DispatcherTimer? _healthTimer;

    /// <summary>
    /// Phase B (§3.4) — every local-video line whose <see cref="LocalVideoOutputDefinition.CloneOfId"/>
    /// matches <paramref name="parentId"/>. Used by <see cref="MediaPlayerViewModel.SelectedOutputLines"/>
    /// to expand parent-selection into parent+clones (PlayerRoutingMirror) and by the Outputs view to
    /// render clones nested under their parent.
    /// </summary>
    public IEnumerable<OutputLineViewModel> GetClonesOf(Guid parentId) =>
        Outputs.Where(o => o.Definition is LocalVideoOutputDefinition lv && lv.CloneOfId == parentId);

    /// <summary>
    /// Phase B (§3.4) — local-video lines that can be promoted to a clone parent. Excludes the line
    /// passed in (a line cannot be its own parent) AND any line that's already a clone (no chained
    /// clone-of-clones — keeps the routing mirror trivially 1-deep).
    /// </summary>
    public IEnumerable<LocalVideoOutputDefinition> GetPotentialCloneParents(OutputLineViewModel? excluding = null) =>
        Outputs
            .Where(o => o != excluding)
            .Select(o => o.Definition)
            .OfType<LocalVideoOutputDefinition>()
            .Where(lv => lv.CloneOfId is null);

    /// <summary>
    /// Phase B (§3.4) — raised when the shape of the routing graph changes: outputs added / removed, or
    /// a definition reconfigured in a way that affects clone-of relationships. Players listen to this
    /// rather than the raw <c>CollectionChanged</c> so a "make this a clone" edit triggers their routing
    /// list to drop the clone's checkbox.
    /// </summary>
    public event EventHandler? RoutingTopologyChanged;

    // ----- Phase E (§8.1): aggregate health summary -------------------------------------------------

    /// <summary>Number of output lines currently driving at least one player session. 0 when the
    /// app is idle.</summary>
    [ObservableProperty]
    private int _aggregateActiveCount;

    /// <summary>Number of lines reporting <see cref="OutputLineHealthState.Warning"/> right now.</summary>
    [ObservableProperty]
    private int _aggregateWarningCount;

    /// <summary>Number of lines reporting <see cref="OutputLineHealthState.Error"/> right now.</summary>
    [ObservableProperty]
    private int _aggregateErrorCount;

    /// <summary>True when any line is below healthy — drives the colour of the summary chip.</summary>
    public bool HasAggregateIssues => AggregateWarningCount + AggregateErrorCount > 0;
    public bool HasOutputs => Outputs.Count > 0;
    public bool HasNoOutputs => Outputs.Count == 0;

    /// <summary>AUDIO-02: one-line media-backend availability (e.g. "FFmpeg ✓  PortAudio ✓  NDI ✗ (native
    /// library not installed)") so a missing optional backend is visible on the I/O workspace, not just in
    /// the log. Sourced from <see cref="MediaRuntime.ModuleDiagnostics"/>.</summary>
    public string MediaBackendsSummary =>
        string.Join("    ", MediaRuntime.ModuleDiagnostics.Select(
            d => d.Available ? $"{d.Name} ✓" : $"{d.Name} ✗ ({d.Detail})"));

    /// <summary>One-line summary: "5 active · 1 warning · 0 errors" — bound by the panel chip.</summary>
    public string AggregateSummary => AggregateActiveCount == 0
        ? Strings.AggregateSummaryIdle
        : Strings.Format(nameof(Strings.AggregateSummaryActiveFormat), AggregateActiveCount, AggregateWarningCount, AggregateErrorCount);

    partial void OnAggregateActiveCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(AggregateSummary));
        OnPropertyChanged(nameof(HasAggregateIssues));
    }

    partial void OnAggregateWarningCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(AggregateSummary));
        OnPropertyChanged(nameof(HasAggregateIssues));
    }

    partial void OnAggregateErrorCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(AggregateSummary));
        OnPropertyChanged(nameof(HasAggregateIssues));
    }

    public OutputManagementViewModel()
    {
        _healthTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshOutputHealth())
        {
            IsEnabled = true,
        };
        Outputs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOutputs));
            OnPropertyChanged(nameof(HasNoOutputs));
            if (SelectedLine is { } sel && !Outputs.Contains(sel))
                SelectedLine = null;
            SelectedLine ??= Outputs.FirstOrDefault();
            RoutingTopologyChanged?.Invoke(this, EventArgs.Empty);
        };
        Outputs.CollectionChanged += OnOutputsChangedForSnapshot;
        RebuildDefinitionsSnapshot();
    }

    // Keep DefinitionsSnapshot current: a line-up change (add/remove) or any line's Definition edit
    // (alias, channel count, resolution lock) rebuilds the immutable snapshot on the UI thread.
    private void OnOutputsChangedForSnapshot(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.OldItems is not null)
            foreach (var removed in e.OldItems.OfType<OutputLineViewModel>())
                removed.PropertyChanged -= OnOutputLineDefinitionChangedForSnapshot;
        if (e.NewItems is not null)
            foreach (var added in e.NewItems.OfType<OutputLineViewModel>())
                added.PropertyChanged += OnOutputLineDefinitionChangedForSnapshot;
        RebuildDefinitionsSnapshot();
    }

    private void OnOutputLineDefinitionChangedForSnapshot(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is null or nameof(OutputLineViewModel.Definition))
            RebuildDefinitionsSnapshot();
    }

    private void RebuildDefinitionsSnapshot() =>
        _definitionsSnapshot = Outputs.Select(o => o.Definition).ToArray();

    private void RaiseTopologyChanged() => RoutingTopologyChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// UI rewrite P2 (plan §2.3/§5): raised when an output's <see cref="OutputDefinition.Alias"/>
    /// changes so dependents (player matrix row labels, cue pickers) refresh their naming.
    /// </summary>
    public event EventHandler? OutputNamingChanged;

    internal void NotifyAliasChanged(OutputLineViewModel line)
    {
        _ = line;
        OutputNamingChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<string> ExistingOutputNames(Guid? excludingId = null)
    {
        var names = new List<string>(Outputs.Count * 2);
        foreach (var line in Outputs)
        {
            if (line.Definition.Id == excludingId)
                continue;

            if (!string.IsNullOrWhiteSpace(line.Definition.DisplayName))
                names.Add(line.Definition.DisplayName);
            if (!string.IsNullOrWhiteSpace(line.Definition.EffectiveName))
                names.Add(line.Definition.EffectiveName);
        }

        return names;
    }

    /// <summary>
    /// Phase B follow-up — raised *before* a line's runtime is torn down so any active playback
    /// can detach its route to that line first and avoid Submit'ing to a disposed output.
    /// Subscribers must run synchronously: by the time the event returns, the runtime
    /// stop / dispose path is about to run.
    /// </summary>
    public event EventHandler<OutputLineViewModel>? OutputLineRemoving;

    /// <summary>
    /// Raised around hot output edits so active players can drop and then re-acquire the line against
    /// the newly configured runtime. This keeps reconfigure-in-place from leaving routes pointed at a
    /// disposed PortAudio stream / NDI sender.
    /// </summary>
    public event Func<OutputLineViewModel, Task>? OutputLineReconfiguringAsync;

    public event Func<OutputLineViewModel, Task>? OutputLineReconfiguredAsync;

    private void Remove(OutputLineViewModel line)
    {
        // Let sessions unwire their routes first — otherwise the AudioRouter pump keeps pushing chunks
        // into a PortAudioOutput we're about to Dispose, producing the spammed ObjectDisposedException
        // observed when the user clicked Remove during active playback.
        OutputLineRemoving?.Invoke(this, line);

        StopLocalPreview(line);
        StopNDIOutput(line);
        StopPortAudioOutput(line);
        Outputs.Remove(line);
    }

    /// <summary>
    /// UI remove path: when a player is actively playing through <paramref name="line"/>, warn the operator
    /// and offer to stop that playback (so removal can't race a live submit and crash) or cancel. Programmatic
    /// removals (project load, reconfigure) keep calling <see cref="Remove"/> directly and never prompt.
    /// </summary>
    public async Task RemoveLineAsync(OutputLineViewModel line, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is not null && PlaybackUsageProbe?.Invoke(line) == true)
        {
            if (!await ConfirmRemoveInUseAsync(owner, line))
                return;
            await StopPlayersUsingLineAsync(line);
        }

        Remove(line);
    }

    private async Task StopPlayersUsingLineAsync(OutputLineViewModel line)
    {
        foreach (var player in ActivePlayersProbe?.Invoke() ?? [])
        {
            if (player.IsActivelyPlayingThroughLine(line) && player.StopCommand.CanExecute(null))
                await player.StopCommand.ExecuteAsync(null);
        }
    }

    private static async Task<bool> ConfirmRemoveInUseAsync(Window owner, OutputLineViewModel line)
    {
        var dlg = new Window
        {
            Title = Strings.OutputRemoveInUseDialogTitle,
            Width = 480,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = Strings.OutputRemoveInUseStopButton, IsDefault = true };
        var cancel = new Button { Content = Strings.CancelButton, IsCancel = true };

        var tcs = new TaskCompletionSource<bool>();
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
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
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var message = new TextBlock
        {
            Text = Strings.Format(nameof(Strings.OutputRemoveInUseDialogMessageFormat), line.Definition.DisplayName),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        root.Children.Add(buttons);
        root.Children.Add(message);
        dlg.Content = root;

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    /// <summary>
    /// Phase A — replaces every output line with new <see cref="OutputLineViewModel"/>s for the supplied
    /// definitions. Existing runtimes are stopped (matches the manual Remove path); the new lines do
    /// <strong>not</strong> have their runtimes started — Phase B's project-load orchestration is
    /// responsible for that. Exists so project save/load can roundtrip a definitions list without the
    /// caller having to script per-output runtime spin-up.
    /// </summary>
    public void ReplaceDefinitionsForLoad(IReadOnlyList<OutputDefinition> definitions)
    {
        // Snapshot the existing lines, then dispose them via the same Remove path the UI uses so any
        // running PortAudio stream / NDI sender / preview window is torn down cleanly.
        foreach (var existing in Outputs.ToList())
            Remove(existing);

        foreach (var def in definitions)
            Outputs.Add(new OutputLineViewModel(def, Remove, this));
    }

    /// <summary>
    /// Starts the runtime side for definitions restored from a project file. Failures leave the line in
    /// the list so the user can edit/rebind it; callers surface the returned messages as a project banner.
    /// </summary>
    public async Task<IReadOnlyList<string>> StartRuntimesForLoadedDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "OutputManagement.StartRuntimesForLoadedDefinitionsAsync", slowWarningMs: 3000);
        var errors = new List<string>();
        var started = 0;
        foreach (var line in Outputs.ToList())
        {
            try
            {
                Trace.LogDebug("Start loaded output runtime: name={Name} kind={Kind}", line.Definition.DisplayName, line.Definition.Kind);
                switch (line.Definition)
                {
                    case PortAudioOutputDefinition pa:
                        await EnsurePortAudioRuntimeAsync(line, pa, cancellationToken).ConfigureAwait(false);
                        break;
                    case NDIOutputDefinition nd:
                        await EnsureNDIRuntimeAsync(line, nd, cancellationToken).ConfigureAwait(false);
                        break;
                    case LocalVideoOutputDefinition:
                        await StartLocalPreviewAsync(line, cancellationToken).ConfigureAwait(false);
                        break;
                }
                started++;
            }
            catch (Exception ex)
            {
                errors.Add($"{line.Definition.DisplayName}: {ex.Message}");
                Trace.LogError(ex, "Failed to start loaded output runtime: name={Name} kind={Kind}", line.Definition.DisplayName, line.Definition.Kind);
            }
        }

        timing?.SetOutcome($"started={started} errors={errors.Count}");
        return errors;
    }

    private async Task EnsurePortAudioRuntimeAsync(
        OutputLineViewModel line,
        PortAudioOutputDefinition definition,
        CancellationToken cancellationToken)
    {
        lock (_portAudioOutputsGate)
        {
            if (_portAudioOutputs.ContainsKey(line))
                return;
        }

        var runtime = await Task.Run(() =>
        {
            return StartPortAudioRuntime(definition);
        }, cancellationToken).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!runtime.Definition.Equals(line.Definition))
                line.ReplaceDefinition(runtime.Definition);
            lock (_portAudioOutputsGate)
                _portAudioOutputs[line] = runtime;
        });
    }

    private static PortAudioOutputRuntime StartPortAudioRuntime(PortAudioOutputDefinition definition)
    {
        var runtime = new PortAudioOutputRuntime(definition);
        try
        {
            runtime.Start();
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private async Task EnsureNDIRuntimeAsync(
        OutputLineViewModel line,
        NDIOutputDefinition definition,
        CancellationToken cancellationToken)
    {
        lock (_ndiOutputsGate)
        {
            if (_ndiOutputs.ContainsKey(line))
                return;
        }

        var runtime = await Task.Run(() =>
        {
            var rt = new NDIOutputPreviewRuntime(definition);
            rt.Start();
            return rt;
        }, cancellationToken).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_ndiOutputsGate)
                _ndiOutputs[line] = runtime;
        });
    }

    /// <summary>
    /// Phase A (§9.6) — reconfigure the runtime backing <paramref name="line"/> with
    /// <paramref name="newDefinition"/>. Per-kind delegation: PortAudio swaps the stream, local-video
    /// reapplies window placement, NDI restarts the sender. <see cref="OutputLineViewModel.Definition"/>
    /// is replaced on the line in place so existing references stay valid. Does nothing if no runtime
    /// is registered for the line (the line was added by ReplaceDefinitionsForLoad but never bound).
    /// </summary>
    /// <remarks>
    /// Hot semantics per §3.6: the runtime swap proceeds even if a playback session currently holds the
    /// line. With <paramref name="detachRoutes"/> = true (default) the session detaches its route around the
    /// swap (the OutputLineReconfiguring/Reconfigured events) — a brief silence/black-frame, but a
    /// guaranteed-consistent route afterward. With <paramref name="detachRoutes"/> = false those events are
    /// skipped: the runtime applies the change in place under the still-attached route (cosmetic changes like
    /// Always-on-top apply with no interruption; a structural change may briefly glitch the live route).
    /// </remarks>
    public async Task ReconfigureLineAsync(
        OutputLineViewModel line,
        OutputDefinition newDefinition,
        CancellationToken cancellationToken = default,
        bool detachRoutes = true)
    {
        if (newDefinition.Id != line.Definition.Id)
            throw new ArgumentException(
                $"newDefinition.Id ({newDefinition.Id}) must match line.Definition.Id ({line.Definition.Id}).",
                nameof(newDefinition));
        if (newDefinition.GetType() != line.Definition.GetType())
            throw new ArgumentException(
                $"Cannot reconfigure {line.Definition.GetType().Name} with {newDefinition.GetType().Name} — output kind is immutable.",
                nameof(newDefinition));

        if (detachRoutes)
            await RaiseAsync(OutputLineReconfiguringAsync, line).ConfigureAwait(false);

        switch (newDefinition)
        {
            case PortAudioOutputDefinition pa:
            {
                PortAudioOutputRuntime? rt;
                lock (_portAudioOutputsGate)
                    _portAudioOutputs.TryGetValue(line, out rt);
                if (rt is not null)
                    await rt.ReconfigureAsync(pa, cancellationToken).ConfigureAwait(false);
                break;
            }
            case LocalVideoOutputDefinition lv:
            {
                if (_localPreviews.TryGetValue(line, out var rt))
                    await rt.ReconfigureAsync(lv, cancellationToken).ConfigureAwait(false);
                break;
            }
            case NDIOutputDefinition nd:
            {
                NDIOutputPreviewRuntime? rt;
                lock (_ndiOutputsGate)
                    _ndiOutputs.TryGetValue(line, out rt);
                if (rt is not null)
                    await rt.ReconfigureAsync(nd, cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new NotSupportedException($"Unknown output definition type: {newDefinition.GetType().Name}");
        }

        // Update the line's definition reference so VM consumers (routing checkboxes, KindLabel, Summary)
        // observe the new values.
        line.ReplaceDefinition(newDefinition);

        // A clone-of change is the load-bearing topology change — fire the event so player VMs resync
        // their routing checkbox list (clones get hidden, parents get exposed).
        RaiseTopologyChanged();
        if (detachRoutes)
            await RaiseAsync(OutputLineReconfiguredAsync, line).ConfigureAwait(false);
    }

    private static async Task RaiseAsync(Func<OutputLineViewModel, Task>? handlers, OutputLineViewModel line)
    {
        if (handlers is null)
            return;
        foreach (var d in handlers.GetInvocationList().Cast<Func<OutputLineViewModel, Task>>())
            await d(line).ConfigureAwait(false);
    }

    private static Window? TryGetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    /// <summary>
    /// A local video preview window stopped. <paramref name="userInitiated"/> is <see langword="true"/>
    /// only when the operator closed the OS window (clicked the title-bar X / quit), as opposed to our
    /// own programmatic teardown (Remove, reconfigure, shutdown). A user-closed window means the operator
    /// is done with that output, so we drop the whole line from the I/O page — which also raises
    /// <see cref="OutputLineRemoving"/> so any active playback session unwires its route first.
    /// </summary>
    internal void NotifyLocalPreviewEnded(OutputLineViewModel line, bool userInitiated = false)
    {
        _localPreviews.Remove(line, out _);
        line.IsPreviewRunning = false;

        // Removing the line re-enters StopLocalPreview, but _localPreviews no longer holds this line
        // (removed just above) so that path is a no-op for the already-closed window — no double dispose.
        if (userInitiated && Outputs.Contains(line))
            Remove(line);
    }

    internal void NotifyLocalPreviewResized(OutputLineViewModel line, int width, int height)
    {
        if (line.Definition is not LocalVideoOutputDefinition lv
            || lv.SurfaceMode != VideoSurfaceMode.Windowed
            || width < 320
            || height < 240)
        {
            return;
        }

        if (lv.WindowWidth == width && lv.WindowHeight == height)
            return;

        line.ReplaceDefinition(lv with { WindowWidth = width, WindowHeight = height });
    }

    public async Task StartLocalPreviewAsync(OutputLineViewModel line, CancellationToken cancellationToken = default)
    {
        if (line.Definition is not LocalVideoOutputDefinition d)
            return;
        if (_localPreviews.ContainsKey(line))
            return;

        ILocalVideoPreviewRuntime runtime = d.Engine == VideoOutputEngine.SDLOpenGl
            ? new SDLLocalVideoPreviewRuntime(d, line, this, TryGetOwnerWindow())
            : new AvaloniaLocalVideoPreviewRuntime(d, line, this, TryGetOwnerWindow());

        try
        {
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _localPreviews[line] = runtime;
                line.IsPreviewRunning = true;
            });
        }
        catch
        {
            runtime.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => line.IsPreviewRunning = false);
            throw;
        }
    }

    public void StopLocalPreview(OutputLineViewModel line)
    {
        if (!_localPreviews.Remove(line, out var rt))
        {
            line.IsPreviewRunning = false;
            return;
        }

        try
        {
            rt.Dispose();
        }
        catch
        {
            /* best effort */
        }

        line.IsPreviewRunning = false;
    }

    public void SetLocalPreviewFullscreen(OutputLineViewModel line, bool fullscreen)
    {
        if (_localPreviews.TryGetValue(line, out var rt))
            rt.SetFullscreen(fullscreen);
    }

    internal void StopPreviewsForPlayback(IEnumerable<OutputLineViewModel> lines)
    {
        // Both NDI carriers and local-video previews now stay alive across playback — sessions acquire the
        // existing output via TryAcquireLocalVideoOutputForPlayback / TryAcquireNDICarrierForPlayback so the
        // window doesn't flash on each media change. Kept for API stability; intentional no-op.
        _ = lines;
    }

    /// <summary>
    /// Returns the persistent local-video output (SDL or Avalonia) so a playback session can route decoded
    /// frames into the existing window. Returns <c>null</c> when the line isn't a local-video output,
    /// the preview isn't running, or another playback session already holds it. Callers MUST pair every
    /// successful acquire with <see cref="ReleaseLocalVideoOutputForPlayback"/>.
    /// </summary>
    internal IVideoOutput? TryAcquireLocalVideoOutputForPlayback(OutputLineViewModel line)
    {
        if (!_localPreviews.TryGetValue(line, out var rt))
            return null;
        return rt.AcquireForPlayback();
    }

    /// <summary>Releases a output acquired via <see cref="TryAcquireLocalVideoOutputForPlayback"/> and resets it
    /// to the idle preview frame so the window keeps showing something.</summary>
    internal void ReleaseLocalVideoOutputForPlayback(OutputLineViewModel line)
    {
        if (!_localPreviews.TryGetValue(line, out var rt))
            return;
        rt.ReleaseFromPlayback();
    }

    /// <summary>Forwards optional hold-image size overrides to local preview runtimes.
    /// Current runtime policy keeps window dimensions stable, so this is presently a no-op.</summary>
    internal void ApplyHoldImageWindowSize(OutputLineViewModel line, int? width, int? height)
    {
        if (_localPreviews.TryGetValue(line, out var rt))
            rt.ApplyHoldImageWindowSize(width, height);
    }

    private void StopPortAudioOutput(OutputLineViewModel line)
    {
        PortAudioOutputRuntime? rt;
        lock (_portAudioOutputsGate)
        {
            if (!_portAudioOutputs.Remove(line, out rt))
                return;
        }

        try { rt.Dispose(); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Returns the persistent audio output for the line so a playback session can route audio into the
    /// already-open stream. Returns <c>null</c> if the line isn't an audio output, the runtime
    /// isn't started yet, or another session already holds it. Callers MUST pair every successful acquire
    /// with <see cref="ReleasePortAudioForPlayback"/>.
    /// </summary>
    internal IAudioOutput? TryAcquirePortAudioForPlayback(OutputLineViewModel line, bool liveMonitoring = false)
    {
        PortAudioOutputRuntime? rt;
        lock (_portAudioOutputsGate)
        {
            if (!_portAudioOutputs.TryGetValue(line, out rt))
                return null;
        }

        return rt.AcquireForPlayback(liveMonitoring);
    }

    /// <summary>Releases the acquirer hold added by <see cref="TryAcquirePortAudioForPlayback"/>.</summary>
    internal void ReleasePortAudioForPlayback(OutputLineViewModel line)
    {
        PortAudioOutputRuntime? rt;
        lock (_portAudioOutputsGate)
        {
            if (!_portAudioOutputs.TryGetValue(line, out rt))
                return;
        }

        rt.ReleaseFromPlayback();
    }

    private void StopNDIOutput(OutputLineViewModel line)
    {
        NDIOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.Remove(line, out rt))
                return;
        }

        try
        {
            rt.Dispose();
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>
    /// Pauses only the carrier sides playback actually needs and returns the live <see cref="NDIOutput"/>
    /// so the playback session can wire onto the existing sender. Other carrier sides keep emitting
    /// (e.g. audio-only file on a VideoAndAudio NDI: carrier video stays running). Returns <c>null</c>
    /// when no carrier is running, when neither side is requested, or when another acquirer holds one of
    /// the requested sides. Callers MUST pair every successful acquire with <see cref="ReleaseNDICarrierForPlayback"/>.
    /// </summary>
    internal NDIOutput? TryAcquireNDICarrierForPlayback(OutputLineViewModel line, bool needsVideo, bool needsAudio)
    {
        NDIOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
                return null;
        }

        return rt.AcquireForPlayback(needsVideo, needsAudio);
    }

    /// <summary>Resumes the carrier sides paused by <see cref="TryAcquireNDICarrierForPlayback"/>.</summary>
    internal void ReleaseNDICarrierForPlayback(
        OutputLineViewModel line,
        bool releaseVideo = true,
        bool releaseAudio = true)
    {
        NDIOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
                return;
        }

        rt.ReleaseFromPlayback(releaseVideo, releaseAudio);
    }

    /// <summary>Installs a logo template on every NDI carrier (idle-slate path). Pass <c>null</c> to revert to black.</summary>
    internal void SetNDICarrierLogo(OutputLineViewModel line, VideoFrame? logoFrame)
    {
        NDIOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
            {
                logoFrame?.Dispose();
                return;
            }
        }

        rt.SetLogoTemplate(logoFrame);
    }

    /// <summary>
    /// Phase B (§3.2) — opens the same dialog used to Add, pre-populated with the line's current
    /// definition. On Save: optionally prompts when playback is actively using the line, then calls
    /// <see cref="ReconfigureLineAsync"/> to apply the change hot.
    /// </summary>
    public async Task EditLineAsync(OutputLineViewModel line, CancellationToken cancellationToken = default)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        OutputDefinition? edited = line.Definition switch
        {
            PortAudioOutputDefinition pa => await ShowEditPortAudioAsync(owner, pa),
            LocalVideoOutputDefinition lv => await ShowEditLocalVideoAsync(owner, lv),
            NDIOutputDefinition nd => await ShowEditNDIAsync(owner, nd),
            _ => null,
        };

        if (edited is null)
            return; // user cancelled

        if (edited.Equals(line.Definition))
            return; // no changes

        // §3.6: confirm only when a player is *actively playing through* this line. The operator chooses
        // whether to keep playing (apply live — no route teardown, best for cosmetic changes like
        // Always-on-top) or reconnect the output (brief black-frame, for structural changes).
        var detachRoutes = true;
        if (PlaybackUsageProbe?.Invoke(line) == true)
        {
            switch (await ConfirmGlitchyEditAsync(owner, line))
            {
                case OutputEditApplyMode.Cancel:
                    return;
                case OutputEditApplyMode.Live:
                    detachRoutes = false;
                    break;
                // OutputEditApplyMode.Reconnect keeps the default detachRoutes = true.
            }
        }

        try
        {
            using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "OutputManagement.EditSelectedAsync.ReconfigureLineAsync", slowWarningMs: 2000);
            await ReconfigureLineAsync(line, edited, cancellationToken, detachRoutes).ConfigureAwait(false);
            timing?.SetOutcome($"line={line.Definition.DisplayName}");
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "ReconfigureLineAsync failed for line={Name} kind={Kind}", line.Definition.DisplayName, line.Definition.Kind);
        }
    }

    private async Task<PortAudioOutputDefinition?> ShowEditPortAudioAsync(Window owner, PortAudioOutputDefinition pa)
    {
        var vm = new AddPortAudioOutputDialogViewModel();
        vm.LoadFromExisting(pa);
        vm.InitializeExistingOutputNames(ExistingOutputNames(pa.Id));
        var dlg = new AddPortAudioOutputDialog { DataContext = vm };
        return await dlg.ShowDialog<PortAudioOutputDefinition?>(owner);
    }

    private async Task<LocalVideoOutputDefinition?> ShowEditLocalVideoAsync(Window owner, LocalVideoOutputDefinition lv)
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        vm.InitializeScreens(owner.Screens.All);
        // For Edit: exclude the line itself from the clone parent list (a line can't be its own parent).
        var editingLine = Outputs.FirstOrDefault(o => o.Definition.Id == lv.Id);
        vm.InitializeCloneParents(GetPotentialCloneParents(editingLine));
        vm.LoadFromExisting(lv);
        vm.InitializeExistingOutputNames(ExistingOutputNames(lv.Id));
        var dlg = new AddLocalVideoOutputDialog { DataContext = vm };
        return await dlg.ShowDialog<LocalVideoOutputDefinition?>(owner);
    }

    private async Task<NDIOutputDefinition?> ShowEditNDIAsync(Window owner, NDIOutputDefinition nd)
    {
        var vm = new AddNDIOutputDialogViewModel();
        vm.LoadFromExisting(nd);
        vm.InitializeExistingOutputNames(ExistingOutputNames(nd.Id));
        var dlg = new AddNDIOutputDialog { DataContext = vm };
        return await dlg.ShowDialog<NDIOutputDefinition?>(owner);
    }

    /// <summary>Operator's answer to the "output in use" edit prompt.</summary>
    private enum OutputEditApplyMode
    {
        /// <summary>Abort the edit.</summary>
        Cancel,

        /// <summary>Detach the route, reconfigure, re-attach — brief black-frame, consistent route.</summary>
        Reconnect,

        /// <summary>Apply live under the still-attached route — no interruption for cosmetic changes.</summary>
        Live,
    }

    private static async Task<OutputEditApplyMode> ConfirmGlitchyEditAsync(Window owner, OutputLineViewModel line)
    {
        var dlg = new Window
        {
            Title = Strings.OutputEditInUseDialogTitle,
            Width = 520,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        // "Save & continue" (apply live) is the default — most in-use edits are cosmetic (Always-on-top,
        // window placement) and shouldn't interrupt playback. "Reconnect" is the deliberate choice for a
        // structural change that needs the route re-established.
        var continueLive = new Button { Content = Strings.OutputEditInUseContinueButton, IsDefault = true };
        var reconnect = new Button { Content = Strings.OutputEditInUseApplyButton };
        var cancel = new Button { Content = Strings.CancelButton, IsCancel = true };

        var tcs = new TaskCompletionSource<OutputEditApplyMode>();
        continueLive.Click += (_, _) => { tcs.TrySetResult(OutputEditApplyMode.Live); dlg.Close(); };
        reconnect.Click += (_, _) => { tcs.TrySetResult(OutputEditApplyMode.Reconnect); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(OutputEditApplyMode.Cancel); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(OutputEditApplyMode.Cancel);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(reconnect);
        buttons.Children.Add(continueLive);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var message = new TextBlock
        {
            Text = Strings.Format(nameof(Strings.OutputEditInUseDialogMessageFormat), line.Definition.DisplayName),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        root.Children.Add(buttons);
        root.Children.Add(message);
        dlg.Content = root;

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    [RelayCommand]
    private async Task AddPortAudioAsync(CancellationToken cancellationToken)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddPortAudioOutputDialogViewModel();
        vm.InitializeExistingOutputNames(ExistingOutputNames());
        vm.ReloadHostApis();
        var dlg = new AddPortAudioOutputDialog { DataContext = vm };
        var result = await dlg.ShowDialog<PortAudioOutputDefinition?>(owner);
        if (result is null)
            return;

        var line = new OutputLineViewModel(result, Remove, this);
        Outputs.Add(line);

        // Open the PortAudio stream once per line and keep it running for the lifetime of the entry.
        // Subsequent playback sessions acquire this output instead of opening a fresh one each time.
        try
        {
            var runtime = await Task.Run(() =>
            {
                return StartPortAudioRuntime(result);
            }, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!runtime.Definition.Equals(line.Definition))
                    line.ReplaceDefinition(runtime.Definition);
                lock (_portAudioOutputsGate)
                    _portAudioOutputs[line] = runtime;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Outputs.Remove(line));
            Trace.LogError(ex, "Failed to start PortAudio output: name={Name} device={DeviceName} channels={Channels} sampleRate={SampleRate}",
                result.DisplayName, result.DeviceName, result.ChannelCount, result.SampleRate);
        }
    }

    [RelayCommand]
    private async Task AddLocalVideoAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddLocalVideoOutputDialogViewModel();
        vm.InitializeExistingOutputNames(ExistingOutputNames());
        vm.InitializeScreens(owner.Screens.All);
        vm.InitializeCloneParents(GetPotentialCloneParents());
        var dlg = new AddLocalVideoOutputDialog { DataContext = vm };
        var result = await dlg.ShowDialog<LocalVideoOutputDefinition?>(owner);
        if (result is null)
            return;

        var line = new OutputLineViewModel(result, Remove, this);
        Outputs.Add(line);

        try
        {
            await StartLocalPreviewAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "Failed to open local video preview: name={Name} engine={Engine} surfaceMode={SurfaceMode} windowWidth={WindowWidth} windowHeight={WindowHeight}",
                result.DisplayName, result.Engine, result.SurfaceMode, result.WindowWidth, result.WindowHeight);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddNDI))]
    private async Task AddNDIAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddNDIOutputDialogViewModel();
        vm.InitializeExistingOutputNames(ExistingOutputNames());
        var dlg = new AddNDIOutputDialog { DataContext = vm };
        var result = await dlg.ShowDialog<NDIOutputDefinition?>(owner);
        if (result is null)
            return;

        var line = new OutputLineViewModel(result, Remove, this);
        Outputs.Add(line);
        try
        {
            var runtime = await Task.Run(() =>
            {
                var r = new NDIOutputPreviewRuntime(result);
                r.Start();
                return r;
            }, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_ndiOutputsGate)
                    _ndiOutputs[line] = runtime;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Outputs.Remove(line));
            Trace.LogError(ex, "Failed to start NDI output: name={Name} source={SourceName} streamMode={StreamMode}",
                result.DisplayName, result.SourceName, result.StreamMode);
        }
    }

    private bool CanAddNDI() => IsNDIAvailable;

    [RelayCommand]
    private void ClearHealth()
    {
        // The ShowSession health probes read cumulative session counters (composition stats / audio pumps);
        // clearing here just resets the panel's displayed state — the counters re-accumulate on the next poll.
        foreach (var line in Outputs)
        {
            line.Health = OutputLineHealthState.Unknown;
            line.HealthDetail = null;
            line.ResetSparkline();
        }

        RefreshOutputHealth();
    }

    private void RefreshOutputHealth()
    {
        var players = ActivePlayersProbe?.Invoke() ?? [];
        var cueProbe = CueLineMetricsProbe;

        foreach (var line in Outputs)
        {
            var worst = OutputLineHealthState.Unknown;
            string? detail = null;
            // §8.1 — sum throughput across all players driving this line. A line wired to two players
            // would otherwise miss the second player's contribution.
            long videoSubmittedTotal = 0;
            long audioEnqueuedTotal = 0;
            var anyWired = false;
            foreach (var player in players)
            {
                // Each deck drives lines through its per-player ShowSession (the only runtime since the
                // legacy engine was deleted) — read the session-side composition/audio-pump health.
                if (player.TryGetShowSessionLineHealthMetrics(line.Definition.Id) is not { } metrics)
                    continue;
                var metricsDetail = FormatShowSessionHealthDetail(metrics);

                if (metrics.State != OutputLineHealthState.Unknown)
                {
                    anyWired = true;
                    videoSubmittedTotal += metrics.VideoSubmitted;
                    audioEnqueuedTotal += metrics.AudioEnqueued;
                }
                if (metrics.State > worst)
                {
                    worst = metrics.State;
                    detail = metricsDetail;
                }
            }

            // Cue-engine playback drives lines outside any player session — without this the panel
            // showed "Idle" (no LED, no stats) during cue playback.
            if (cueProbe?.Invoke(line.Definition.Id) is { State: not OutputLineHealthState.Unknown } cue)
            {
                anyWired = true;
                videoSubmittedTotal += cue.VideoSubmitted;
                audioEnqueuedTotal += cue.AudioEnqueued;
                if (cue.State > worst)
                {
                    worst = cue.State;
                    detail = FormatCueHealthDetail(cue);
                }
            }

            line.Health = worst;
            line.HealthDetail = detail;
            if (anyWired)
            {
                line.RecordSparklineSample(videoSubmittedTotal, audioEnqueuedTotal);
                // P2 stats line: cumulative delivery counters next to the sparkline. Same 1 Hz tick
                // as the health poll — no extra polling cost.
                line.StatsSummary = (videoSubmittedTotal, audioEnqueuedTotal) switch
                {
                    (> 0, > 0) => $"{videoSubmittedTotal:N0} f · {audioEnqueuedTotal:N0} ch",
                    (> 0, _) => $"{videoSubmittedTotal:N0} f",
                    (_, > 0) => $"{audioEnqueuedTotal:N0} ch",
                    _ => null,
                };
            }
            else
            {
                line.ResetSparkline();
                line.StatsSummary = null;
            }
        }

        var active = 0;
        var warn = 0;
        var err = 0;
        foreach (var line in Outputs)
        {
            switch (line.Health)
            {
                case OutputLineHealthState.Healthy: active++; break;
                case OutputLineHealthState.Warning: active++; warn++; break;
                case OutputLineHealthState.Error: active++; err++; break;
            }
        }
        AggregateActiveCount = active;
        AggregateWarningCount = warn;
        AggregateErrorCount = err;
    }

    private static string FormatCueHealthDetail(Playback.OutputLineHealthEvaluator.LineHealthMetrics m) =>
        m.State == OutputLineHealthState.Healthy
            ? $"Cues: {m.VideoSubmitted:N0} f · {m.AudioEnqueued:N0} ch"
            : $"Cues: {m.VideoDropped + m.AudioDropped:N0} drops of {m.VideoSubmitted + m.AudioEnqueued:N0} delivered";

    private static string FormatShowSessionHealthDetail(Playback.OutputLineHealthEvaluator.LineHealthMetrics m) =>
        m.State == OutputLineHealthState.Healthy
            ? (m.VideoSubmitted, m.AudioEnqueued) switch
            {
                (> 0, > 0) => $"Player: {m.VideoSubmitted:N0} f · {m.AudioEnqueued:N0} ch",
                (_, > 0) => $"Player: {m.AudioEnqueued:N0} ch",
                _ => $"Player: {m.VideoSubmitted:N0} f",
            }
            : $"Player: {m.VideoDropped + m.AudioDropped:N0} drops of {m.VideoSubmitted + m.AudioEnqueued:N0} delivered";
}
