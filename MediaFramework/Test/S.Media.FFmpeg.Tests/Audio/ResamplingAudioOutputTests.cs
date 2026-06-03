using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

public sealed class ResamplingAudioOutputTests
{
    [Fact]
    public void Wrap_PreservesClockedPlaybackOutput()
    {
        var inner = new ClockedPlaybackOutput(new AudioFormat(44_100, 2));
        var output = ResamplingAudioOutput.Wrap(inner, new AudioFormat(48_000, 2));

        var clocked = Assert.IsAssignableFrom<IClockedOutput>(output);
        var playback = Assert.IsAssignableFrom<IPlaybackClock>(output);

        Assert.True(clocked.WaitForCapacity(480, CancellationToken.None));
        Assert.Equal(443, inner.LastWaitSamples);
        Assert.Equal(TimeSpan.FromSeconds(12), playback.ElapsedSinceStart);
        Assert.True(playback.IsAdvancing);
    }

    [Fact]
    public void Wrap_DoesNotPromoteNonClockedOutput()
    {
        var output = ResamplingAudioOutput.Wrap(
            new PlainOutput(new AudioFormat(48_000, 2)),
            new AudioFormat(48_000, 2));

        Assert.IsNotAssignableFrom<IClockedOutput>(output);
        Assert.IsNotAssignableFrom<IPlaybackClock>(output);
    }

    private sealed class PlainOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockedPlaybackOutput(AudioFormat format) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        public AudioFormat Format { get; } = format;

        public int LastWaitSamples { get; private set; }

        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(12);

        public bool IsAdvancing => true;

        public void Submit(ReadOnlySpan<float> packedSamples) { }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            LastWaitSamples = chunkSamples;
            return !token.IsCancellationRequested;
        }
    }
}
