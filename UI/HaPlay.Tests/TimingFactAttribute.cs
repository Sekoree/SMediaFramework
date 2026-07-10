using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) unless timing-sensitive integration
/// tests are explicitly opted into via <c>MFP_TIMING_TESTS=1</c>. These tests drive real thread-pool hops
/// (<c>Task.Run</c> → <c>Dispatcher.InvokeAsync</c>) and assert that a status update lands within a pumped
/// window. On an oversubscribed CI VM that window is occasionally missed even at a very generous timeout, so
/// - matching the repo's <c>LiveFact</c>/<c>FFmpegNativeFact</c> convention - they must never gate CI. They
/// remain fully runnable locally (and can be switched on in CI deliberately). See project_flaky_timing_tests.
/// </summary>
public sealed class TimingFactAttribute : FactAttribute
{
    public TimingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MFP_TIMING_TESTS") != "1")
            Skip = "timing-sensitive integration test is opt-in: set MFP_TIMING_TESTS=1";
    }
}
