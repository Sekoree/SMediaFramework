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
        RegisterDockPaneTemplates();
#if DEBUG
        this.AttachDeveloperTools();
#endif
        timing?.SetOutcome("xaml-loaded");
    }

    // Maps the Control workspace dock panes to their views - see ControlDockPaneTemplates for why they're
    // registered in code rather than App.axaml.
    private void RegisterDockPaneTemplates()
    {
        foreach (var template in Views.ControlPanes.ControlDockPaneTemplates.Create())
            DataTemplates.Add(template);
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
            // At shutdown, tear down the ShowSession cue re-back first, then dispose the media host so the
            // modules' native runtime holds are released deterministically (NXT-05 - sessions that borrow the
            // registry must go first; the host is the last thing out). Teardown belongs on Exit: doing it during
            // ShutdownRequested can dispose recovery/media state before MainWindow.Closing has a chance to cancel
            // shutdown for unsaved work. Forced Shutdown() still raises Exit.
            var toreDown = 0;
            void Teardown()
            {
                if (System.Threading.Interlocked.Exchange(ref toreDown, 1) != 0)
                    return;
                mainVm.ShutdownCleanup();
                MediaRuntime.Shutdown();
            }

            desktop.Exit += (_, _) => Teardown();

            WireSmokeSelfExit(desktop);
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
    /// CI launch gate (NXT-15): with <c>HAPLAY_SMOKE=1</c> the app renders its first real frame and then
    /// shuts itself down through the NORMAL teardown path (ShutdownRequested → ShowSession cleanup →
    /// MediaRuntime.Shutdown), so the smoke gates startup wiring AND clean exit, not just "a process ran".
    /// Exit 0 = frame rendered + clean shutdown; a watchdog hard-exits 2 when no frame appears in time so a
    /// wedged launch fails the gate instead of hanging the runner.
    /// </summary>
    private static void WireSmokeSelfExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var smoke = System.Environment.GetEnvironmentVariable("HAPLAY_SMOKE");
        if (smoke is not ("1" or "true"))
            return;

        var exited = 0;
        desktop.MainWindow!.Opened += (_, _) =>
            // RequestAnimationFrame fires after a compositor frame actually renders - the "a real frame was
            // drawn" signal the old rebuild app's smoke had and the ported app lacked (review NXT-15).
            Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow)?.RequestAnimationFrame(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (System.Threading.Interlocked.Exchange(ref exited, 1) != 0)
                        return;
                    Trace.LogInformation("HAPLAY_SMOKE: first frame rendered - shutting down (exit 0)");
                    // TryShutdown, NOT Shutdown: the forced path skips ShutdownRequested, and the smoke must
                    // exercise the app's real teardown (ShowSession cleanup + MediaRuntime.Shutdown).
                    desktop.TryShutdown(0);
                }));

        _ = System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(45)).ContinueWith(_ =>
        {
            if (System.Threading.Interlocked.Exchange(ref exited, 1) != 0)
                return;
            Trace.LogError("HAPLAY_SMOKE: no frame rendered within 45s - hard exit 2");
            System.Environment.Exit(2);
        });
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
