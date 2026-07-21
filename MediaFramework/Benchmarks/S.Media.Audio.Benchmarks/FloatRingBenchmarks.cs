using BenchmarkDotNet.Attributes;
using S.Media.Core.Audio;

namespace S.Media.Audio.Benchmarks;

/// <summary>
/// Measures <see cref="FrameAlignedFloatRing"/> — the producer↔audio-callback boundary for every
/// device output/input. Single-threaded write/read round trip isolates the copy + index math
/// (the counters currently share a cache line; a padded layout would mostly show up under
/// cross-thread contention, but the single-thread floor matters too).
/// </summary>
[MemoryDiagnoser]
public class FloatRingBenchmarks
{
    private FrameAlignedFloatRing _ring = null!;
    private float[] _chunk = null!;

    [Params(256, 960, 4096)]
    public int ChunkFloats { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ring = new FrameAlignedFloatRing(channels: 2, requestedFloats: 16384);
        _chunk = new float[ChunkFloats];
        for (var i = 0; i < _chunk.Length; i++)
            _chunk[i] = i * 0.001f;
    }

    [Benchmark]
    public int WriteThenRead()
    {
        _ring.Write(_chunk);
        return _ring.Read(_chunk);
    }
}
