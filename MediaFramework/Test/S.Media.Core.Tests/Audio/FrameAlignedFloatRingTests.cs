using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Whole-frame conformance for the shared ring primitive every audio backend uses (P1-1). The core
/// invariant: no operation may ever split an interleaved frame, for ANY channel count - a power-of-two
/// float capacity is not divisible by 3, 5, 6 or 7, so unaligned truncation would rotate channels.
/// Samples encode <c>frameOrdinal * 100 + channelIndex</c> so any rotation is immediately visible.
/// </summary>
public sealed class FrameAlignedFloatRingTests
{
    public static TheoryData<int> ChannelCounts()
    {
        // 1 through 32 - the product supports arbitrary counts including a 32-in/32-out interface.
        var data = new TheoryData<int>();
        for (var channels = 1; channels <= 32; channels++)
            data.Add(channels);
        return data;
    }

    private static float Marker(long frame, int channel) => frame * 100 + channel;

    private static float[] Frames(int channels, long firstFrame, int frameCount)
    {
        var floats = new float[frameCount * channels];
        for (var f = 0; f < frameCount; f++)
        for (var c = 0; c < channels; c++)
            floats[f * channels + c] = Marker(firstFrame + f, c);
        return floats;
    }

    private static void AssertFramesAligned(ReadOnlySpan<float> data, int channels)
    {
        for (var i = 0; i < data.Length; i += channels)
        {
            var frame = (long)(data[i] / 100);
            for (var c = 0; c < channels; c++)
                Assert.Equal(Marker(frame, c), data[i + c]);
        }
    }

    [Theory]
    [MemberData(nameof(ChannelCounts))]
    public void UsableCapacity_IsAlwaysAFrameMultiple(int channels)
    {
        var ring = new FrameAlignedFloatRing(channels, requestedFloats: 4096);
        Assert.Equal(0, ring.CapacityFloats % channels);
        Assert.True(ring.CapacityFloats > 0);
        Assert.Equal(ring.CapacityFloats / channels, ring.CapacityFrames);
    }

    [Theory]
    [MemberData(nameof(ChannelCounts))]
    public void OverflowingWrite_NeverSplitsAFrame(int channels)
    {
        var ring = new FrameAlignedFloatRing(channels, requestedFloats: 1024);
        var capacityFrames = ring.CapacityFrames;

        // Fill to one frame below capacity, then submit a chunk that cannot fully fit.
        var fill = Frames(channels, firstFrame: 0, frameCount: capacityFrames - 1);
        Assert.Equal(fill.Length, ring.Write(fill));

        var overflowChunk = Frames(channels, firstFrame: capacityFrames - 1, frameCount: 4);
        var written = ring.Write(overflowChunk);

        Assert.Equal(channels, written); // exactly the one whole frame that fit
        Assert.Equal(0, ring.BufferedFloats % channels);

        // Drain and verify every frame is intact and in order - no channel rotation anywhere.
        var drained = new float[ring.CapacityFloats];
        var read = ring.Read(drained);
        Assert.Equal(capacityFrames * channels, read);
        AssertFramesAligned(drained.AsSpan(0, read), channels);
        for (var f = 0; f < capacityFrames; f++)
            Assert.Equal(Marker(f, 0), drained[f * channels]);
    }

    [Theory]
    [MemberData(nameof(ChannelCounts))]
    public void WrapAround_PreservesChannelOrder(int channels)
    {
        var ring = new FrameAlignedFloatRing(channels, requestedFloats: 1024);
        var chunkFrames = Math.Max(3, ring.CapacityFrames / 3);
        var scratch = new float[chunkFrames * channels];
        long produced = 0, consumed = 0;

        // Push the cursors around the physical buffer several times.
        for (var round = 0; round < 12; round++)
        {
            var chunk = Frames(channels, produced, chunkFrames);
            var written = ring.Write(chunk);
            Assert.Equal(0, written % channels);
            produced += written / channels;

            var read = ring.Read(scratch);
            Assert.Equal(0, read % channels);
            AssertFramesAligned(scratch.AsSpan(0, read), channels);
            if (read > 0)
            {
                Assert.Equal(Marker(consumed, 0), scratch[0]);
                consumed += read / channels;
            }
        }

        Assert.Equal(produced, consumed + ring.BufferedFrames);
    }

    [Theory]
    [MemberData(nameof(ChannelCounts))]
    public void DropOldest_KeepsWholeFrames(int channels)
    {
        var ring = new FrameAlignedFloatRing(channels, requestedFloats: 2048);
        var frames = ring.CapacityFrames;
        Assert.Equal(frames * channels, ring.Write(Frames(channels, 0, frames)));

        var keepFrames = Math.Max(1, frames / 4);
        var dropped = ring.DropOldestKeepingFloats(keepFrames * channels + channels - 1); // deliberately unaligned
        Assert.Equal(0, dropped % channels);
        Assert.Equal(0, ring.BufferedFloats % channels);

        var drained = new float[ring.BufferedFloats];
        Assert.Equal(drained.Length, ring.Read(drained));
        AssertFramesAligned(drained, channels);
        // The oldest frames were dropped: what remains ends at the newest produced frame.
        Assert.Equal(Marker(frames - 1, channels - 1), drained[^1]);
    }

    [Fact]
    public void Clear_DiscardsEverything()
    {
        var ring = new FrameAlignedFloatRing(channels: 6, requestedFloats: 1024);
        ring.Write(Frames(6, 0, 10));
        ring.Clear();
        Assert.Equal(0, ring.BufferedFloats);
        var scratch = new float[6];
        Assert.Equal(0, ring.Read(scratch));
    }

    [Fact]
    public void MisalignedLengths_AreRejected()
    {
        var ring = new FrameAlignedFloatRing(channels: 6, requestedFloats: 1024);
        Assert.Throws<ArgumentException>(() => ring.Write(new float[5]));
        Assert.Throws<ArgumentException>(() => ring.Read(new float[7]));
    }

    [Fact]
    public void SixChannel_SustainedOverrunPressure_NeverRotatesChannels()
    {
        // The concrete P1-1 scenario: a 5.1 ring under producer pressure. The consumer drains slower
        // than the producer fills, forcing constant overflow truncation at a capacity that is NOT a
        // multiple-of-six power of two internally.
        const int channels = 6;
        var ring = new FrameAlignedFloatRing(channels, requestedFloats: 4096);
        var scratch = new float[20 * channels];
        long produced = 0;

        for (var round = 0; round < 200; round++)
        {
            var chunk = Frames(channels, produced, 37); // odd frame count to hit ragged boundaries
            produced += ring.Write(chunk) / channels;

            var read = ring.Read(scratch);
            AssertFramesAligned(scratch.AsSpan(0, read), channels);
        }
    }
}
