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
/// <see cref="FusedScalar"/>/<see cref="FusedVectorized"/> are the proposed replacement: one pass
/// over src/dst computing the dense matrix-vector product per sample frame.
/// </summary>
[MemoryDiagnoser]
public class MatrixMixBenchmarks
{
    private const int Channels = 8;
    private const int SamplesPerChannel = 480;

    private readonly float[] _gains = new float[Channels * Channels];
    private ChannelMap[] _cellMaps = null!;
    private float[] _src = null!;
    private float[] _dst = null!;

    [GlobalSetup]
    public void Setup()
    {
        _src = new float[SamplesPerChannel * Channels];
        _dst = new float[SamplesPerChannel * Channels];
        for (var i = 0; i < _src.Length; i++)
            _src[i] = MathF.Sin(i * 0.01f) * 0.25f;
        for (var i = 0; i < _gains.Length; i++)
            _gains[i] = 0.11f;

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
