using S.Media.NDI.Audio;
using Xunit;

namespace S.Media.NDI.Tests.Audio;

/// <summary>
/// Phase C.5 jitter buffer (2026-05-22): exercises the holdback math and read policy. The full
/// <c>ReadInto</c> path requires a live NDI source, so these tests pin the structural decisions that
/// keep bursty NDI audio from being silence-padded by the router.
/// </summary>
public class NDIAudioReceiverHoldbackTests
{
    [Fact]
    public void ZeroDuration_ReturnsZero()
    {
        Assert.Equal(0, NDIAudioReceiver.ComputeMinBufferedFrames(TimeSpan.Zero, 48_000, 96_000));
    }

    [Fact]
    public void NegativeDuration_ReturnsZero()
    {
        Assert.Equal(0, NDIAudioReceiver.ComputeMinBufferedFrames(TimeSpan.FromMilliseconds(-1), 48_000, 96_000));
    }

    [Theory]
    [InlineData(48_000, 50, 2400)]
    [InlineData(48_000, 33, 1584)]
    [InlineData(44_100, 50, 2205)]
    [InlineData(96_000, 100, 9600)]
    public void TypicalDurations_ProduceExactFrameCounts(int sampleRate, int holdbackMs, int expectedFrames)
    {
        var frames = NDIAudioReceiver.ComputeMinBufferedFrames(
            TimeSpan.FromMilliseconds(holdbackMs), sampleRate, capacityFrames: sampleRate * 2);
        Assert.Equal(expectedFrames, frames);
    }

    [Fact]
    public void Holdback_NeverExceedsHalfCapacity()
    {
        // 5 seconds at 48kHz = 240k frames requested, but capacity is only 1024 → must clamp to 512.
        var frames = NDIAudioReceiver.ComputeMinBufferedFrames(
            TimeSpan.FromSeconds(5), 48_000, capacityFrames: 1024);
        Assert.Equal(512, frames);
    }

    [Fact]
    public void DefaultDuration_FiftyMs()
    {
        // The default exposed for HaPlay / framework callers stays at 50 ms (one NDI 30p frame +
        // margin). Pin the constant so a future tweak surfaces in review.
        Assert.Equal(TimeSpan.FromMilliseconds(50), NDIAudioReceiver.DefaultMinBufferedDuration);
    }

    [Fact]
    public void ZeroSampleRate_ReturnsZero()
    {
        Assert.Equal(0, NDIAudioReceiver.ComputeMinBufferedFrames(
            TimeSpan.FromMilliseconds(50), sampleRate: 0, capacityFrames: 1024));
    }

    [Fact]
    public void ZeroCapacity_ReturnsZero()
    {
        Assert.Equal(0, NDIAudioReceiver.ComputeMinBufferedFrames(
            TimeSpan.FromMilliseconds(50), sampleRate: 48_000, capacityFrames: 0));
    }

    [Fact]
    public void ReadPolicy_WaitsUntilHoldbackIsPrimed()
    {
        var primed = false;

        var read = NDIAudioReceiver.ComputeReadCount(
            requestedFloats: 960,
            availableFloats: 4_000,
            minBufferedFloats: 4_800,
            ref primed);

        Assert.Equal(0, read);
        Assert.False(primed);
    }

    [Fact]
    public void ReadPolicy_ConsumesReserveAfterPriming()
    {
        var primed = false;

        var first = NDIAudioReceiver.ComputeReadCount(
            requestedFloats: 960,
            availableFloats: 4_800,
            minBufferedFloats: 4_800,
            ref primed);
        var second = NDIAudioReceiver.ComputeReadCount(
            requestedFloats: 960,
            availableFloats: 3_840,
            minBufferedFloats: 4_800,
            ref primed);

        Assert.Equal(960, first);
        Assert.Equal(960, second);
        Assert.True(primed);
    }

    [Fact]
    public void ReadPolicy_ReprimesAfterTrueUnderrun()
    {
        var primed = true;

        var partial = NDIAudioReceiver.ComputeReadCount(
            requestedFloats: 960,
            availableFloats: 320,
            minBufferedFloats: 4_800,
            ref primed);
        var recoveryBeforeHoldback = NDIAudioReceiver.ComputeReadCount(
            requestedFloats: 960,
            availableFloats: 4_000,
            minBufferedFloats: 4_800,
            ref primed);

        Assert.Equal(320, partial);
        Assert.False(primed);
        Assert.Equal(0, recoveryBeforeHoldback);
    }
}
