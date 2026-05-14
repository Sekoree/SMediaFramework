using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>§Tier F row 26 — <see cref="NDIOutput.TryPollMonitorReceiverPumpFusion"/> snapshot wiring.</summary>
public sealed class NdiMonitorReceiverPumpFusionTests
{
    private static bool TryProbeNdi(out string? failReason)
    {
        failReason = null;
        try
        {
            var name = $"mf-ndi-probe-{Guid.NewGuid():N}";
            using var o = new NDIOutput(name, clockVideo: false, clockAudio: false,
                videoTimecodeMode: NDIVideoTimecodeMode.Synthesize);
            _ = o.ConnectionCount;
            return true;
        }
        catch (Exception ex)
        {
            failReason = ex.Message;
            return false;
        }
    }

    [Fact]
    public void TryPollMonitorReceiverPumpFusion_passes_through_pump_counters_when_no_receivers()
    {
        if (!TryProbeNdi(out _))
            return;

        using var o = new NDIOutput($"mf-ndi-fusion-{Guid.NewGuid():N}", clockVideo: false, clockAudio: false,
            videoTimecodeMode: NDIVideoTimecodeMode.Synthesize);
        _ = o.EnableAudio(new AudioFormat(48_000, 2));
        o.VideoSink.Configure(new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1)));

        var f = o.TryPollMonitorReceiverPumpFusion(0, false,
            ndiVideoPumpDropped: 11,
            ndiVideoPumpSubmitted: 900,
            ndiVideoPumpMaxQueueDepth: 8,
            ndiVideoPumpCurrentQueuedDepth: 2,
            ndiAudioPumpDropped: 5);

        Assert.Equal(0, f.ReceiverConnectionCount);
        Assert.False(f.UpstreamMetadataFrameDrained);
        Assert.Equal(11, f.NdiVideoPumpDropped);
        Assert.Equal(900, f.NdiVideoPumpSubmitted);
        Assert.Equal(8, f.NdiVideoPumpMaxQueueDepth);
        Assert.Equal(2, f.NdiVideoPumpCurrentQueuedDepth);
        Assert.Equal(5, f.NdiAudioPumpDropped);
    }
}
