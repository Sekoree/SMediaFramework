using NDILib;
using S.Media.NDI.Clock;
using Xunit;

namespace S.Media.NDI.Tests.Clock;

public class NdiIngestPlaybackClockTests
{
    private static long FrameDurationTicks(int sampleRate, int samples) =>
        (long)Math.Round(samples * (double)TimeSpan.TicksPerSecond / sampleRate);

    [Fact]
    public void Timecode_ChainsFrames_ElapsedMatchesSampleDuration()
    {
        var c = new NdiIngestPlaybackClock();
        c.AttachReceiver();
        const int rate = 48_000;
        const int samples = 480;
        var dur = TimeSpan.FromTicks(FrameDurationTicks(rate, samples));

        c.NotifyAudioFrame(rate, samples, timecode100Ns: 0, NDIConstants.TimestampUndefined);
        AssertDurationNear(dur, c.ElapsedSinceStart);
        Assert.True(c.IsAdvancing);

        c.NotifyAudioFrame(rate, samples, timecode100Ns: dur.Ticks, NDIConstants.TimestampUndefined);
        AssertDurationNear(dur + dur, c.ElapsedSinceStart);
    }

    [Fact]
    public void Timestamp_UsedWhenTimecodeSynthesize()
    {
        var c = new NdiIngestPlaybackClock();
        c.AttachReceiver();
        const int rate = 48_000;
        const int samples = 480;
        var dur = TimeSpan.FromTicks(FrameDurationTicks(rate, samples));

        c.NotifyAudioFrame(rate, samples, NDIConstants.TimecodeSynthesize, timestamp100Ns: 1_000_000);
        AssertDurationNear(dur, c.ElapsedSinceStart);
    }

    [Fact]
    public void Pause_FreezesElapsed_ResumeContinues()
    {
        var c = new NdiIngestPlaybackClock();
        c.AttachReceiver();
        c.NotifyAudioFrame(48_000, 4800, 0, NDIConstants.TimestampUndefined);
        Thread.Sleep(20);
        var mid = c.ElapsedSinceStart;
        c.Pause();
        Thread.Sleep(30);
        Assert.False(c.IsAdvancing);
        Assert.True(Math.Abs((c.ElapsedSinceStart - mid).TotalMilliseconds) < 3.0);

        c.Resume();
        Assert.True(c.IsAdvancing);
        var postResume = c.ElapsedSinceStart;
        Thread.Sleep(15);
        Assert.True(c.ElapsedSinceStart > postResume);
    }

    [Fact]
    public void NotifyCaptureStopped_FreezesAndBlocksFurtherFrames()
    {
        var c = new NdiIngestPlaybackClock();
        c.AttachReceiver();
        c.NotifyAudioFrame(48_000, 4800, 0, NDIConstants.TimestampUndefined);
        c.NotifyCaptureStopped();
        var atStop = c.ElapsedSinceStart;
        Assert.False(c.IsAdvancing);
        c.NotifyAudioFrame(48_000, 4800, timecode100Ns: 999_999_999, NDIConstants.TimestampUndefined);
        Assert.Equal(atStop, c.ElapsedSinceStart);
    }

    [Fact]
    public void AttachReceiver_ResetsForNewSession()
    {
        var c = new NdiIngestPlaybackClock();
        c.AttachReceiver();
        c.NotifyAudioFrame(48_000, 4800, 0, NDIConstants.TimestampUndefined);
        c.NotifyCaptureStopped();
        c.AttachReceiver();
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
        Assert.False(c.IsAdvancing);
    }

    private static void AssertDurationNear(TimeSpan expected, TimeSpan actual)
    {
        var d = Math.Abs((actual - expected).Ticks);
        // Frame duration uses double rounding; ingest clock may accumulate small FP drift across chained frames.
        Assert.True(d <= 512, $"expected ~{expected}, got {actual} (delta {d} ticks)");
    }
}
