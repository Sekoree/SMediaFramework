using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class ChannelMapTests
{
    [Fact]
    public void Identity_2_RoutesEachChannelToItself()
    {
        var map = ChannelMap.Identity(2);

        Assert.Equal(2, map.OutputChannels);
        Assert.Equal(2, map.RequiredInputChannels);
        Assert.Equal(0, map[0]);
        Assert.Equal(1, map[1]);
    }

    [Fact]
    public void Apply_StereoSwap_SwapsChannels()
    {
        var map = new ChannelMap([1, 0]);
        ReadOnlySpan<float> src = stackalloc float[] { 1f, 2f,  3f, 4f,  5f, 6f };  // 3 frames stereo
        Span<float> dst = stackalloc float[6];

        map.Apply(src, srcChannels: 2, dst, samplesPerChannel: 3);

        Assert.Equal(new float[] { 2f, 1f,  4f, 3f,  6f, 5f }, dst.ToArray());
    }

    [Fact]
    public void Apply_StereoToFour_DuplicatesPairs()
    {
        var map = new ChannelMap([0, 0, 1, 1]);
        ReadOnlySpan<float> src = stackalloc float[] { 10f, 20f,  30f, 40f };  // 2 frames stereo
        Span<float> dst = stackalloc float[8];

        map.Apply(src, srcChannels: 2, dst, samplesPerChannel: 2);

        Assert.Equal(new float[] { 10f, 10f, 20f, 20f,  30f, 30f, 40f, 40f }, dst.ToArray());
    }

    [Fact]
    public void Apply_SilenceSentinelZeroesChannel()
    {
        var map = new ChannelMap([-1, 0, 0, -1]);
        ReadOnlySpan<float> src = stackalloc float[] { 7f,  8f };  // 2 frames mono
        Span<float> dst = stackalloc float[8];
        dst.Fill(99f);  // pre-fill to ensure overwrite (not just zero)

        map.Apply(src, srcChannels: 1, dst, samplesPerChannel: 2);

        Assert.Equal(new float[] { 0f, 7f, 7f, 0f,  0f, 8f, 8f, 0f }, dst.ToArray());
    }

    [Fact]
    public void Apply_DropsUnmappedSourceChannels()
    {
        // 4-channel source, only channel 2 is mapped to output → ch0, ch1, ch3 are dropped.
        var map = new ChannelMap([2]);
        ReadOnlySpan<float> src = stackalloc float[] { 1f, 2f, 3f, 4f,  5f, 6f, 7f, 8f };
        Span<float> dst = stackalloc float[2];

        map.Apply(src, srcChannels: 4, dst, samplesPerChannel: 2);

        Assert.Equal(new float[] { 3f, 7f }, dst.ToArray());
    }

    [Fact]
    public void Apply_TooFewInputChannels_Throws()
    {
        var map = new ChannelMap([0, 1, 2]);  // requires 3 input channels
        ReadOnlySpan<float> src = stackalloc float[] { 1f, 2f };  // 1 frame stereo (only 2 channels)
        Span<float> dst = stackalloc float[3];

        try
        {
            map.Apply(src, srcChannels: 2, dst, samplesPerChannel: 1);
            Assert.Fail("expected ArgumentException");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }

    [Fact]
    public void ApplyAdditive_AccumulatesIntoDst()
    {
        var map = new ChannelMap([0, 1]);
        ReadOnlySpan<float> src = stackalloc float[] { 1f, 2f,  3f, 4f };
        Span<float> dst = stackalloc float[4];
        dst[0] = 100f; dst[1] = 200f; dst[2] = 300f; dst[3] = 400f;

        map.ApplyAdditive(src, srcChannels: 2, dst, samplesPerChannel: 2);

        Assert.Equal(new float[] { 101f, 202f, 303f, 404f }, dst.ToArray());
    }

    [Fact]
    public void ApplyAdditive_SilenceLeavesDstUnchanged()
    {
        var map = new ChannelMap([-1, 1]);
        ReadOnlySpan<float> src = stackalloc float[] { 5f, 6f };
        Span<float> dst = stackalloc float[2];
        dst[0] = 99f; dst[1] = 0f;

        map.ApplyAdditive(src, srcChannels: 2, dst, samplesPerChannel: 1);

        // Channel 0: silence sentinel → no change → 99 stays.
        // Channel 1: src ch1 = 6 added to 0 → 6.
        Assert.Equal(new float[] { 99f, 6f }, dst.ToArray());
    }

    [Fact]
    public void Ctor_EmptyMap_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ChannelMap(ReadOnlySpan<int>.Empty));
    }

    [Fact]
    public void Ctor_NegativeBelowSilence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMap(new int[] { 0, -2 }));
    }

    [Fact]
    public void Equality_StructuralOverArray()
    {
        Assert.Equal(new ChannelMap([0, 1, 0]), new ChannelMap([0, 1, 0]));
        Assert.NotEqual(new ChannelMap([0, 1]), new ChannelMap([1, 0]));
    }

    [Fact]
    public void MonoToN_DupesSingleChannel()
    {
        var map = ChannelMap.MonoToN(4);
        Assert.Equal(new int[] { 0, 0, 0, 0 }, map.AsSpan().ToArray());
        Assert.Equal(1, map.RequiredInputChannels);
    }

    [Fact]
    public void StereoToN_RepeatsLR()
    {
        var map = ChannelMap.StereoToN(5);
        Assert.Equal(new int[] { 0, 1, 0, 1, 0 }, map.AsSpan().ToArray());
        Assert.Equal(2, map.RequiredInputChannels);
    }
}
