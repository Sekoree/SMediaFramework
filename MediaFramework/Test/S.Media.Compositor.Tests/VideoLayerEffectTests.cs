using S.Media.Compositor;
using S.Media.Compositor.Effects;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;
using Xunit;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Layer-effect system coverage: the CPU compositor path of the built-in chroma key (key color →
/// transparent, non-key survives, spill desaturation), the graceful skip of GPU-only effects on
/// the CPU backend, and the GLSL composer's marker splicing + validation. The GL variant path
/// needs a live context and is exercised by the GL-gated smoke tools instead.
/// </summary>
public sealed class VideoLayerEffectTests
{
    private static readonly Rational Fps = new(30, 1);

    private static VideoFrame SolidBgra(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(w, h, PixelFormat.Bgra32, Fps),
            [pixels],
            [stride]);
    }

    private static CompositorLayer KeyedLayer(VideoFrame frame, ChromaKeySettings settings) =>
        CompositorLayer.Default(frame) with { Effects = [ChromaKeyVideoEffect.Create(settings)] };

    [Fact]
    public void ChromaKey_MakesKeyColoredPixelsTransparent()
    {
        var canvas = new VideoFormat(8, 8, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var green = SolidBgra(8, 8, b: 0, g: 255, r: 0);

        using var result = compositor.Composite(
            [KeyedLayer(green, ChromaKeySettings.GreenScreen)], TimeSpan.Zero);

        // Pure key color sits far inside the similarity radius → fully keyed → the (premultiplied)
        // output stays the transparent-black canvas clear.
        var pixels = result.Planes[0].Span;
        Assert.Equal(0, pixels[3]);
        Assert.Equal(0, pixels[1]);
    }

    [Fact]
    public void ChromaKey_KeepsNonKeyPixelsOpaque()
    {
        var canvas = new VideoFormat(8, 8, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var red = SolidBgra(8, 8, b: 10, g: 20, r: 230);

        using var result = compositor.Composite(
            [KeyedLayer(red, ChromaKeySettings.GreenScreen)], TimeSpan.Zero);

        var pixels = result.Planes[0].Span;
        Assert.Equal(255, pixels[3]);
        Assert.InRange(pixels[2], 200, 255);
    }

    [Fact]
    public void ChromaKey_WithoutEffect_LeavesKeyColorUntouched()
    {
        var canvas = new VideoFormat(8, 8, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var green = SolidBgra(8, 8, b: 0, g: 255, r: 0);

        using var result = compositor.Composite([CompositorLayer.Default(green)], TimeSpan.Zero);

        var pixels = result.Planes[0].Span;
        Assert.Equal(255, pixels[3]);
        Assert.Equal(255, pixels[1]);
    }

    [Fact]
    public void GpuOnlyEffect_IsSkippedByCpuCompositor()
    {
        var gpuOnly = new VideoLayerEffectDescriptor(
            "test.gpu-only", "return vec4(0.0);", []);
        var canvas = new VideoFormat(4, 4, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var white = SolidBgra(4, 4, 255, 255, 255);

        using var result = compositor.Composite(
            [CompositorLayer.Default(white) with { Effects = [new VideoLayerEffect(gpuOnly, [])] }],
            TimeSpan.Zero);

        // No CPU kernel → pass-through, not a failed composite.
        Assert.Equal(255, result.Planes[0].Span[3]);
    }

    [Fact]
    public void SlotEffects_FlowIntoCompositedLayers()
    {
        var canvas = new VideoFormat(8, 8, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = new VideoCompositorSource(canvas, compositor);
        var slot = source.AddSlot();
        slot.Effects = [ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen)];

        slot.Output.Configure(new VideoFormat(8, 8, PixelFormat.Bgra32, Fps));
        slot.Output.Submit(SolidBgra(8, 8, b: 0, g: 255, r: 0));

        Assert.True(source.TryReadNextFrame(out var frame));
        using (frame)
        {
            Assert.Equal(0, frame!.Planes[0].Span[3]);
        }
    }

    [Fact]
    public void MidStackSourceBlendLayer_TransparentPixelsShowLowerLayer()
    {
        // Regression guard for the integer-translate blit fast path: the generic loop skips
        // zero-alpha source pixels even under BlendMode.Source, so a mid-stack Source layer with
        // transparent regions must NOT punch holes through the layer beneath it. (The blit is
        // therefore gated to the first drawn layer, where destination = cleared canvas.)
        var canvas = new VideoFormat(8, 8, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var red = SolidBgra(8, 8, b: 0, g: 0, r: 255);
        using var transparent = SolidBgra(8, 8, b: 0, g: 0, r: 0, a: 0);

        using var result = compositor.Composite(
            [
                CompositorLayer.Default(red) with { BlendMode = BlendMode.Source },
                CompositorLayer.Default(transparent) with { BlendMode = BlendMode.Source },
            ],
            TimeSpan.Zero);

        var pixels = result.Planes[0].Span;
        Assert.Equal(255, pixels[2]); // red survives under the transparent Source layer
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void ShaderComposer_SplicesUniformsAndChainCalls()
    {
        var baseSrc = """
            uniform float uOpacity;
            //__LAYER_FX_DECLARATIONS__
            void main()
            {
                vec4 src = vec4(1.0);
                //__LAYER_FX_APPLY__
            }
            """;
        var effect = ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen);

        var composed = VideoLayerEffectShaderComposer.Compose(baseSrc, [effect, effect]);

        Assert.Contains("uniform vec3 uFx0_key;", composed);
        Assert.Contains("uniform vec3 uFx1_ranges;", composed);
        Assert.Contains("vec4 ApplyFx0(vec4 src)", composed);
        Assert.Contains("src = ApplyFx1(src);", composed);
        Assert.DoesNotContain("$P(", composed);
        Assert.DoesNotContain(VideoLayerEffectShaderComposer.DeclarationsMarker, composed);
        Assert.Equal("chroma-key.v1+chroma-key.v1", VideoLayerEffectShaderComposer.ChainKey([effect, effect]));
    }

    [Fact]
    public void ShaderComposer_UndeclaredParameterReference_Throws()
    {
        var bad = new VideoLayerEffectDescriptor(
            "test.bad-param", "src.a *= $P(missing);\nreturn src;", []);
        Assert.Throws<InvalidOperationException>(() =>
            VideoLayerEffectShaderComposer.Compose(
                $"{VideoLayerEffectShaderComposer.DeclarationsMarker}\n{VideoLayerEffectShaderComposer.ApplyMarker}",
                [new VideoLayerEffect(bad, [])]));
    }

    [Fact]
    public void Descriptor_RejectsInvalidIdsAndParameters()
    {
        Assert.Throws<ArgumentException>(() =>
            new VideoLayerEffectDescriptor("Bad Id!", "return src;", []));
        Assert.Throws<ArgumentException>(() =>
            new VideoLayerEffectDescriptor("ok", "return src;", [new VideoLayerEffectParameter("1bad", 1)]));
        Assert.Throws<ArgumentException>(() =>
            new VideoLayerEffectDescriptor("ok", "return src;", [new VideoLayerEffectParameter("p", 5)]));
        Assert.Throws<ArgumentException>(() =>
            new VideoLayerEffectDescriptor("ok", "return src;",
                [new VideoLayerEffectParameter("p", 1), new VideoLayerEffectParameter("p", 2)]));
        Assert.Throws<ArgumentException>(() =>
            new VideoLayerEffect(ChromaKeyVideoEffect.Descriptor, [1f, 2f]));
    }

    [Fact]
    public void ChromaKeyCpuKernel_SuppressesSpillOnNearKeyPixels()
    {
        var kernel = ChromaKeyVideoEffect.Create(
            new ChromaKeySettings(0f, 1f, 0f, Similarity: 0.1f, Smoothness: 0.05f, SpillSuppression: 0.9f)).CpuKernel;
        Assert.NotNull(kernel);

        // A greenish (but surviving) pixel should lose chroma toward its luma, keeping alpha > 0.
        float r = 0.4f, g = 0.8f, b = 0.4f, a = 1f;
        kernel.Apply(ref r, ref g, ref b, ref a);
        Assert.True(a > 0f);
        Assert.True(g - r < 0.4f, $"expected green pulled toward luma, got r={r} g={g}");
    }
}
