using S.Media.Core.Audio;
using S.Media.NDI.Audio;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDIAudioJitterBufferTests
{
    [Fact]
    public void OverflowKeepsNewestWholeFrames()
    {
        var buffer = new NDIAudioJitterBuffer(
            new AudioFormat(48_000, 2),
            capacityFrames: 4,
            minBufferedFrames: 0);

        Assert.Equal(0, buffer.Enqueue(StereoFrames(0, 4)));
        Assert.Equal(4, buffer.Enqueue(StereoFrames(4, 2)));

        var output = new float[8];
        Assert.Equal(output.Length, buffer.ReadInto(output));
        Assert.Equal(StereoFrames(2, 4), output);
    }

    [Fact]
    public void RebaseKeepsNewestSamplesAndRemainsImmediatelyReadableWhenPrimed()
    {
        var buffer = new NDIAudioJitterBuffer(
            new AudioFormat(48_000, 2),
            capacityFrames: 8,
            minBufferedFrames: 2);
        buffer.Enqueue(StereoFrames(0, 8));

        Assert.Equal(8, buffer.RebaseToLatest(keepFloats: 8));
        var output = new float[4];
        Assert.Equal(output.Length, buffer.ReadInto(output));
        Assert.Equal(StereoFrames(4, 2), output);
    }

    private static float[] StereoFrames(int firstFrame, int frameCount)
    {
        var result = new float[frameCount * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            result[frame * 2] = firstFrame + frame;
            result[frame * 2 + 1] = 1_000 + firstFrame + frame;
        }

        return result;
    }
}
