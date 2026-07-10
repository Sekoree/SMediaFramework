using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS (rather than fails) unless timing-sensitive tests are opted into
/// via <c>MFP_TIMING_TESTS=1</c>. These assert wall-clock/real-time behaviour - e.g. a timer firing at an
/// approximate rate - which an oversubscribed CI VM cannot deliver even at generous tolerances (a ~300 ms
/// window has been observed stretching to ~50 s, during which the timer thread was too starved to fire). So,
/// matching the repo's LiveFact / FFmpegNativeFact / HaPlay.Tests.TimingFact convention, they must never gate
/// CI. They stay fully runnable locally and can be switched on in CI deliberately (set MFP_TIMING_TESTS=1).
/// </summary>
public sealed class TimingFactAttribute : FactAttribute
{
    public TimingFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("MFP_TIMING_TESTS") != "1")
            Skip = "timing-sensitive test is opt-in on CI: set MFP_TIMING_TESTS=1";
    }
}
