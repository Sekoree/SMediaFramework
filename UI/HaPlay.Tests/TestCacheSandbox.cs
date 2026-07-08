using System.Runtime.CompilerServices;

namespace HaPlay.Tests;

internal static class TestCacheSandbox
{
    /// <summary>
    /// Redirects HaPlay's per-machine cache root (via <c>HAPLAY_CACHE_ROOT</c>) into a throwaway temp directory
    /// for the whole test run. Constructing a <c>MainViewModel</c> spins up session recovery, which creates a
    /// folder under the cache root; without this, every such test would litter the real user cache. Runs once at
    /// assembly load, before any test executes.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("HAPLAY_CACHE_ROOT") is null or "")
        {
            var dir = Path.Combine(Path.GetTempPath(), "haplay-test-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("HAPLAY_CACHE_ROOT", dir);
        }
        // MainViewModel-heavy tests explicitly exercise flush/recovery where relevant. Disabling its recurring
        // dispatcher timer prevents hundreds of short-lived test VMs being retained by timer event handlers.
        Environment.SetEnvironmentVariable("HAPLAY_DISABLE_RECOVERY_TIMER", "1");
    }
}
