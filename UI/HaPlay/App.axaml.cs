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
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;

namespace HaPlay;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        InitializeMediaFramework();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Registers the FFmpeg framework plugins once at startup so file/stream source factories and the
    /// adaptive-rate output wrapper are wired on <see cref="MediaFrameworkPlugins"/>. The wrapper backs
    /// <see cref="S.Media.Core.Audio.AudioRouter.EnableAdaptiveRateOnNonMasterOutputs"/> (multi-output
    /// drift correction); without this call that method throws. Idempotent and best-effort — a failure
    /// degrades to prior behaviour rather than blocking startup.
    /// </summary>
    private static void InitializeMediaFramework()
    {
        try
        {
            MediaFrameworkRuntime.Init().UseFFmpeg();
        }
        catch (System.Exception ex)
        {
            MediaDiagnostics.LogWarning("HaPlay: media framework init (UseFFmpeg) failed: {0}", ex.Message);
        }
    }
}
