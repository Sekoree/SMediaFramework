using BenchmarkDotNet.Attributes;
using S.Media.Compositor;
using S.Media.Core.Video;

namespace S.Media.Compositor.Benchmarks;

/// <summary>
/// Measures <see cref="CpuVideoCompositor.Composite"/> — the CPU fallback backend that also runs
/// per frame in real deployments via <c>CompositorOutputScaler</c> (NDI format lock / logo template).
/// The per-pixel inner loop currently re-applies the inverse transform and re-dispatches the
/// sampling/blend switches per pixel; these numbers are the baseline for specializing it.
/// </summary>
[MemoryDiagnoser]
public class CpuCompositorBenchmarks
{
    public enum TransformKind
    {
        Identity,
        Scaled,
        Rotated,
    }

    private const int Width = 1280;
    private const int Height = 720;

    private CpuVideoCompositor _compositor = null!;
    private VideoFrame _source = null!;
    private CompositorLayer[] _layers = null!;

    [Params(CompositorSamplingMode.Nearest, CompositorSamplingMode.Bilinear, CompositorSamplingMode.Bicubic)]
    public CompositorSamplingMode Sampling { get; set; }

    [Params(BlendMode.Source, BlendMode.SourceOver)]
    public BlendMode Blend { get; set; }

    [Params(TransformKind.Identity, TransformKind.Rotated)]
    public TransformKind Transform { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var format = new VideoFormat(Width, Height, PixelFormat.Bgra32, new Rational(60, 1));
        _compositor = new CpuVideoCompositor(format, Sampling);
        _source = BenchFrames.CreateBgra(Width, Height);

        var transform = Transform switch
        {
            TransformKind.Scaled => LayerTransform2D.Scale(0.75f, 0.75f),
            TransformKind.Rotated => LayerTransform2D.Compose(
                LayerTransform2D.Translate(64f, 32f), LayerTransform2D.Rotate(0.12f)),
            _ => LayerTransform2D.Identity,
        };
        _layers = [new CompositorLayer(_source, transform, 1f, Blend)];
    }

    [GlobalCleanup]
    public void Cleanup() => _compositor.Dispose();

    [Benchmark]
    public void CompositeOneLayer()
    {
        using var result = _compositor.Composite(_layers, TimeSpan.Zero);
    }
}

/// <summary>
/// The multi-layer case with fixed settings (the deck-compositor shape: opaque base layer plus
/// three alpha-blended overlays), kept out of the parameterized class to avoid a param explosion.
/// </summary>
[MemoryDiagnoser]
public class CpuCompositorMultiLayerBenchmarks
{
    private const int Width = 1280;
    private const int Height = 720;

    private CpuVideoCompositor _compositor = null!;
    private VideoFrame _base = null!;
    private VideoFrame _overlay = null!;
    private CompositorLayer[] _layers = null!;

    [GlobalSetup]
    public void Setup()
    {
        var format = new VideoFormat(Width, Height, PixelFormat.Bgra32, new Rational(60, 1));
        _compositor = new CpuVideoCompositor(format);
        _base = BenchFrames.CreateBgra(Width, Height);
        _overlay = BenchFrames.CreateBgra(480, 270);
        _layers =
        [
            new CompositorLayer(_base, LayerTransform2D.Identity, 1f, BlendMode.Source),
            new CompositorLayer(_overlay, LayerTransform2D.Translate(32f, 32f), 0.9f, BlendMode.SourceOver),
            new CompositorLayer(_overlay, LayerTransform2D.Translate(720f, 64f), 0.7f, BlendMode.SourceOver),
            new CompositorLayer(_overlay, LayerTransform2D.Translate(380f, 400f), 0.5f, BlendMode.SourceOver),
        ];
    }

    [GlobalCleanup]
    public void Cleanup() => _compositor.Dispose();

    [Benchmark]
    public void CompositeFourLayers()
    {
        using var result = _compositor.Composite(_layers, TimeSpan.Zero);
    }
}

/// <summary>
/// The layer-effect path (chroma-key) plus the blend/crop shapes the parameterized class leaves
/// out. Effects gate off BOTH fast paths and force the generic per-pixel loop with a per-pixel
/// premultiplied→straight conversion — the slowest CPU shape, so it needs its own numbers to
/// guard the kernel-scratch reuse and any future row-specialized effects loop.
/// </summary>
[MemoryDiagnoser]
public class CpuCompositorEffectsBenchmarks
{
    private const int Width = 1280;
    private const int Height = 720;

    private CpuVideoCompositor _compositor = null!;
    private VideoFrame _source = null!;
    private CompositorLayer[] _chromaKeyLayer = null!;
    private CompositorLayer[] _multiplyLayer = null!;
    private CompositorLayer[] _croppedLayer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var format = new VideoFormat(Width, Height, PixelFormat.Bgra32, new Rational(60, 1));
        _compositor = new CpuVideoCompositor(format);
        _source = BenchFrames.CreateBgra(Width, Height);
        _chromaKeyLayer =
        [
            CompositorLayer.Default(_source) with
            {
                Effects = [Effects.ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen)],
            },
        ];
        _multiplyLayer = [new CompositorLayer(_source, LayerTransform2D.Identity, 1f, BlendMode.Multiply)];
        _croppedLayer =
        [
            CompositorLayer.Default(_source) with
            {
                SourceCrop = new RectNormalized(0.25f, 0.25f, 0.75f, 0.75f),
            },
        ];
    }

    [GlobalCleanup]
    public void Cleanup() => _compositor.Dispose();

    /// <summary>Generic per-pixel loop + CPU chroma-key kernel per pixel.</summary>
    [Benchmark]
    public void CompositeChromaKey()
    {
        using var result = _compositor.Composite(_chromaKeyLayer, TimeSpan.Zero);
    }

    /// <summary>Multiply blend arm of the generic loop (not reachable from either fast path).</summary>
    [Benchmark]
    public void CompositeMultiply()
    {
        using var result = _compositor.Composite(_multiplyLayer, TimeSpan.Zero);
    }

    /// <summary>Integer-translate blit with a sub-rect crop — exercises the crop-interval logic
    /// in the fast paths instead of the full-frame crop the other benchmarks use.</summary>
    [Benchmark]
    public void CompositeCropped()
    {
        using var result = _compositor.Composite(_croppedLayer, TimeSpan.Zero);
    }
}

internal static class BenchFrames
{
    /// <summary>Array-backed BGRA frame filled with a gradient (avoids all-zero shortcut effects).</summary>
    public static VideoFrame CreateBgra(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i + 0] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(x + y);
                pixels[i + 3] = (byte)(x % 3 == 0 ? 255 : 200);
            }
        }

        var format = new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(60, 1));
        return new VideoFrame(TimeSpan.Zero, format, [pixels], [width * 4]);
    }
}
