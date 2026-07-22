using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HaViz.Desktop.ViewModels;
using HaViz.Desktop.Views;

namespace HaViz.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Engine/NDI must not outlive the window: dispose on shutdown so the sender
            // disappears from receivers instead of lingering until process exit.
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
