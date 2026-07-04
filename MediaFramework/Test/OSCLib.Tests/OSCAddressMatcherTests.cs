using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public sealed class OSCAddressMatcherTests
{
    [Theory]
    [InlineData("/mixer/*/gain", "/mixer/1/gain")]
    [InlineData("/mixer/?/gain", "/mixer/7/gain")]
    [InlineData("/clip/{start,stop}", "/clip/start")]
    [InlineData("/clip/{start,stop}", "/clip/stop")]
    [InlineData("/deck/[ab]/play", "/deck/a/play")]
    [InlineData("/show//go", "/show/page/scene/go")]
    [InlineData("/show//go", "/show/go")]
    public void IsMatch_MatchesSupportedOSCPatterns(string pattern, string address)
    {
        Assert.True(OSCAddressMatcher.IsMatch(pattern, address));
    }

    [Theory]
    [InlineData("/mixer/?/gain", "/mixer/12/gain")]
    [InlineData("/clip/{start,stop}", "/clip/pause")]
    [InlineData("/deck/[!ab]/play", "/deck/a/play")]
    [InlineData("mixer/*", "/mixer/1")]
    [InlineData("/mixer/*", "mixer/1")]
    public void IsMatch_RejectsNonMatchingPatterns(string pattern, string address)
    {
        Assert.False(OSCAddressMatcher.IsMatch(pattern, address));
    }
}
