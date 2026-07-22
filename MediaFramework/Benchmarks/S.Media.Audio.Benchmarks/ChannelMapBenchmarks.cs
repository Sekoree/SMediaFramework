using BenchmarkDotNet.Attributes;
using S.Media.Core.Audio;

namespace S.Media.Audio.Benchmarks;

/// <summary>
/// Measures <see cref="ChannelMap.ApplyAdditive"/> across the map shapes the router hits in practice.
/// Each call re-runs the ordered SIMD-probe chain in <c>ChannelMap.TryAccumulateAnyInterleaved</c>,
/// so shapes that match early (stereo identity) vs late/never (permutations, single-cell) expose the
/// dispatch overhead a cached kernel-id would remove.
/// </summary>
[MemoryDiagnoser]
public class ChannelMapBenchmarks
{
    public enum Shape
    {
        StereoIdentity,
        StereoSwap,
        MonoDupToStereo,
        SixChPermutation,
        EightChSingleCell,
    }

    private const int SamplesPerChannel = 480;

    private ChannelMap _map;
    private int _srcChannels;
    private float[] _src = null!;
    private float[] _dst = null!;

    [Params(Shape.StereoIdentity, Shape.StereoSwap, Shape.MonoDupToStereo, Shape.SixChPermutation, Shape.EightChSingleCell)]
    public Shape MapShape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_map, _srcChannels) = MapShape switch
        {
            Shape.StereoIdentity => (new ChannelMap([0, 1]), 2),
            Shape.StereoSwap => (new ChannelMap([1, 0]), 2),
            Shape.MonoDupToStereo => (new ChannelMap([0, 0]), 1),
            Shape.SixChPermutation => (new ChannelMap([5, 4, 3, 2, 1, 0]), 6),
            Shape.EightChSingleCell => (new ChannelMap([-1, -1, -1, 3, -1, -1, -1, -1]), 8),
            _ => throw new InvalidOperationException($"Unknown shape {MapShape}."),
        };

        _src = new float[SamplesPerChannel * _srcChannels];
        _dst = new float[SamplesPerChannel * _map.OutputChannels];
        for (var i = 0; i < _src.Length; i++)
            _src[i] = MathF.Sin(i * 0.01f) * 0.5f;
    }

    [Benchmark]
    public void ApplyAdditive() => _map.ApplyAdditive(_src, _srcChannels, _dst, SamplesPerChannel);
}
