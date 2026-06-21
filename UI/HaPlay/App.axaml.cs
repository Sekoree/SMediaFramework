using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
#if DEBUG
using Avalonia.Diagnostics;
#endif
using Avalonia.Markup.Xaml;
using HaPlay.ViewModels;
using HaPlay.Views;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.MiniAudio;
using S.Media.PortAudio;

namespace HaPlay;

public partial class App : Application
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.App");

    public override void Initialize()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "App.Initialize", slowWarningMs: 250);
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
        timing?.SetOutcome("xaml-loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "App.OnFrameworkInitializationCompleted", slowWarningMs: 1000);
        InitializeMediaFramework();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Trace.LogDebug("App lifetime: classic desktop");
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            Trace.LogDebug("App lifetime: activity single-view factory");
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            Trace.LogDebug("App lifetime: single-view");
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
        timing?.SetOutcome($"lifetime={ApplicationLifetime?.GetType().Name ?? "<none>"}");
    }

    /// <summary>
    /// Registers framework plugins once at startup so file/stream source factories, audio backends, and the
    /// adaptive-rate output wrapper are wired on <see cref="MediaFrameworkPlugins"/>. The wrapper backs
    /// <see cref="S.Media.Core.Audio.AudioRouter.EnableAdaptiveRateOnNonMasterOutputs"/> (multi-output
    /// drift correction); without this call that method throws. Idempotent and best-effort — a failure
    /// degrades to prior behaviour rather than blocking startup.
    /// </summary>
    private static void InitializeMediaFramework()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "App.InitializeMediaFramework", slowWarningMs: 1000);
        var builder = MediaFrameworkRuntime.Init();
        try
        {
            builder.UseFFmpeg();
        }
        catch (System.Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay media framework init failed during UseFFmpeg; continuing with degraded plugin availability");
        }

        try
        {
            builder.UsePortAudio();
        }
        catch (System.Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay media framework init failed during UsePortAudio; PortAudio devices will be unavailable");
        }

        try
        {
            builder.UseMiniAudio();
        }
        catch (System.Exception ex)
        {
            Trace.LogWarning(ex, "HaPlay media framework init failed during UseMiniAudio; miniaudio devices will be unavailable");
        }

        timing?.SetOutcome($"audio-backends={string.Join(",", AudioBackends.All.Select(b => b.Name))}");
    }
}
