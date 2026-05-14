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

namespace HaPlay.ViewModels;

public partial class OutputManagementViewModel : ViewModelBase
{
    public ObservableCollection<OutputLineViewModel> Outputs { get; } = new();

    private readonly Dictionary<OutputLineViewModel, ILocalVideoPreviewRuntime> _localPreviews = new();
    private readonly Dictionary<OutputLineViewModel, NdiOutputPreviewRuntime> _ndiOutputs = new();

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
        foreach (var line in lines)
        {
            StopLocalPreview(line);
            StopNdiOutput(line);
        }
    }

    private void StopNdiOutput(OutputLineViewModel line)
    {
        if (!_ndiOutputs.Remove(line, out var rt))
            return;
        try
        {
            rt.Dispose();
        }
        catch
        {
            /* best effort */
        }
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

            await Dispatcher.UIThread.InvokeAsync(() => _ndiOutputs[line] = runtime);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Outputs.Remove(line));
            Debug.WriteLine($"HaPlay: failed to start NDI output '{result.SourceName}': {ex}");
        }
    }
}
