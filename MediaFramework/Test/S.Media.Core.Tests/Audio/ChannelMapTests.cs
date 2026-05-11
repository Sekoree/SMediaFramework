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

    [Theory]
    [InlineData(0.25f, 512)]
    [InlineData(1f, 9)]
    [InlineData(-0.5f, 64)]
    public void StereoSimd_Identity_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([0, 1]);
        var floats = samplesPerChannel * 2;
        var srcBuf = new float[floats];
        var rnd = new Random(42 + samplesPerChannel);
        for (var i = 0; i < floats; i++)
            srcBuf[i] = (float)(rnd.NextDouble() * 2 - 1);

        var expected = new float[floats];
        ReferenceStereoAccumulate(srcBuf, expected, map, samplesPerChannel, gain);

        var actual = new float[floats];
        if (!ChannelMap.TryAccumulateStereoIdentityInterleaved(srcBuf, 2, actual, 2, map, samplesPerChannel, gain))
            ReferenceStereoAccumulate(srcBuf, actual, map, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1f, 512)]
    [InlineData(0.5f, 16)]
    public void StereoSimd_Swapped_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([1, 0]);
        var floats = samplesPerChannel * 2;
        var srcBuf = new float[floats];
        var rnd = new Random(43 + samplesPerChannel);
        for (var i = 0; i < floats; i++)
            srcBuf[i] = (float)(rnd.NextDouble() * 2 - 1);

        var expected = new float[floats];
        ReferenceStereoAccumulate(srcBuf, expected, map, samplesPerChannel, gain);

        var actual = new float[floats];
        if (!ChannelMap.TryAccumulateStereoIdentityInterleaved(srcBuf, 2, actual, 2, map, samplesPerChannel, gain))
            ReferenceStereoAccumulate(srcBuf, actual, map, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    private static void ReferenceStereoAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        var routing = map.AsSpan();
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * 2;
            var dstBase = s * 2;
            for (var oc = 0; oc < 2; oc++)
            {
                var ic = routing[oc];
                if (ic >= 0) dst[dstBase + oc] += src[srcBase + ic] * uniformGain;
            }
        }
    }

    private static void ReferenceMonoDupAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var v = src[s] * uniformGain;
            var b = s * 2;
            dst[b] += v;
            dst[b + 1] += v;
        }
    }

    private static void ReferenceStereoWideAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int samplesPerChannel, float uniformGain)
    {
        for (var i = 0; i < samplesPerChannel * 2; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += L;
            dst[dstBase + 1] += R;
            dst[dstBase + 2] += L;
            dst[dstBase + 3] += R;
        }
    }

    [Theory]
    [InlineData(1f, 400)]
    [InlineData(0.5f, 7)]
    [InlineData(-1f, 3)]
    public void MonoDupSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = ChannelMap.MonoToN(2);
        var mono = new float[samplesPerChannel];
        var rnd = new Random(51 + samplesPerChannel);
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (float)(rnd.NextDouble() - 0.5);

        var expected = new float[samplesPerChannel * 2];
        ReferenceMonoDupAccumulate(mono, expected, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 2];
        if (!ChannelMap.TryAccumulateMonoDupStereoInterleaved(mono, 1, actual, 2, map, samplesPerChannel, gain))
            ReferenceMonoDupAccumulate(mono, actual, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(0.75f, 5)]
    public void StereoDuplexWideSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([0, 1, 0, 1]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(52 + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * 4];
        ReferenceStereoWideAccumulate(stereo, expected, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 4];
        if (!ChannelMap.TryAccumulateStereoDuplexWideInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain))
            ReferenceStereoWideAccumulate(stereo, actual, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Apply_RandomLayouts_Deterministic_MatchNaive()
    {
        var rnd = new Random(271828);
        var wiring = Array.Empty<int>();
        for (var n = 0; n < 800; n++)
        {
            var srcCh = rnd.Next(1, 12);
            var outCh = rnd.Next(1, 11);
            Array.Resize(ref wiring, outCh);
            for (var o = 0; o < outCh; o++)
                wiring[o] = rnd.Next(-1, srcCh);
            wiring[rnd.Next(outCh)] = srcCh - 1;

            var map = new ChannelMap(wiring.AsSpan());

            var spc = rnd.Next(1, 97);
            var srcTotal = srcCh * spc;
            var dstTotal = map.OutputChannels * spc;

            var src = new float[srcTotal];
            for (var i = 0; i < src.Length; i++)
                src[i] = (float)rnd.NextDouble();

            var expected = new float[dstTotal];
            var actual = new float[dstTotal];
            ApplyPackedNaive(map.AsSpan(), src, srcCh, expected, map.OutputChannels, spc);

            map.Apply(src, srcCh, actual, spc);

            Assert.Equal(expected, actual);
        }
    }

    private static void ApplyPackedNaive(
        ReadOnlySpan<int> routing, ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int outChannels,
        int samplesPerChannel)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * outChannels;
            for (var oc = 0; oc < outChannels; oc++)
            {
                var ic = routing[oc];
                dst[dstBase + oc] = ic < 0 ? 0f : src[srcBase + ic];
            }
        }
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
