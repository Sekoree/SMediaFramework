using S.Media.NDI;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.NDI.Tests;

/// <summary>
/// NDI-01: the opt-in real loopback soak. Unlike the pure <c>NDIFrameTiming</c>/bandwidth tests, this drives the
/// actual native receive path end-to-end against a live NDI source on the network — discover → open → receive →
/// read diagnostics → dispose — asserting real timestamp correlation (non-negative, non-decreasing video PTS),
/// that frames actually flow, that the capture thread is healthy, and <strong>exactly-once release</strong> (the
/// process-wide live-connection count returns to its baseline after dispose). It is gated behind
/// <c>MFP_RUN_NDI_TESTS=1</c> and self-skips when no source is discovered, so it never runs — or flakes — in the
/// hermetic suite; run it with an NDI source present to validate the hardware path.
/// </summary>
public sealed class NDILoopbackSoakTests(ITestOutputHelper output)
{
    private static bool OptedIn => Environment.GetEnvironmentVariable("MFP_RUN_NDI_TESTS") == "1";

    [Fact]
    public void DiscoverOpenReceiveAndRelease_AgainstALiveSource()
    {
        if (!OptedIn)
        {
            output.WriteLine("skipped: set MFP_RUN_NDI_TESTS=1 to run the live NDI loopback soak");
            return;
        }

        var sources = NDISource.Find(TimeSpan.FromSeconds(5));
        if (sources.Count == 0)
        {
            output.WriteLine("skipped: no NDI source discovered on the network within 5 s");
            return;
        }

        var chosen = sources[0];
        output.WriteLine($"discovered {sources.Count} source(s); opening '{chosen.Name}' ({chosen.UrlAddress})");

        var baseline = NDISource.LiveConnectionCount;
        using (var ndi = NDISource.Open(chosen))
        {
            Assert.True(NDISource.LiveConnectionCount > baseline, "opening a source did not register a live connection");

            var videoFrames = 0;
            var audioFrames = 0;
            var lastVideoTicks = long.MinValue;
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline && videoFrames + audioFrames < 60)
            {
                if (ndi.Video.TryReadNextFrame(out var vf))
                {
                    Assert.True(vf.PresentationTime >= TimeSpan.Zero, $"negative NDI video PTS {vf.PresentationTime}");
                    var ticks = vf.PresentationTime.Ticks;
                    Assert.True(ticks >= lastVideoTicks, $"NDI video PTS went backwards: {ticks} < {lastVideoTicks}");
                    lastVideoTicks = ticks;
                    vf.Dispose();
                    videoFrames++;
                }
                else if (ndi.Audio.TryReadNextFrame(out var af))
                {
                    af.Dispose();
                    audioFrames++;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            output.WriteLine($"received video={videoFrames} audio={audioFrames}; state={ndi.State}");
            Assert.True(videoFrames + audioFrames > 0, "no NDI frames received within the timeout — source connected but silent?");

            var diag = ndi.GetReceiveDiagnostics();
            output.WriteLine($"diagnostics: {diag}");
            Assert.False(diag.CaptureThreadStuck, "NDI capture thread reported stuck");
            if (videoFrames > 0)
                Assert.True(diag.VideoFramesUnpacked > 0, "video frames were read but diagnostics report none unpacked");
        }

        // Exactly-once release: the receiver is gone after dispose (teardown can be slightly async, so poll briefly).
        var releaseDeadline = Environment.TickCount64 + 2_000;
        while (NDISource.LiveConnectionCount > baseline && Environment.TickCount64 < releaseDeadline)
            Thread.Sleep(10);
        Assert.Equal(baseline, NDISource.LiveConnectionCount);
    }
}
