using S.Media.Compositor;
using S.Media.Compositor.Effects;
using S.Media.Core.Buses;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;
using S.Media.Routing;
using S.Media.Session;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Unified effect-catalog coverage: layer effects registered/created through <see cref="IBusRegistry"/>
/// (chroma key's JSON factory), the CPU bridge that runs layer-effect kernels as an output bus
/// effect, and the geometry-stage seam (the mapping/warp spec behind
/// <see cref="IVideoLayerGeometryEffect"/> resolving identically to the raw resolver).
/// </summary>
public sealed class EffectCatalogTests
{
    [Fact]
    public void BusRegistry_CreatesLayerEffectFromJsonConfig()
    {
        var registry = BusRegistryBuilder.Build(b =>
            b.AddLayerEffect("chroma-key", static config => ChromaKeyVideoEffect.FromJson(config)));

        Assert.Contains("chroma-key", registry.LayerEffectKinds);
        Assert.True(registry.TryCreateLayerEffect(
            "chroma-key", """{"keyR":0,"keyG":0,"keyB":1,"similarity":0.25}""", out var effect));
        Assert.Equal(ChromaKeyVideoEffect.EffectId, effect.Descriptor.Id);
        // Packed values: keyR, keyG, keyB, similarity, smoothness, spill.
        Assert.Equal(1f, effect.Values[2]);
        Assert.Equal(0.25f, effect.Values[3]);
        Assert.False(registry.TryCreateLayerEffect("unknown", null, out _));
    }

    [Fact]
    public void ChromaKeyFromJson_MalformedConfig_FallsBackToGreenScreenDefaults()
    {
        var effect = ChromaKeyVideoEffect.FromJson("{not json");
        Assert.Equal(ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen).Values.ToArray(),
            effect.Values.ToArray());
    }

    [Fact]
    public void LayerEffectBusAdapter_KeysGreenFrameOnOutputPath()
    {
        using var adapter = new LayerEffectVideoBusAdapter(
            [ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen)]);
        var format = new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
        adapter.Configure(format);

        using var keyed = adapter.Process(SolidBgra(format, b: 0, g: 255, r: 0), TimeSpan.Zero);
        Assert.Equal(0, keyed.Planes[0].Span[3]);

        using var kept = adapter.Process(SolidBgra(format, b: 10, g: 20, r: 230), TimeSpan.Zero);
        Assert.Equal(255, kept.Planes[0].Span[3]);
        Assert.InRange(kept.Planes[0].Span[2], 200, 255);
    }

    [Fact]
    public void LayerEffectBusAdapter_GpuOnlyChain_PassesFramesThrough()
    {
        var gpuOnly = new VideoLayerEffectDescriptor("test.gpu-only", "return src;", []);
        using var adapter = new LayerEffectVideoBusAdapter([new VideoLayerEffect(gpuOnly, [])]);
        var format = new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
        adapter.Configure(format);

        var frame = SolidBgra(format, b: 0, g: 255, r: 0);
        using var result = adapter.Process(frame, TimeSpan.Zero);
        Assert.Same(frame, result);
    }

    [Fact]
    public void OutputMappingGeometryEffect_MatchesRawResolver()
    {
        var spec = new ClipOutputMappingSpec(
            Sections:
            [
                new ClipOutputMappingSection(
                    Id: "a", Enabled: true,
                    SrcX: 0, SrcY: 0, SrcWidth: 0.5, SrcHeight: 1,
                    DestX: 0.5, DestY: 0, DestWidth: 0.5, DestHeight: 1),
                new ClipOutputMappingSection(
                    Id: "off", Enabled: false,
                    SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                    DestX: 0, DestY: 0, DestWidth: 1, DestHeight: 1),
            ],
            OutputWidth: 640,
            OutputHeight: 360);
        var source = new VideoFormat(1280, 720, PixelFormat.Bgra32, new Rational(30, 1));
        var geometry = new OutputMappingGeometryEffect(spec);

        Assert.Equal(
            OutputMappingResolver.ResolveOutputFormat(spec, source),
            geometry.ResolveOutputFormat(source));

        var direct = OutputMappingResolver.Resolve(spec, source.Width, source.Height, RectNormalized.Full);
        var viaSeam = geometry.ResolveSections(source.Width, source.Height, RectNormalized.Full);
        Assert.Equal(direct.Count, viaSeam.Count);
        Assert.Single(viaSeam); // disabled section dropped
        for (var i = 0; i < direct.Count; i++)
        {
            Assert.Equal(direct[i].SourceCrop, viaSeam[i].SourceCrop);
            Assert.Equal(direct[i].Transform, viaSeam[i].Transform);
            Assert.Equal(direct[i].Opacity, viaSeam[i].Opacity);
        }
    }

    private static VideoFrame SolidBgra(VideoFormat format, byte b, byte g, byte r)
    {
        var stride = format.Width * 4;
        var pixels = new byte[stride * format.Height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return new VideoFrame(TimeSpan.Zero, format, [pixels], [stride]);
    }
}
