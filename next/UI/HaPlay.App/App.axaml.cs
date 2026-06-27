using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HaPlay.Core;
using S.Media.Core.Registry;
using S.Media.Session;

namespace HaPlay.App;

public partial class App : Application
{
    private const string EmptyShow =
        "{\"Version\":1,\"Cues\":[],\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Headless session — the UI process drives transport/composition without owning an audio device.
            // Real decoders / audio-out get wired in as media-loading workspaces strangle over from the old app.
            var registry = MediaRegistry.Build(_ => { });
            var vm = new ShowSessionViewModel(new ShowSession(registry));
            vm.LoadShow(EmptyShow);

            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => _ = vm.DisposeAsync();

            // Headless dev/CI smoke (HAPLAY_SMOKE set): render, then exit cleanly so the app can be gated under xvfb.
            if (Environment.GetEnvironmentVariable("HAPLAY_SMOKE") is not null)
                DispatcherTimer.RunOnce(() => desktop.Shutdown(0), TimeSpan.FromSeconds(2));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
