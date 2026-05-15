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

namespace HaPlay.ViewModels;

public partial class OutputManagementViewModel : ViewModelBase
{
    public ObservableCollection<OutputLineViewModel> Outputs { get; } = new();

    private readonly Dictionary<OutputLineViewModel, ILocalVideoPreviewRuntime> _localPreviews = new();
    private readonly Dictionary<OutputLineViewModel, NdiOutputPreviewRuntime> _ndiOutputs = new();
    private readonly Lock _ndiOutputsGate = new();

    private void Remove(OutputLineViewModel line)
    {
        StopLocalPreview(line);
        StopNdiOutput(line);
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
        // NDI carriers stay alive across playback — playback acquires the existing sender via
        // TryAcquireNdiCarrier so receivers don't have to re-discover. Only local previews are torn down here.
        foreach (var line in lines)
            StopLocalPreview(line);
    }

    private void StopNdiOutput(OutputLineViewModel line)
    {
        NdiOutputPreviewRuntime? rt;
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
    /// the requested sides. Callers MUST pair every successful acquire with <see cref="ReleaseNdiCarrierForPlayback"/>.
    /// </summary>
    internal NDIOutput? TryAcquireNdiCarrierForPlayback(OutputLineViewModel line, bool needsVideo, bool needsAudio)
    {
        NdiOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
                return null;
        }

        return rt.AcquireForPlayback(needsVideo, needsAudio);
    }

    /// <summary>Resumes the carrier paused by <see cref="TryAcquireNdiCarrierForPlayback"/>.</summary>
    internal void ReleaseNdiCarrierForPlayback(OutputLineViewModel line)
    {
        NdiOutputPreviewRuntime? rt;
        lock (_ndiOutputsGate)
        {
            if (!_ndiOutputs.TryGetValue(line, out rt))
                return;
        }

        rt.ReleaseFromPlayback();
    }

    /// <summary>Installs a logo template on every NDI carrier (idle-slate path). Pass <c>null</c> to revert to black.</summary>
    internal void SetNdiCarrierLogo(OutputLineViewModel line, VideoFrame? logoFrame)
    {
        NdiOutputPreviewRuntime? rt;
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
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddPortAudioOutputDialogViewModel();
        vm.ReloadHostApis();
        var dlg = new AddPortAudioOutputDialog { DataContext = vm };
        var result = await dlg.ShowDialog<PortAudioOutputDefinition?>(owner);
        if (result is not null)
            Outputs.Add(new OutputLineViewModel(result, Remove, this));
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
        if (result is not null)
            Outputs.Add(new OutputLineViewModel(result, Remove, this));
    }

    [RelayCommand]
    private async Task AddNdiAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new AddNdiOutputDialogViewModel();
        var dlg = new AddNdiOutputDialog { DataContext = vm };
        var result = await dlg.ShowDialog<NdiOutputDefinition?>(owner);
        if (result is null)
            return;

        var line = new OutputLineViewModel(result, Remove, this);
        Outputs.Add(line);
        try
        {
            var runtime = await Task.Run(() =>
            {
                var r = new NdiOutputPreviewRuntime(result);
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
