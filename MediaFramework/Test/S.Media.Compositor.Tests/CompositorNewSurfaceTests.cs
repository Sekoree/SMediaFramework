using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;

namespace S.Media.Compositor.Tests;

public sealed class CompositorWorkingSpaceTests
{
    [Theory]
    [InlineData(VideoColorSpace.Bt709, VideoTransferHint.Sdr, false)]
    [InlineData(VideoColorSpace.Bt601, VideoTransferHint.FromSrgb, false)]
    [InlineData(VideoColorSpace.Bt709, VideoTransferHint.FromPq, true)]   // HDR transfer
    [InlineData(VideoColorSpace.Bt709, VideoTransferHint.FromHlg, true)]  // HDR transfer
    [InlineData(VideoColorSpace.Bt2020, VideoTransferHint.Sdr, true)]     // wide gamut
    [InlineData(VideoColorSpace.Bt2020Cl, VideoTransferHint.Sdr, true)]
    public void IsHdrOrWideGamut_classifies_correctly(VideoColorSpace space, VideoTransferHint transfer, bool expected) =>
        Assert.Equal(expected, CompositorWorkingSpaceController.IsHdrOrWideGamut(space, transfer));

    [Fact]
    public void Promotes_eagerly_and_demotes_only_at_a_boundary()
    {
        var c = new CompositorWorkingSpaceController();
        Assert.Equal(CompositorWorkingSpace.Sdr8, c.Current);
        Assert.Equal(GlCompositorOutputPrecision.Rgba8, c.Precision);

        Assert.False(c.Promote(anyHdrOrWideGamut: false));  // still SDR
        Assert.True(c.Promote(anyHdrOrWideGamut: true));    // SDR -> HDR16F (eager), rebuild
        Assert.Equal(CompositorWorkingSpace.Hdr16F, c.Current);
        Assert.Equal(GlCompositorOutputPrecision.Rgba16F, c.Precision);

        Assert.False(c.Promote(anyHdrOrWideGamut: true));   // already HDR -> no change
        Assert.False(c.Promote(anyHdrOrWideGamut: false));  // hysteresis: a transient SDR gap does NOT demote
        Assert.Equal(CompositorWorkingSpace.Hdr16F, c.Current);

        Assert.False(c.DemoteAtBoundary(anyHdrOrWideGamut: true));  // HDR still present at the boundary
        Assert.True(c.DemoteAtBoundary(anyHdrOrWideGamut: false));  // boundary + no HDR -> demote, rebuild
        Assert.Equal(CompositorWorkingSpace.Sdr8, c.Current);
        Assert.False(c.DemoteAtBoundary(anyHdrOrWideGamut: false)); // already SDR -> no change
    }
}

public sealed class CompositorLayerSurfaceRegistryTests
{
    [Fact]
    public void Registers_and_resolves_layer_surface_factories_by_kind()
    {
        var created = 0;
        var registry = CompositorRegistryBuilder.Build(b =>
            b.AddLayerSurface("object3d", () => { created++; return new FakeSurface(); }));

        Assert.Contains("object3d", registry.LayerSurfaceKinds);
        Assert.True(registry.TryCreateLayerSurface("OBJECT3D", out var surface)); // case-insensitive
        Assert.NotNull(surface);
        Assert.Equal(1, created);

        Assert.False(registry.TryCreateLayerSurface("nope", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void AddLayerSurface_validates_arguments()
    {
        var b = new CompositorRegistryBuilder();
        Assert.Throws<ArgumentException>(() => b.AddLayerSurface("", () => new FakeSurface()));
        Assert.Throws<ArgumentNullException>(() => b.AddLayerSurface("k", (Func<IVideoCompositorLayerSurface>)null!));
    }

    [Fact]
    public void AddLayerSurface_config_aware_passes_config_blob_to_factory()
    {
        string? captured = null;
        var registry = CompositorRegistryBuilder.Build(b =>
            b.AddLayerSurface("mmd", cfg => { captured = cfg; return new FakeSurface(); }));

        Assert.True(registry.TryCreateLayerSurface("mmd", "{\"models\":[\"a.pmx\"]}", out var surface));
        Assert.NotNull(surface);
        Assert.Equal("{\"models\":[\"a.pmx\"]}", captured);
    }

    [Fact]
    public void TryCreateLayerSurface_without_config_passes_null_to_config_aware_factory()
    {
        var sawConfig = "unset";
        var registry = CompositorRegistryBuilder.Build(b =>
            b.AddLayerSurface("mmd", cfg => { sawConfig = cfg ?? "<null>"; return new FakeSurface(); }));

        Assert.True(registry.TryCreateLayerSurface("mmd", out _));
        Assert.Equal("<null>", sawConfig);
    }

    [Fact]
    public void TryCreateLayerSurface_contains_factory_failure()
    {
        var registry = CompositorRegistryBuilder.Build(builder =>
            builder.AddLayerSurface("broken", _ => throw new InvalidOperationException("boom")));

        Assert.False(registry.TryCreateLayerSurface("broken", "{}", out var surface));
        Assert.Null(surface);
    }

    private sealed class FakeSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }
}
