using BenchmarkDotNet.Attributes;
using S.Media.Compositor;
using S.Media.Core.Video;

namespace S.Media.Compositor.Benchmarks;

/// <summary>
/// Measures <see cref="VideoCpuOpacity.ApplyInPlace"/> — the scalar per-byte fade loop that runs on
/// every frame of a CPU fade (exactly when the pipeline is busiest). Baseline for a SIMD rewrite.
/// Repeatedly fading the same buffer toward black is fine: the byte math cost is value-independent.
/// </summary>
[MemoryDiagnoser]
public class OpacityBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;

    private VideoFrame _bgra = null!;
    private VideoFrame _nv12 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bgra = BenchFrames.CreateBgra(Width, Height);

        var y = new byte[Width * Height];
        var uv = new byte[Width * (Height / 2)];
        Random.Shared.NextBytes(y);
        Random.Shared.NextBytes(uv);
        var format = new VideoFormat(Width, Height, PixelFormat.Nv12, new Rational(60, 1));
        _nv12 = new VideoFrame(TimeSpan.Zero, format, [y, uv], [Width, Width]);
    }

    [Benchmark]
    public void FadeBgra32() => VideoCpuOpacity.ApplyInPlace(_bgra, 0.5f);

    [Benchmark]
    public void FadeNv12() => VideoCpuOpacity.ApplyInPlace(_nv12, 0.5f);
}
