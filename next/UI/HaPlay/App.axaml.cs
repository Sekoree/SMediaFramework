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
using S.Media.Decode.FFmpeg;
using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;

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
            var mainVm = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
            // Best-effort teardown of the gated ShowSession cue re-back at shutdown (no-op when disabled).
            desktop.ShutdownRequested += (_, _) => mainVm.ShutdownCleanup();
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
    /// Builds the process-wide <see cref="MediaRuntime.Registry"/> once at startup (the rewritten framework's
    /// composition root: FFmpeg + PortAudio + MiniAudio + NDI modules). Replaces the old static
    /// MediaFrameworkRuntime/MediaFrameworkPlugins plugin setup. Best-effort per module (see MediaRuntime).
    /// </summary>
    private static void InitializeMediaFramework()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "App.InitializeMediaFramework", slowWarningMs: 1000);
        MediaRuntime.Initialize();
        timing?.SetOutcome($"audio-backends={string.Join(",", MediaRuntime.Registry.AudioBackends.Select(b => b.Name))}");
    }
}
