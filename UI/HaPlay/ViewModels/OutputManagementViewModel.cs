using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.OutputPreview;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.PortAudio;

namespace HaPlay.ViewModels;

public partial class OutputManagementViewModel : ViewModelBase
{
    public ObservableCollection<OutputLineViewModel> Outputs { get; } = new();

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

    public OutputManagementViewModel()
    {
        Outputs.CollectionChanged += (_, _) => RoutingTopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseTopologyChanged() => RoutingTopologyChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Phase B follow-up — raised *before* a line's runtime is torn down so any active
    /// <c>HaPlayPlaybackSession</c> can call <c>TryRemoveOutput</c> first and avoid Submit'ing to a
    /// disposed sink. Subscribers must run synchronously: by the time the event returns, the runtime
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
        var errors = new List<string>();
        foreach (var line in Outputs.ToList())
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                errors.Add($"{line.Definition.DisplayName}: {ex.Message}");
                Debug.WriteLine($"HaPlay: failed to start loaded output '{line.Definition.DisplayName}': {ex}");
            }
        }

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
            var rt = new PortAudioOutputRuntime(definition);
            rt.Start();
            return rt;
        }, cancellationToken).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_portAudioOutputsGate)
                _portAudioOutputs[line] = runtime;
        });
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
    /// line. The session will see a brief silence/black-frame window; orchestration (UI confirm prompts,
    /// session re-acquire) is the Phase B caller's concern.
    /// </remarks>
    public async Task ReconfigureLineAsync(
        OutputLineViewModel line,
        OutputDefinition newDefinition,
        CancellationToken cancellationToken = default)
    {
        if (newDefinition.Id != line.Definition.Id)
            throw new ArgumentException(
                $"newDefinition.Id ({newDefinition.Id}) must match line.Definition.Id ({line.Definition.Id}).",
                nameof(newDefinition));
        if (newDefinition.GetType() != line.Definition.GetType())
            throw new ArgumentException(
                $"Cannot reconfigure {line.Definition.GetType().Name} with {newDefinition.GetType().Name} — output kind is immutable.",
                nameof(newDefinition));

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

    internal void NotifyLocalPreviewEnded(OutputLineViewModel line)
    {
        _localPreviews.Remove(line, out _);
        line.IsPreviewRunning = false;
    }

    public async Task StartLocalPreviewAsync(OutputLineViewModel line, CancellationToken cancellationToken = default)
    {
        if (line.Definition is not LocalVideoOutputDefinition d)
            return;
        if (_localPreviews.ContainsKey(line))
            return;

        ILocalVideoPreviewRuntime runtime = d.Engine == VideoOutputEngine.SdlOpenGl
            ? new SdlLocalVideoPreviewRuntime(d, line, this)
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
        // existing sink via TryAcquireLocalVideoSinkForPlayback / TryAcquireNDICarrierForPlayback so the
        // window doesn't flash on each media change. Kept for API stability; intentional no-op.
        _ = lines;
    }

    /// <summary>
    /// Returns the persistent local-video sink (SDL or Avalonia) so a playback session can route decoded
    /// frames into the existing window. Returns <c>null</c> when the line isn't a local-video output,
    /// the preview isn't running, or another playback session already holds it. Callers MUST pair every
    /// successful acquire with <see cref="ReleaseLocalVideoSinkForPlayback"/>.
    /// </summary>
    internal IVideoSink? TryAcquireLocalVideoSinkForPlayback(OutputLineViewModel line)
    {
        if (!_localPreviews.TryGetValue(line, out var rt))
            return null;
        return rt.AcquireForPlayback();
    }

    /// <summary>Releases a sink acquired via <see cref="TryAcquireLocalVideoSinkForPlayback"/> and resets it
    /// to the idle preview frame so the window keeps showing something.</summary>
    internal void ReleaseLocalVideoSinkForPlayback(OutputLineViewModel line)
    {
        if (!_localPreviews.TryGetValue(line, out var rt))
            return;
        rt.ReleaseFromPlayback();
    }

    /// <summary>Phase 3 — resize the local video window to a hold-image's native dimensions
    /// (or restore the user's chosen size when both args are null).</summary>
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
    /// Returns the persistent <see cref="PortAudioOutput"/> for the line so a playback session can route
    /// audio into the already-open stream. Returns <c>null</c> if the line isn't PortAudio, the runtime
    /// isn't started yet, or another session already holds it. Callers MUST pair every successful acquire
    /// with <see cref="ReleasePortAudioForPlayback"/>.
    /// </summary>
    internal PortAudioOutput? TryAcquirePortAudioForPlayback(OutputLineViewModel line)
    {
        PortAudioOutputRuntime? rt;
        lock (_portAudioOutputsGate)
        {
            if (!_portAudioOutputs.TryGetValue(line, out rt))
                return null;
        }

        return rt.AcquireForPlayback();
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

    /// <summary>Resumes the carrier paused by <see cref="TryAcquireNDICarrierForPlayback"/>.</summary>
    internal void ReleaseNDICarrierForPlayback(OutputLineViewModel line)
    {
        NDIOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
                return;
        }

        rt.ReleaseFromPlayback();
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

        // §3.6: confirm only when a player is *actively playing through* this line.
        if (PlaybackUsageProbe?.Invoke(line) == true)
        {
            var confirmed = await ConfirmGlitchyEditAsync(owner, line);
            if (!confirmed)
                return;
        }

        try
        {
            await ReconfigureLineAsync(line, edited, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HaPlay: ReconfigureLineAsync failed for '{line.Definition.DisplayName}': {ex}");
        }
    }

    private static async Task<PortAudioOutputDefinition?> ShowEditPortAudioAsync(Window owner, PortAudioOutputDefinition pa)
    {
        var vm = new AddPortAudioOutputDialogViewModel();
        vm.LoadFromExisting(pa);
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
        var dlg = new AddLocalVideoOutputDialog { DataContext = vm };
        return await dlg.ShowDialog<LocalVideoOutputDefinition?>(owner);
    }

    private static async Task<NDIOutputDefinition?> ShowEditNDIAsync(Window owner, NDIOutputDefinition nd)
    {
        var vm = new AddNDIOutputDialogViewModel();
        vm.LoadFromExisting(nd);
        var dlg = new AddNDIOutputDialog { DataContext = vm };
        return await dlg.ShowDialog<NDIOutputDefinition?>(owner);
    }

    private static async Task<bool> ConfirmGlitchyEditAsync(Window owner, OutputLineViewModel line)
    {
        var dlg = new Window
        {
            Title = "Apply edit while output is in use?",
            Width = 480,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Apply (brief glitch)", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

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
            Text =
                $"'{line.Definition.DisplayName}' is currently delivering audio/video to a running player. " +
                "Applying the edit will cause a brief silence / black-frame on that output (typically 1–2 frames). " +
                "Continue?",
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
                var rt = new PortAudioOutputRuntime(result);
                rt.Start();
                return rt;
            }, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_portAudioOutputsGate)
                    _portAudioOutputs[line] = runtime;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Outputs.Remove(line));
            Debug.WriteLine($"HaPlay: failed to start PortAudio output '{result.DisplayName}': {ex}");
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
            Debug.WriteLine($"HaPlay: failed to open preview for '{result.DisplayName}': {ex}");
        }
    }

    [RelayCommand]
    private async Task AddNDIAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddNDIOutputDialogViewModel();
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
            Debug.WriteLine($"HaPlay: failed to start NDI output '{result.SourceName}': {ex}");
        }
    }
}
