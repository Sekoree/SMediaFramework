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
    [InlineData(0)]
    [InlineData(1)]
    public void ApplyAdditive_StereoToMonoSingleChannel_matches_naive(int takeChannel)
    {
        var map = new ChannelMap(new[] { takeChannel });
        const int spc = 65;
        var src = new float[2 * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = i * 0.02f + 0.1f;

        var expected = new float[spc];
        var actual = new float[spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, 2, expected, 1, spc);
        map.ApplyAdditive(src, 2, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ApplyAdditive_StereoToMonoSingleChannel_additive_into_primmed_dst(int takeChannel)
    {
        var map = new ChannelMap(new[] { takeChannel });
        const int spc = 12;
        var src = new float[2 * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = 0.5f;

        var expected = new float[spc];
        var actual = new float[spc];
        for (var i = 0; i < spc; i++)
        {
            expected[i] = 3f;
            actual[i] = 3f;
        }

        ApplyAdditivePackedNaive(map.AsSpan(), src, 2, expected, 1, spc);
        map.ApplyAdditive(src, 2, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_QuadToStereoLeadingIdentity_matches_naive()
    {
        var map = new ChannelMap([0, 1]);
        const int spc = 33;
        const int srcCh = 4;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = i * 0.03f - 0.7f;

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_SixChannelToStereoLeadingIdentity_matches_naive()
    {
        var map = new ChannelMap([0, 1]);
        const int spc = 17;
        const int srcCh = 6;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)Math.Sin(i * 0.11);

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_QuadToStereoRearPair_matches_naive()
    {
        var map = new ChannelMap([2, 3]);
        const int spc = 31;
        const int srcCh = 4;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = i * 0.07f - 0.2f;

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_FiveChannelToStereoMiddlePair_matches_naive()
    {
        var map = new ChannelMap([1, 2]);
        const int spc = 19;
        const int srcCh = 5;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)Math.Cos(i * 0.09);

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_SixChannelToStereoRearSurroundPair_matches_naive()
    {
        var map = new ChannelMap([4, 5]);
        const int spc = 23;
        const int srcCh = 6;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)(i % 7) * 0.125f;

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_QuadToStereoDupChannel0_matches_naive()
    {
        var map = new ChannelMap([0, 0]);
        const int spc = 29;
        const int srcCh = 4;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = i * 0.05f + 0.3f;

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_QuadToStereoDupChannel1_matches_naive()
    {
        var map = new ChannelMap([1, 1]);
        const int spc = 27;
        const int srcCh = 4;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)Math.Sin(i * 0.13);

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_SixChannelToStereoDupChannel2_matches_naive()
    {
        var map = new ChannelMap([2, 2]);
        const int spc = 21;
        const int srcCh = 6;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)(i % 5) * 0.2f - 0.4f;

        var expected = new float[2 * spc];
        var actual = new float[2 * spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 2, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_QuadToMonoChannel2_matches_naive()
    {
        var map = new ChannelMap([2]);
        const int spc = 26;
        const int srcCh = 4;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = i * 0.04f - 1f;

        var expected = new float[spc];
        var actual = new float[spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 1, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_FiveChannelToMonoChannel3_matches_naive()
    {
        var map = new ChannelMap([3]);
        const int spc = 18;
        const int srcCh = 5;
        var src = new float[srcCh * spc];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)Math.Cos(i * 0.17);

        var expected = new float[spc];
        var actual = new float[spc];
        ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, 1, spc);
        map.ApplyAdditive(src, srcCh, actual, spc);
        Assert.Equal(expected, actual);
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

    private static void ReferenceMonoDupNAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var v = src[s] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
                dst[b + k] += v;
        }
    }

    private static void ReferenceMonoSilenceOrZeroAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var v = src[s] * uniformGain;
            var b = s * routing.Length;
            for (var k = 0; k < routing.Length; k++)
            {
                if (routing[k] >= 0)
                    dst[b + k] += v;
            }
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

    private static void ReferenceStereoGroupedAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int samplesPerChannel, float uniformGain)
    {
        for (var i = 0; i < samplesPerChannel * 2; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += L;
            dst[dstBase + 1] += L;
            dst[dstBase + 2] += R;
            dst[dstBase + 3] += R;
        }
    }

    private static void ReferenceStereoGroupedSwappedAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int samplesPerChannel, float uniformGain)
    {
        for (var i = 0; i < samplesPerChannel * 2; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += R;
            dst[dstBase + 1] += R;
            dst[dstBase + 2] += L;
            dst[dstBase + 3] += L;
        }
    }

    private static void ReferenceStereoToNAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
                dst[b + k] += (k & 1) == 0 ? L : R;
        }
    }

    private static void ReferencePackedIdentityAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int channels, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var b = s * channels;
            for (var c = 0; c < channels; c++)
                dst[b + c] += src[b + c] * uniformGain;
        }
    }

    private static void ReferenceStereoWideSwappedAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int samplesPerChannel, float uniformGain)
    {
        for (var i = 0; i < samplesPerChannel * 2; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += R;
            dst[dstBase + 1] += L;
            dst[dstBase + 2] += R;
            dst[dstBase + 3] += L;
        }
    }

    private static void ReferenceStereoToNSwappedAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
                dst[b + k] += (k & 1) == 0 ? R : L;
        }
    }

    private static void ReferenceStereoSilenceLrAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * routing.Length;
            for (var k = 0; k < routing.Length; k++)
            {
                var ic = routing[k];
                if (ic >= 0)
                    dst[b + k] += ic == 0 ? L : R;
            }
        }
    }

    private static void ReferenceStereoDupSameChannelAccumulate(
        ReadOnlySpan<float> src, Span<float> dst, int sourceChannelIndex, int samplesPerChannel, float uniformGain)
    {
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var v = src[s * 2 + sourceChannelIndex] * uniformGain;
            var b = s * 2;
            dst[b] += v;
            dst[b + 1] += v;
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

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(0.75f, 5)]
    public void StereoDuplexGroupedSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([0, 0, 1, 1]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(54 + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * 4];
        ReferenceStereoGroupedAccumulate(stereo, expected, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 4];
        if (!ChannelMap.TryAccumulateStereoDuplexGroupedInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain))
            ReferenceStereoGroupedAccumulate(stereo, actual, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(0.75f, 5)]
    public void StereoDuplexGroupedSwappedSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([1, 1, 0, 0]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(55 + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * 4];
        ReferenceStereoGroupedSwappedAccumulate(stereo, expected, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 4];
        if (!ChannelMap.TryAccumulateStereoDuplexGroupedSwappedInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain))
            ReferenceStereoGroupedSwappedAccumulate(stereo, actual, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed8Permutation_seeded_steps_match_naive()
    {
        var rnd = new Random(161_803);
        const int spc = 24;
        const int ch = 8;
        var wiring = new int[ch];
        for (var step = 0; step < 600; step++)
        {
            for (var k = 0; k < ch; k++)
                wiring[k] = k;
            for (var k = ch - 1; k > 0; k--)
            {
                var j = rnd.Next(k + 1);
                (wiring[k], wiring[j]) = (wiring[j], wiring[k]);
            }

            var map = new ChannelMap(wiring);
            var total = spc * ch;
            var src = new float[total];
            for (var i = 0; i < total; i++)
                src[i] = (float)(rnd.NextDouble() * 2 - 1);

            var expected = new float[total];
            ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, spc);
            var actual = new float[total];
            map.ApplyAdditive(src, ch, actual, spc);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ApplyAdditive_GroupedStereoQuad_seeded_steps_match_naive()
    {
        var rnd = new Random(265358);
        for (var step = 0; step < 1200; step++)
        {
            const int spc = 48;
            var stereo = new float[spc * 2];
            for (var i = 0; i < stereo.Length; i++)
                stereo[i] = (float)(rnd.NextDouble() * 2 - 1);

            foreach (var map in new[] { new ChannelMap([0, 0, 1, 1]), new ChannelMap([1, 1, 0, 0]) })
            {
                var expected = new float[spc * 4];
                if (map[0] == 0)
                    ReferenceStereoGroupedAccumulate(stereo, expected, spc, 1f);
                else
                    ReferenceStereoGroupedSwappedAccumulate(stereo, expected, spc, 1f);

                var actual = new float[spc * 4];
                map.ApplyAdditive(stereo, 2, actual, spc);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Theory]
    [InlineData(1f, 256)]
    [InlineData(0.75f, 5)]
    public void StereoDuplexWideSwappedSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([1, 0, 1, 0]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(53 + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * 4];
        ReferenceStereoWideSwappedAccumulate(stereo, expected, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 4];
        if (!ChannelMap.TryAccumulateStereoDuplexWideSwappedInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain))
            ReferenceStereoWideSwappedAccumulate(stereo, actual, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(3, 1f, 200)]
    [InlineData(4, 0.5f, 9)]
    [InlineData(8, -0.25f, 64)]
    [InlineData(6, 1.25f, 5)]
    [InlineData(400, 1f, 16)]
    public void MonoDupNSimd_AccumulatesLikeScalar(int nOut, float gain, int samplesPerChannel)
    {
        var map = ChannelMap.MonoToN(nOut);
        var mono = new float[samplesPerChannel];
        var rnd = new Random(61 + nOut + samplesPerChannel);
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (float)(rnd.NextDouble() - 0.5);

        var expected = new float[samplesPerChannel * nOut];
        ReferenceMonoDupNAccumulate(mono, expected, nOut, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * nOut];
        if (!ChannelMap.TryAccumulateMonoDupNInterleaved(mono, 1, actual, nOut, map, samplesPerChannel, gain))
            ReferenceMonoDupNAccumulate(mono, actual, nOut, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1f, 120)]
    [InlineData(-0.5f, 7)]
    [InlineData(0.25f, 64)]
    public void MonoSilenceOrZeroSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([-1, 0, 0, -1]);
        var mono = new float[samplesPerChannel];
        var rnd = new Random(71 + samplesPerChannel);
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (float)(rnd.NextDouble() - 0.5);

        var expected = new float[samplesPerChannel * 4];
        ReferenceMonoSilenceOrZeroAccumulate(mono, expected, map.AsSpan(), samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 4];
        if (!ChannelMap.TryAccumulateMonoSilenceOrZeroDupInterleaved(mono, 1, actual, 4, map, samplesPerChannel, gain))
            ReferenceMonoSilenceOrZeroAccumulate(mono, actual, map.AsSpan(), samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1f, 96)]
    [InlineData(0.5f, 11)]
    [InlineData(-0.25f, 64)]
    public void StereoSilenceOrZeroSimd_AccumulatesLikeScalar(float gain, int samplesPerChannel)
    {
        var map = new ChannelMap([-1, 0, 1, 0, -1]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(81 + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * 5];
        ReferenceStereoSilenceLrAccumulate(stereo, expected, map.AsSpan(), samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 5];
        if (!ChannelMap.TryAccumulateStereoSilenceOrZeroDupInterleaved(stereo, 2, actual, 5, map, samplesPerChannel, gain))
            ReferenceStereoSilenceLrAccumulate(stereo, actual, map.AsSpan(), samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 1f, 120)]
    [InlineData(1, 0.5f, 15)]
    [InlineData(0, -0.25f, 64)]
    public void StereoDupSingleChannelSimd_AccumulatesLikeScalar(int whichDup, float gain, int samplesPerChannel)
    {
        var map = whichDup == 0 ? new ChannelMap([0, 0]) : new ChannelMap([1, 1]);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(91 + whichDup + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.2);

        var expected = new float[samplesPerChannel * 2];
        ReferenceStereoDupSameChannelAccumulate(stereo, expected, whichDup, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * 2];
        if (!ChannelMap.TryAccumulateStereoDupSingleChannelInterleaved(stereo, 2, actual, 2, map, samplesPerChannel, gain))
            ReferenceStereoDupSameChannelAccumulate(stereo, actual, whichDup, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StereoFullSilenceStereo_ApplyAdditive_LeavesDstUnchanged()
    {
        var map = new ChannelMap([-1, -1]);
        var stereo = new float[128];
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = 2.71f;
        var dst = new float[128];
        for (var i = 0; i < dst.Length; i++)
            dst[i] = (float)(i * 0.01 + 0.33);
        var expected = (float[])dst.ToArray();
        map.ApplyAdditive(stereo, 2, dst, 64);
        Assert.Equal(expected, dst);
    }

    [Theory]
    [InlineData(3, 1f, 180)]
    [InlineData(5, 0.25f, 11)]
    [InlineData(6, -0.5f, 33)]
    [InlineData(4, 0.75f, 128)]
    [InlineData(120, 0.5f, 8)]
    public void StereoToNSimd_AccumulatesLikeScalar(int nOut, float gain, int samplesPerChannel)
    {
        var map = ChannelMap.StereoToN(nOut);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(62 + nOut + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * nOut];
        ReferenceStereoToNAccumulate(stereo, expected, nOut, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * nOut];
        var ok = nOut == 4
            ? ChannelMap.TryAccumulateStereoDuplexWideInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain)
            : ChannelMap.TryAccumulateStereoToNInterleaved(stereo, 2, actual, nOut, map, samplesPerChannel, gain);
        if (!ok)
            ReferenceStereoToNAccumulate(stereo, actual, nOut, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(3, 1f, 180)]
    [InlineData(5, 0.25f, 11)]
    [InlineData(6, -0.5f, 33)]
    [InlineData(4, 0.75f, 128)]
    [InlineData(120, 0.5f, 8)]
    public void StereoToNSwappedSimd_AccumulatesLikeScalar(int nOut, float gain, int samplesPerChannel)
    {
        var map = ChannelMap.StereoToNSwapped(nOut);
        var stereo = new float[samplesPerChannel * 2];
        var rnd = new Random(63 + nOut + samplesPerChannel);
        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = (float)(rnd.NextDouble() - 0.25);

        var expected = new float[samplesPerChannel * nOut];
        ReferenceStereoToNSwappedAccumulate(stereo, expected, nOut, samplesPerChannel, gain);

        var actual = new float[samplesPerChannel * nOut];
        var ok = nOut == 4
            ? ChannelMap.TryAccumulateStereoDuplexWideSwappedInterleaved(stereo, 2, actual, 4, map, samplesPerChannel, gain)
            : ChannelMap.TryAccumulateStereoToNInterleavedSwapped(stereo, 2, actual, nOut, map, samplesPerChannel, gain);
        if (!ok)
            ReferenceStereoToNSwappedAccumulate(stereo, actual, nOut, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StereoToNSwapped_RepeatsRl()
    {
        var map = ChannelMap.StereoToNSwapped(5);
        Assert.Equal(new int[] { 1, 0, 1, 0, 1 }, map.AsSpan().ToArray());
    }

    [Theory]
    [InlineData(3, 1f, 64)]
    [InlineData(4, 0.5f, 3)]
    [InlineData(6, -0.25f, 17)]
    [InlineData(8, 1.25f, 1)]
    [InlineData(12, 0.75f, 128)]
    public void PackedIdentitySimd_AccumulatesLikeScalar(int channels, float gain, int samplesPerChannel)
    {
        var map = ChannelMap.Identity(channels);
        var total = samplesPerChannel * channels;
        var src = new float[total];
        var rnd = new Random(91 + channels + samplesPerChannel);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)(rnd.NextDouble() * 2 - 1);

        var expected = new float[total];
        ReferencePackedIdentityAccumulate(src, expected, channels, samplesPerChannel, gain);

        var actual = new float[total];
        if (!ChannelMap.TryAccumulatePackedIdentityInterleaved(src, channels, actual, channels, map, samplesPerChannel, gain))
            ReferencePackedIdentityAccumulate(src, actual, channels, samplesPerChannel, gain);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(3, 5)]
    [InlineData(6, 11)]
    public void ApplyAdditive_PackedIdentity_MatchesNaive(int channels, int samplesPerChannel)
    {
        var map = ChannelMap.Identity(channels);
        var total = samplesPerChannel * channels;
        var src = new float[total];
        var rnd = new Random(92 + channels);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.03 - 0.1);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, channels, expected, channels, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, channels, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed4Permutation_MatchesNaive_case_a()
    {
        var map = new ChannelMap(new[] { 2, 0, 3, 1 });
        const int ch = 4;
        const int samplesPerChannel = 13;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(9042);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.017 - 0.05);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed4Permutation_MatchesNaive_case_b()
    {
        var map = new ChannelMap(new[] { 3, 2, 1, 0 });
        const int ch = 4;
        const int samplesPerChannel = 17;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(9043);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.019 - 0.07);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed3Permutation_MatchesNaive()
    {
        var wiring = new[] { 1, 2, 0 };
        var map = new ChannelMap(wiring);
        const int ch = 3;
        const int samplesPerChannel = 19;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(40203);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.003 - 0.01);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed3GatherDuplicateSourceIndices_MatchesNaive()
    {
        var map = new ChannelMap(new[] { 0, 0, 2 });
        const int ch = 3;
        const int samplesPerChannel = 23;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(314159);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.011 - 0.02);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed4GatherDuplicateSourceIndices_MatchesNaive()
    {
        var map = new ChannelMap(new[] { 0, 0, 1, 2 });
        const int ch = 4;
        const int samplesPerChannel = 29;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(271828);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.007 - 0.015);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed8GatherDuplicateSourceIndices_MatchesNaive()
    {
        var map = new ChannelMap(new[] { 0, 0, 7, 3, 3, 5, 5, 1 });
        const int ch = 8;
        const int samplesPerChannel = 11;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(1618033);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.005 - 0.012);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed5Permutation_MatchesNaive()
    {
        var wiring = new[] { 2, 4, 0, 1, 3 };
        var map = new ChannelMap(wiring);
        const int ch = 5;
        const int samplesPerChannel = 13;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(50205);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.013 - 0.03);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed8Permutation_MatchesNaive()
    {
        var wiring = new[] { 4, 2, 0, 6, 1, 5, 3, 7 };
        var map = new ChannelMap(wiring);
        const int ch = 8;
        const int samplesPerChannel = 9;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(314159);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        primed[17] = 0.3f;
        primed[22] = -1.1f;
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed6Permutation_MatchesNaive()
    {
        var wiring = new[] { 3, 5, 1, 0, 4, 2 };
        var map = new ChannelMap(wiring);
        const int ch = 6;
        const int samplesPerChannel = 11;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(60206);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.011 - 0.02);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyAdditive_Packed7Permutation_MatchesNaive()
    {
        var wiring = new[] { 6, 3, 1, 0, 2, 5, 4 };
        var map = new ChannelMap(wiring);
        const int ch = 7;
        const int samplesPerChannel = 10;
        var total = ch * samplesPerChannel;
        var src = new float[total];
        var rnd = new Random(70207);
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)rnd.NextDouble();
        var primed = new float[total];
        for (var i = 0; i < total; i++)
            primed[i] = (float)(i * 0.007 - 0.015);
        var expected = (float[])primed.Clone();
        ApplyAdditivePackedNaive(map.AsSpan(), src, ch, expected, ch, samplesPerChannel);
        var actual = (float[])primed.Clone();
        map.ApplyAdditive(src, ch, actual, samplesPerChannel);
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

    [Fact]
    public void ApplyAndApplyAdditive_MultiChannelRandomLayouts_MatchNaive()
    {
        // Sources with ≥3 channels exercise routing indices beyond mono/stereo L–R against SIMD
        // or scalar paths (see **`ApplyAdditive_Packed*Gather*`** and **`ApplyAndApplyAdditive_MultiChannelRandomLayouts_MatchNaive`**).
        var rnd = new Random(926_535);
        var wiring = Array.Empty<int>();
        for (var n = 0; n < 450; n++)
        {
            var srcCh = rnd.Next(3, 13);
            var outCh = rnd.Next(1, 11);
            Array.Resize(ref wiring, outCh);
            for (var o = 0; o < outCh; o++)
                wiring[o] = rnd.Next(-1, srcCh);
            wiring[rnd.Next(outCh)] = srcCh - 1;

            var map = new ChannelMap(wiring.AsSpan());

            var spc = rnd.Next(1, 48);
            var srcTotal = srcCh * spc;
            var dstTotal = map.OutputChannels * spc;

            var src = new float[srcTotal];
            for (var i = 0; i < src.Length; i++)
                src[i] = (float)rnd.NextDouble();

            var expectedApply = new float[dstTotal];
            var actualApply = new float[dstTotal];
            ApplyPackedNaive(map.AsSpan(), src, srcCh, expectedApply, map.OutputChannels, spc);
            map.Apply(src, srcCh, actualApply, spc);
            Assert.Equal(expectedApply, actualApply);

            var expectedAdd = new float[dstTotal];
            var actualAdd = new float[dstTotal];
            for (var i = 0; i < dstTotal; i++)
            {
                var v = (float)rnd.NextDouble();
                expectedAdd[i] = v;
                actualAdd[i] = v;
            }

            ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expectedAdd, map.OutputChannels, spc);
            map.ApplyAdditive(src, srcCh, actualAdd, spc);
            Assert.Equal(expectedAdd, actualAdd);
        }
    }

    [Fact]
    public void ApplyAdditive_RandomLayouts_Deterministic_MatchNaive()
    {
        var rnd = new Random(161803);
        var wiring = Array.Empty<int>();
        for (var n = 0; n < 400; n++)
        {
            var srcCh = rnd.Next(1, 10);
            var outCh = rnd.Next(1, 9);
            Array.Resize(ref wiring, outCh);
            for (var o = 0; o < outCh; o++)
                wiring[o] = rnd.Next(-1, srcCh);
            wiring[rnd.Next(outCh)] = srcCh - 1;

            var map = new ChannelMap(wiring.AsSpan());

            var spc = rnd.Next(1, 64);
            var srcTotal = srcCh * spc;
            var dstTotal = map.OutputChannels * spc;

            var src = new float[srcTotal];
            for (var i = 0; i < src.Length; i++)
                src[i] = (float)rnd.NextDouble();

            var expected = new float[dstTotal];
            var actual = new float[dstTotal];
            for (var i = 0; i < dstTotal; i++)
            {
                var v = (float)rnd.NextDouble();
                expected[i] = v;
                actual[i] = v;
            }

            ApplyAdditivePackedNaive(map.AsSpan(), src, srcCh, expected, map.OutputChannels, spc);
            map.ApplyAdditive(src, srcCh, actual, spc);

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

    private static void ApplyAdditivePackedNaive(
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
                if (ic >= 0)
                    dst[dstBase + oc] += src[srcBase + ic];
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

    [Fact]
    public void DefaultFor_EqualCounts_IsIdentity()
    {
        Assert.Equal(ChannelMap.Identity(2).AsSpan().ToArray(), ChannelMap.DefaultFor(2, 2).AsSpan().ToArray());
        Assert.Equal(ChannelMap.Identity(6).AsSpan().ToArray(), ChannelMap.DefaultFor(6, 6).AsSpan().ToArray());
    }

    [Fact]
    public void DefaultFor_MonoSource_FansOutToEveryOutputChannel()
    {
        // The headless-show crash case: a mono source into a stereo output must not require 2 inputs.
        var map = ChannelMap.DefaultFor(inputChannels: 1, outputChannels: 2);
        Assert.Equal(new[] { 0, 0 }, map.AsSpan().ToArray());
        Assert.Equal(2, map.OutputChannels);
        Assert.Equal(1, map.RequiredInputChannels);
    }

    [Fact]
    public void DefaultFor_StereoSource_RepeatsLR()
    {
        var map = ChannelMap.DefaultFor(inputChannels: 2, outputChannels: 4);
        Assert.Equal(new[] { 0, 1, 0, 1 }, map.AsSpan().ToArray());
        Assert.Equal(2, map.RequiredInputChannels);
    }

    [Fact]
    public void DefaultFor_WideSourceToFewerOutputs_TakesLeadingChannels()
    {
        var map = ChannelMap.DefaultFor(inputChannels: 6, outputChannels: 2);
        Assert.Equal(new[] { 0, 1 }, map.AsSpan().ToArray());
        Assert.Equal(2, map.OutputChannels);
        Assert.Equal(2, map.RequiredInputChannels);
    }

    [Fact]
    public void DefaultFor_WideNonMultipleSource_WrapsSourceChannels()
    {
        var map = ChannelMap.DefaultFor(inputChannels: 3, outputChannels: 5);
        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, map.AsSpan().ToArray());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 6)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 8)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    [InlineData(6, 6)]
    [InlineData(8, 2)]
    public void DefaultFor_AlwaysSatisfiesRouterContract(int input, int output)
    {
        var map = ChannelMap.DefaultFor(input, output);
        Assert.Equal(output, map.OutputChannels);
        // The router rejects a map needing more input channels than the source has; the default must
        // never trip that, for any (source, output) channel-count pairing.
        Assert.True(map.RequiredInputChannels <= input,
            $"RequiredInputChannels {map.RequiredInputChannels} exceeds source channels {input}");
    }

    [Fact]
    public void DefaultFor_NonPositiveCounts_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.DefaultFor(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.DefaultFor(2, 0));
    }
}
