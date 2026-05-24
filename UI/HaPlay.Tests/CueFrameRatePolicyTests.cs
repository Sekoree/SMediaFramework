using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueFrameRatePolicyTests
{
    [Fact]
    public void RatesMismatch_Flags23_976Against60()
    {
        Assert.True(CueFrameRatePolicy.RatesMismatch(24000, 1001, 60, 1));
    }

    [Fact]
    public void RatesMismatch_Allows30Into60()
    {
        Assert.False(CueFrameRatePolicy.RatesMismatch(30, 1, 60, 1));
    }
}
