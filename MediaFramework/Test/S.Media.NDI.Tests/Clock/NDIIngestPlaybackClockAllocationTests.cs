using NDILib;
using S.Media.NDI.Clock;
using Xunit;

namespace S.Media.NDI.Tests.Clock;

public sealed class NDIIngestPlaybackClockAllocationTests
{
    private const int WarmupIterations = 100;
    private const int MeasuredIterations = 2000;

    [Fact]
    public void NDIIngestPlaybackClock_ElapsedSinceStart_does_not_allocate_per_read()
    {
        var clock = new NDIIngestPlaybackClock();
        clock.AttachReceiver();
        clock.NotifyAudioFrame(48_000, 480, timecode100Ns: 0, NDIConstants.TimestampUndefined);
        clock.NotifyAudioFrame(48_000, 480, timecode100Ns: 10_000_000, NDIConstants.TimestampUndefined);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = clock.ElapsedSinceStart;
            _ = clock.IsAdvancing;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredIterations; i++)
        {
            _ = clock.ElapsedSinceStart;
            _ = clock.IsAdvancing;
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }
}
