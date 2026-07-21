using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using S.Media.Core.Audio;

namespace S.Media.Audio.Benchmarks;

/// <summary>
/// Models the audio-router matrix mode. <c>AudioRouter.ApplyMatrix</c> expands an S×D gain matrix
/// into one single-cell route per non-zero cell, so a dense 8×8 matrix costs 64 independent
/// full-buffer accumulate passes per chunk. <see cref="PerCellRoutes"/> reproduces that shape with
/// 64 single-cell <see cref="ChannelMap.ApplyAdditive"/> passes (the router additionally applies a
/// per-route gain, so the real path is slightly more expensive than this emulation);
/// <see cref="FusedKernelShipped"/> is what the run loop now executes for co-routed cells;
/// <see cref="FusedScalar"/>/<see cref="FusedVectorized"/> stay as reference shapes (the scalar
/// span-slicing loop and the Vector256 dot-product form the shipped kernel is built on).
/// </summary>
[MemoryDiagnoser]
public class MatrixMixBenchmarks
{
    private const int Channels = 8;
    private const int SamplesPerChannel = 480;

    private readonly float[] _gains = new float[Channels * Channels];
    private readonly float[] _rampToGains = new float[Channels * Channels];
    private readonly float[] _gainsSixWide = new float[6 * Channels];
    private ChannelMap[] _cellMaps = null!;
    private float[] _src = null!;
    private float[] _srcSixChannels = null!;
    private float[] _dst = null!;

    [GlobalSetup]
    public void Setup()
    {
        _src = new float[SamplesPerChannel * Channels];
        _srcSixChannels = new float[SamplesPerChannel * 6];
        _dst = new float[SamplesPerChannel * Channels];
        for (var i = 0; i < _src.Length; i++)
            _src[i] = MathF.Sin(i * 0.01f) * 0.25f;
        for (var i = 0; i < _srcSixChannels.Length; i++)
            _srcSixChannels[i] = MathF.Sin(i * 0.01f) * 0.25f;
        for (var i = 0; i < _gains.Length; i++)
            _gains[i] = 0.11f;
        for (var i = 0; i < _rampToGains.Length; i++)
            _rampToGains[i] = 0.23f;
        for (var i = 0; i < _gainsSixWide.Length; i++)
            _gainsSixWide[i] = 0.11f;

        _cellMaps = new ChannelMap[Channels * Channels];
        Span<int> map = stackalloc int[Channels];
        for (var ic = 0; ic < Channels; ic++)
        {
            for (var oc = 0; oc < Channels; oc++)
            {
                map.Fill(ChannelMap.Silence);
                map[oc] = ic;
                _cellMaps[ic * Channels + oc] = new ChannelMap(map);
            }
        }
    }

    [Benchmark(Baseline = true)]
    public void PerCellRoutes()
    {
        Array.Clear(_dst);
        foreach (var cell in _cellMaps)
            cell.ApplyAdditive(_src, Channels, _dst, SamplesPerChannel);
    }

    /// <summary>The shipped fused kernel (AudioRouter.ApplyFusedMatrixSettled) - what the run
    /// loop now executes for co-routed matrix cells instead of PerCellRoutes.</summary>
    [Benchmark]
    public void FusedKernelShipped()
    {
        Array.Clear(_dst);
        S.Media.Routing.AudioRouter.ApplyFusedMatrixSettled(_src, Channels, _dst, Channels, _gains, SamplesPerChannel);
    }

    /// <summary>The shipped ramping fused kernel (AudioRouter.ApplyFusedMatrixRamp) - the chunk
    /// shape while any cell of the group is mid-fade.</summary>
    [Benchmark]
    public void FusedKernelShippedRamp()
    {
        Array.Clear(_dst);
        S.Media.Routing.AudioRouter.ApplyFusedMatrixRamp(_src, Channels, _dst, Channels, _gains, _rampToGains, SamplesPerChannel);
    }

    /// <summary>Odd source width (6ch → 8ch) - stays on the scalar fallback inside the shipped
    /// kernel, guarding against a regression that only shows off the 8/4-wide vector paths.</summary>
    [Benchmark]
    public void FusedKernelShippedSixWide()
    {
        Array.Clear(_dst);
        S.Media.Routing.AudioRouter.ApplyFusedMatrixSettled(
            _srcSixChannels, 6, _dst, Channels, _gainsSixWide, SamplesPerChannel);
    }

    [Benchmark]
    public void FusedScalar()
    {
        Array.Clear(_dst);
        var src = _src.AsSpan();
        var dst = _dst.AsSpan();
        var gains = _gains.AsSpan();
        for (var f = 0; f < SamplesPerChannel; f++)
        {
            var srcFrame = src.Slice(f * Channels, Channels);
            var dstFrame = dst.Slice(f * Channels, Channels);
            for (var oc = 0; oc < Channels; oc++)
            {
                var acc = 0f;
                var row = gains.Slice(oc * Channels, Channels);
                for (var ic = 0; ic < Channels; ic++)
                    acc += srcFrame[ic] * row[ic];
                dstFrame[oc] += acc;
            }
        }
    }

    [Benchmark]
    public void FusedVectorized()
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            FusedScalar();
            return;
        }

        Array.Clear(_dst);
        ReadOnlySpan<float> src = _src;
        var dst = _dst.AsSpan();
        ReadOnlySpan<float> gains = _gains;
        for (var f = 0; f < SamplesPerChannel; f++)
        {
            var srcVec = Vector256.Create(src.Slice(f * Channels, Channels));
            var dstFrame = dst.Slice(f * Channels, Channels);
            for (var oc = 0; oc < Channels; oc++)
            {
                var row = Vector256.Create(gains.Slice(oc * Channels, Channels));
                dstFrame[oc] += Vector256.Sum(srcVec * row);
            }
        }
    }
}
