using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(HaPlay.Tests.AvaloniaHeadlessTestApp))]

namespace HaPlay.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

internal sealed class TestApp : Application
{
}
