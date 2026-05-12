using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

public sealed class AdaptiveRateAudioSinkTests
{
    private static readonly AudioFormat Stereo48K = new(48_000, 2);

    [Fact]
    public void ZeroPpm_SubmitsSameFrameCount_AsIdentity()
    {
        var inner = new RecordingSink(Stereo48K);
        using var adaptive = new AdaptiveRateAudioSink(inner, () => 0.0, maxRateDeltaHz: 3);
        var chunk = new float[480 * 2];
        for (var i = 0; i < chunk.Length; i++)
            chunk[i] = (float)(i * 0.001);
        adaptive.Submit(chunk);
        Assert.Single(inner.SubmittedFrameCounts);
        Assert.Equal(480, inner.SubmittedFrameCounts[0]);
    }

    [Fact]
    public void StrongNegativePpm_SubmitsFewerFrames_ThanInput()
    {
        var inner = new RecordingSink(Stereo48K);
        using var adaptive = new AdaptiveRateAudioSink(inner, () => -400.0, maxRateDeltaHz: 12);
        var chunk = new float[480 * 2];
        for (var i = 0; i < chunk.Length; i++)
            chunk[i] = (float)(i * 0.001 - 0.1);
        adaptive.Submit(chunk);
        Assert.Single(inner.SubmittedFrameCounts);
        Assert.True(inner.SubmittedFrameCounts[0] < 480, $"got {inner.SubmittedFrameCounts[0]} frames");
    }

    [Fact]
    public void WaitForCapacity_DelegatesToInnerWhenClocked()
    {
        var inner = new RecordingSink(Stereo48K);
        using var adaptive = new AdaptiveRateAudioSink(inner, () => 0.0);
        using var cts = new CancellationTokenSource();
        Assert.True(((IClockedSink)adaptive).WaitForCapacity(256, cts.Token));
        Assert.Equal(1, inner.WaitCalls);
    }

    private sealed class RecordingSink(AudioFormat format) : IAudioSink, IClockedSink
    {
        public AudioFormat Format { get; } = format;
        public List<int> SubmittedFrameCounts { get; } = [];
        public int WaitCalls;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (packedSamples.Length % Format.Channels != 0)
                throw new ArgumentException("alignment");
            SubmittedFrameCounts.Add(packedSamples.Length / Format.Channels);
        }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            WaitCalls++;
            return !token.IsCancellationRequested;
        }
    }
}
