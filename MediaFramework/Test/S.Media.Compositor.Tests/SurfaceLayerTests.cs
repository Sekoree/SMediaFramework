using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Compositor.Tests;

/// <summary>
/// NXT-10 - surface layers as first-class mixer citizens. A fake <see cref="IVideoCompositorSurfaceHost"/>
/// (CPU compositing behind the capability interface) proves the mixer's routing logic without a GL
/// context: surface slots snapshot atomically, ride their placement, and route the composite through
/// <see cref="IVideoCompositorSurfaceHost.CompositeWithSurfaces"/>; a non-hosting compositor refuses
/// surface slots so callers fall back to the source's frame path.
/// </summary>
public sealed class SurfaceLayerTests
{
    private static readonly VideoFormat Canvas = new(16, 8, PixelFormat.Bgra32, new Rational(30, 1));

    private sealed class RecordingSurface : IVideoCompositorLayerSurface
    {
        public int ConfigureCalls;
        public void ConfigureGl(GL gl, VideoFormat canvas) => ConfigureCalls++;
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    /// <summary>CPU compositing behind the surface-host capability - records what reaches
    /// CompositeWithSurfaces so the mixer's surface routing is observable without GL.</summary>
    private sealed class FakeSurfaceHost(VideoFormat output) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public readonly List<(int FrameLayers, CompositorSurfaceLayer[] Surfaces, TimeSpan Pts)> SurfaceComposites = [];
        public int PlainComposites;

        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);

        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
        {
            PlainComposites++;
            return _inner.Composite(layersBackToFront, presentationTime);
        }

        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime)
        {
            SurfaceComposites.Add((frameLayers.Count, surfaceLayers.ToArray(), presentationTime));
            return _inner.Composite(frameLayers, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void CpuCompositor_DoesNotHostSurfaces_AndAddThrows()
    {
        using var mixer = new VideoCompositorSource(Canvas, new CpuVideoCompositor(Canvas));
        Assert.False(mixer.SupportsSurfaceLayers);
        Assert.Throws<InvalidOperationException>(() => mixer.AddSurfaceSlot(new RecordingSurface()));
    }

    [Fact]
    public void SurfaceSlots_RouteThroughCompositeWithSurfaces_WithPlacementAndPts()
    {
        var host = new FakeSurfaceHost(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, host);
        Assert.True(mixer.SupportsSurfaceLayers);

        var surface = new RecordingSurface();
        var slot = mixer.AddSurfaceSlot(surface);
        slot.Transform = LayerTransform2D.Translate(3, 4);
        slot.Opacity = 0.5f;

        var pts = TimeSpan.FromSeconds(1.25);
        Assert.True(mixer.TryReadNextFrame(pts, out var frame));
        frame.Dispose();

        var call = Assert.Single(host.SurfaceComposites);
        Assert.Equal(pts, call.Pts);
        var placed = Assert.Single(call.Surfaces);
        Assert.Same(surface, placed.Surface);
        Assert.Equal(LayerTransform2D.Translate(3, 4), placed.Transform);
        Assert.Equal(0.5f, placed.Opacity);
        Assert.Equal(0, host.PlainComposites); // the surface path replaced the plain composite
    }

    [Fact]
    public void ZeroOpacitySurfaces_AreSkipped_AndPlainCompositeRuns()
    {
        var host = new FakeSurfaceHost(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, host);
        mixer.AddSurfaceSlot(new RecordingSurface()).Opacity = 0f;

        Assert.True(mixer.TryReadNextFrame(TimeSpan.Zero, out var frame));
        frame.Dispose();

        Assert.Empty(host.SurfaceComposites);
        Assert.Equal(1, host.PlainComposites);
    }

    [Fact]
    public void RemovedSurfaceSlot_StopsCompositing_AndSurfaceIsNotDisposed()
    {
        var host = new FakeSurfaceHost(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, host);
        var surface = new RecordingSurface();
        var slot = mixer.AddSurfaceSlot(surface);

        Assert.True(mixer.HasSurfaceSlots);
        Assert.True(mixer.RemoveSurfaceSlot(slot));
        Assert.False(mixer.HasSurfaceSlots);

        Assert.True(mixer.TryReadNextFrame(TimeSpan.Zero, out var frame));
        frame.Dispose();
        Assert.Empty(host.SurfaceComposites); // caller owns the surface; mixer never disposed it
    }

    [Fact]
    public void SurfaceSlots_OrderBySortComparison()
    {
        var host = new FakeSurfaceHost(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, host);
        var a = mixer.AddSurfaceSlot(new RecordingSurface(), "a");
        var b = mixer.AddSurfaceSlot(new RecordingSurface(), "b");
        mixer.SortSurfaceSlots((x, y) => string.CompareOrdinal(y.Id, x.Id)); // reverse: b before a

        Assert.True(mixer.TryReadNextFrame(TimeSpan.Zero, out var frame));
        frame.Dispose();

        var call = Assert.Single(host.SurfaceComposites);
        Assert.Equal(2, call.Surfaces.Length);
        Assert.Same(b.Surface, call.Surfaces[0].Surface);
        Assert.Same(a.Surface, call.Surfaces[1].Surface);
    }
}
