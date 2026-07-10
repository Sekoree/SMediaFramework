using Avalonia.Headless;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("HaPlay.Tests.HeadlessSessionFramework", "HaPlay.Tests")]

namespace HaPlay.Tests;

/// <summary>
/// THE FLAKY-HANG FIX (captured live 2026-07-04 with dotnet-stack + dotnet-dump). Avalonia binds
/// <c>Dispatcher.UIThread</c> to the FIRST thread that touches it, process-wide. When a plain
/// view-model test (whose code calls <c>Dispatcher.UIThread.Post</c>) happened to run before any
/// <see cref="HeadlessUnitTestSession"/>-based test, UIThread bound to an xunit worker thread -
/// and the shared session's first Dispatch then crashed its own loop while initializing the
/// isolated headless app ("The calling thread cannot access this object because a different
/// thread owns it", thrown from <c>DefaultRenderLoop.Add</c> inside
/// <c>EnsureIsolatedApplication</c>). With the session loop dead, every later session Dispatch
/// waited forever - the whole run hung with zero test code on any thread. Test ORDER decides
/// whether it happens (xunit's collection order shifts whenever tests are added/renamed), which
/// is why the hang came and went across code changes.
///
/// The custom test framework warms the session BEFORE any test runs, so the headless app
/// initializes ON the session thread and UIThread belongs to it from the start. (A
/// [ModuleInitializer] cannot do this: the session thread has to call this module's
/// BuildAvaloniaApp, which the loader blocks until the initializer returns - a guaranteed
/// deadlock when the initializer waits on the session.)
/// </summary>
public sealed class HeadlessSessionFramework : XunitTestFramework
{
    public HeadlessSessionFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        // Every MainViewModel a test constructs starts a SessionRecoveryService DispatcherTimer (2 s) and never
        // stops it - the tests don't tear the VM down. Across an assembly run dozens of these accumulate on the
        // ONE shared headless dispatcher, each tick building a project snapshot on the UI thread and firing a
        // Task.Run disk write. That background flood (a) saturates the dispatcher, so UI-thread-dispatch tests
        // wedge until the 4-minute blame-hang kills the host, and (b) churns the disk, so the best-effort
        // File.Copy in AppSettings.Save intermittently loses a share-violation race and silently skips the
        // backup (2026-07-09 win-x64: host hang + AppSettings "Actual: null"). Disable the auto-timer here - it
        // runs before any test, and the recovery LOGIC stays covered by SessionRecoveryTests, which drive
        // CaptureAsync directly on their own service instance rather than through this timer.
        Environment.SetEnvironmentVariable("HAPLAY_DISABLE_RECOVERY_TIMER", "1");

        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSessionFramework).Assembly)
            .Dispatch(static () => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
