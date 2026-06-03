using HaPlay.Playback;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace HaPlay.Tests;

public sealed class MeteringAudioOutputTests
{
    [Fact]
    public void Wrap_PreservesClockedPlaybackOutput()
    {
        var inner = new ClockedPlaybackOutput(new AudioFormat(48_000, 2));
        var meter = MeteringAudioOutput.Wrap(inner);

        var clocked = Assert.IsAssignableFrom<IClockedOutput>(meter);
        var playback = Assert.IsAssignableFrom<IPlaybackClock>(meter);
        var flushable = Assert.IsAssignableFrom<IFlushableOutput>(meter);

        Assert.True(clocked.WaitForCapacity(480, CancellationToken.None));
        Assert.Equal(480, inner.LastWaitSamples);
        Assert.Equal(TimeSpan.FromSeconds(7), playback.ElapsedSinceStart);
        flushable.Flush();
        Assert.True(inner.Flushed);
    }

    [Fact]
    public void Wrap_DoesNotPromoteNonClockedOutput()
    {
        var meter = MeteringAudioOutput.Wrap(new PlainOutput(new AudioFormat(48_000, 2)));

        Assert.IsNotAssignableFrom<IClockedOutput>(meter);
        Assert.IsNotAssignableFrom<IPlaybackClock>(meter);
        Assert.IsAssignableFrom<IFlushableOutput>(meter);
    }

    [Fact]
    public void WrappedClockedOutput_BecomesAudioRouterPrimaryAndClockMaster()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(48_000, 480);
        router.AttachMasterClock(clock);

        var meter = MeteringAudioOutput.Wrap(new ClockedPlaybackOutput(new AudioFormat(48_000, 2)));
        var id = router.AddOutput(meter);

        Assert.Equal(id, router.PrimaryOutputId);
        Assert.Same(meter, clock.Master);
    }

    [Fact]
    public void WrappedResampledClockedOutput_BecomesAudioRouterPrimaryAndClockMaster()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(44_100, 441);
        router.AttachMasterClock(clock);

        var inner = new ClockedPlaybackOutput(new AudioFormat(48_000, 2));
        var resampled = ResamplingAudioOutput.Wrap(inner, new AudioFormat(44_100, 2));
        var meter = MeteringAudioOutput.Wrap(resampled);
        var id = router.AddOutput(meter);

        Assert.Equal(id, router.PrimaryOutputId);
        Assert.Same(meter, clock.Master);
        Assert.True(((IClockedOutput)meter).WaitForCapacity(441, CancellationToken.None));
        Assert.Equal(482, inner.LastWaitSamples);
    }

    private sealed class PlainOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockedPlaybackOutput(AudioFormat format) :
        IAudioOutput,
        IClockedOutput,
        IPlaybackClock,
        IFlushableOutput
    {
        public AudioFormat Format { get; } = format;

        public int LastWaitSamples { get; private set; }

        public bool Flushed { get; private set; }

        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(7);

        public bool IsAdvancing => true;

        public void Submit(ReadOnlySpan<float> packedSamples) { }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            LastWaitSamples = chunkSamples;
            return !token.IsCancellationRequested;
        }

        public void Flush() => Flushed = true;
    }
}
