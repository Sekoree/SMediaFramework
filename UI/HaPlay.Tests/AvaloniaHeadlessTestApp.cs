using Avalonia;
using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(HaPlay.Tests.AvaloniaHeadlessTestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
