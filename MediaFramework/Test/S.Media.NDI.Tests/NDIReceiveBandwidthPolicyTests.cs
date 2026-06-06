using NDILib;
using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDIReceiveBandwidthPolicyTests
{
    [Theory]
    [InlineData(true, true, NDIRecvBandwidth.Highest, NDIRecvBandwidth.Highest)]
    [InlineData(true, false, NDIRecvBandwidth.Highest, NDIRecvBandwidth.AudioOnly)]
    [InlineData(false, true, NDIRecvBandwidth.Highest, NDIRecvBandwidth.Lowest)]
    [InlineData(true, false, NDIRecvBandwidth.Lowest, NDIRecvBandwidth.Lowest)]
    public void Resolve_UsesExplicitOverrideOrStreamDefaults(
        bool receiveAudio,
        bool receiveVideo,
        NDIRecvBandwidth configured,
        NDIRecvBandwidth expected) =>
        Assert.Equal(
            expected,
            NDIReceiveBandwidthPolicy.Resolve(receiveAudio, receiveVideo, configured));
}
