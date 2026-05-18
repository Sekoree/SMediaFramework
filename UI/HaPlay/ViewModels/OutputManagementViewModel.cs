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

    private void Remove(OutputLineViewModel line)
    {
        StopLocalPreview(line);
        StopNDIOutput(line);
        StopPortAudioOutput(line);
        Outputs.Remove(line);
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
