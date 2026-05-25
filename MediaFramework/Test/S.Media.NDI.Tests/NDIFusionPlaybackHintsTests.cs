using NDILib;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDIFusionPlaybackHintsTests
{
    [Fact]
    public void FromSnapshot_flags_audio_and_video_pump_drops()
    {
        var fusion = new NDIMonitorReceiverPumpFusion(0, default, false, false, 2, 10, 4, 1, 7);

        var h = NDIFusionPlaybackHints.FromSnapshot(in fusion);
        Assert.True(h.ReviewAudioPumpIfNonZeroDrops);
        Assert.True(h.ReviewVideoPumpIfNonZeroDrops);
        Assert.False(h.ReviewVideoQueueIfSustainedDepth);
    }

    [Fact]
    public void FromSnapshot_flags_sustained_queue_when_receivers_connected()
    {
        var fusion = new NDIMonitorReceiverPumpFusion(1, default, false, false, 0, 100, 8, 7, 0);

        var h = NDIFusionPlaybackHints.FromSnapshot(in fusion, videoQueueDepthHintThreshold: 6);
        Assert.False(h.ReviewAudioPumpIfNonZeroDrops);
        Assert.False(h.ReviewVideoPumpIfNonZeroDrops);
        Assert.True(h.ReviewVideoQueueIfSustainedDepth);
    }

    [Fact]
    public void AnyReviewSuggested_false_when_all_flags_clear()
    {
        var fusion = new NDIMonitorReceiverPumpFusion(0, default, false, false, 0, 0, 0, 0, 0);
        var h = NDIFusionPlaybackHints.FromSnapshot(in fusion);
        Assert.False(h.AnyReviewSuggested);
    }

    [Fact]
    public void AnyReviewSuggested_true_when_any_flag_set()
    {
        var fusion = new NDIMonitorReceiverPumpFusion(0, default, false, false, 0, 0, 0, 0, 1);
        var h = NDIFusionPlaybackHints.FromSnapshot(in fusion);
        Assert.True(h.AnyReviewSuggested);
    }
}
