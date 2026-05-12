using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Validates <see cref="AudioRouter.ApplyRoute"/> (SIMD + scalar + gain ramps) against a
/// compact scalar reference mixer on seeded random graphs — extends fuzz-style coverage without
/// threading nondeterminism.
/// </summary>
public sealed class AudioRouterApplyRouteDeterminismTests
{
    private const int SamplesPerChannel = 96;
    private const int SrcChannels = 2;
    private const int DstChannels = 2;

    private static readonly ChannelMap[] StereoToStereoMaps =
    [
        ChannelMap.Identity(2),
        new ChannelMap([1, 0]),
        new ChannelMap([-1, 0]),
        new ChannelMap([1, -1]),
        new ChannelMap([0, 0]),
        new ChannelMap([1, 1]),
        new ChannelMap([-1, -1]),
    ];

    /// <summary>Scalar-only version of the router's per-route accumulation (no SIMD).</summary>
    private static void AccumulateRouteScalarReference(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        ChannelMap map, float fromGain, float toGain, int samplesPerChannel)
    {
        if (fromGain == 0f && toGain == 0f)
            return;

        var routing = map.AsSpan();
        if (fromGain == toGain)
        {
            var g = fromGain;
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var srcBase = s * srcChannels;
                var dstBase = s * dstChannels;
                for (var oc = 0; oc < dstChannels; oc++)
                {
                    var ic = routing[oc];
                    if (ic >= 0)
                        dst[dstBase + oc] += src[srcBase + ic] * g;
                }
            }

            return;
        }

        var step = (toGain - fromGain) / samplesPerChannel;
        var gain = fromGain + step * 0.5f;
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * dstChannels;
            for (var oc = 0; oc < dstChannels; oc++)
            {
                var ic = routing[oc];
                if (ic >= 0)
                    dst[dstBase + oc] += src[srcBase + ic] * gain;
            }

            gain += step;
        }
    }

    [Fact]
    public void ApplyRoute_SeededRandomMultiRouteGraphs_MatchScalarReference()
    {
        var rnd = new Random(161_803_398);
        var src0 = new float[SamplesPerChannel * SrcChannels];
        var src1 = new float[SamplesPerChannel * SrcChannels];
        var src2 = new float[SamplesPerChannel * SrcChannels];
        float[][] srcBuffers = [src0, src1, src2];

        var expected = new float[SamplesPerChannel * DstChannels];
        var actual = new float[SamplesPerChannel * DstChannels];

        for (var iter = 0; iter < 400; iter++)
        {
            for (var si = 0; si < srcBuffers.Length; si++)
            {
                var buf = srcBuffers[si];
                for (var i = 0; i < buf.Length; i++)
                    buf[i] = (float)(rnd.NextDouble() * 2.0 - 1.0);
            }

            Array.Clear(expected);
            Array.Clear(actual);

            var routeCount = rnd.Next(1, 7);
            for (var r = 0; r < routeCount; r++)
            {
                var srcIdx = rnd.Next(srcBuffers.Length);
                var map = StereoToStereoMaps[rnd.Next(StereoToStereoMaps.Length)];
                var fromGain = (float)(rnd.NextDouble() * 2.4 - 0.4);
                var toGain = rnd.Next(3) == 0 ? (float)(rnd.NextDouble() * 2.4 - 0.4) : fromGain;

                AccumulateRouteScalarReference(srcBuffers[srcIdx], SrcChannels, expected, DstChannels,
                    map, fromGain, toGain, SamplesPerChannel);
                AudioRouter.ApplyRoute(srcBuffers[srcIdx], SrcChannels, actual, DstChannels,
                    map, fromGain, toGain, SamplesPerChannel);
            }

            Assert.Equal(expected, actual);
        }
    }
}
