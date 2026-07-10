using Xunit;

namespace S.Media.Players.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS (rather than fails) unless timing-sensitive tests are opted into
/// via <c>MFP_TIMING_TESTS=1</c>. These assert wall-clock/scheduling behaviour - e.g. many per-clip player
/// threads all staying scheduled within a soak window - which an oversubscribed CI VM cannot deliver (the
/// testhost has hung under 30-57x slowdown regardless of thread count). So, matching the repo's LiveFact /
/// FFmpegNativeFact / HaPlay.Tests.TimingFact convention, they must never gate CI. They stay fully runnable
/// locally and can be switched on in CI deliberately (set MFP_TIMING_TESTS=1).
/// </summary>
public sealed class TimingFactAttribute : FactAttribute
{
    public TimingFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("MFP_TIMING_TESTS") != "1")
            Skip = "timing-sensitive test is opt-in on CI: set MFP_TIMING_TESTS=1";
    }
}
