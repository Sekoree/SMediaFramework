using NDILib;
using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// NDI-01: unit tests for <see cref="NDIReceiveBandwidthPolicy"/> - the pure decision that turns "which streams
/// do I want" into a receiver bandwidth mode. The regression it guards is real: defaulting a video-only open to
/// the low-res proxy made NDI playback receive a 640×360 stream and upscale it.
/// </summary>
public sealed class NDIReceiveBandwidthPolicyTests
{
    [Fact]
    public void AudioOnlyReceiver_TakesNoVideoBandwidth() =>
        Assert.Equal(NDIRecvBandwidth.AudioOnly, NDIReceiveBandwidthPolicy.Resolve(receiveAudio: true, receiveVideo: false));

    [Fact]
    public void AudioAndVideoReceiver_DefaultsToFullResolution() =>
        Assert.Equal(NDIRecvBandwidth.Highest, NDIReceiveBandwidthPolicy.Resolve(receiveAudio: true, receiveVideo: true));

    [Fact]
    public void VideoOnlyReceiver_DefaultsToFullResolution_NotTheProxy() =>
        // "play this source without its audio" means full quality, not a thumbnail.
        Assert.Equal(NDIRecvBandwidth.Highest, NDIReceiveBandwidthPolicy.Resolve(receiveAudio: false, receiveVideo: true));

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]  // an explicit override beats even the audio-only default
    [InlineData(false, true)]
    public void ExplicitOverride_IsAlwaysHonored(bool audio, bool video) =>
        Assert.Equal(
            NDIRecvBandwidth.Lowest,
            NDIReceiveBandwidthPolicy.Resolve(audio, video, configured: NDIRecvBandwidth.Lowest));

    [Fact]
    public void NeitherStream_DoesNotClaimAudioOnly() =>
        // No audio and no video is not an "audio-only" receiver - it falls through to the video default.
        Assert.Equal(NDIRecvBandwidth.Highest, NDIReceiveBandwidthPolicy.Resolve(receiveAudio: false, receiveVideo: false));
}
