using Avalonia;

namespace HaPlay.Desktop;

internal static class Program
{
    // Avalonia needs an STA thread; do nothing before BuildAvaloniaApp (no SynchronizationContext yet).
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::HaPlay.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
