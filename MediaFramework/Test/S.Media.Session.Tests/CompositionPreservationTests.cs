using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// Opt-in composition preservation (LoadDocumentAsync preserveMatchingCompositions): a live composition
/// whose id + raster + rate match the incoming document is kept alive across the reload, and with it any
/// attached visualizer (surface + audio tap + source). Default reloads still fully rebuild - the behaviour
/// the cue player relies on. This is the mechanism behind a visualizer that runs continuously while a
/// playlist of tracks feeds into it.
/// </summary>
public sealed class CompositionPreservationTests
{
    private sealed class MinimalSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    /// <summary>Surface-hosting CPU compositor (no GL) so SetCompositionVisualizerAsync can attach.</summary>
    private sealed class FakeSurfaceHost(VideoFormat output) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);
        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime) => _inner.Composite(frameLayers, presentationTime);
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A stand-in visualizer: the audio-visual + surface faces a projectM source presents, with a
    /// disposal flag so the test can assert whether the session tore it down.</summary>
    private sealed class FakeVisualizer : IAudioVisualSource, ILayerSurfaceVideoSource, IDisposable
    {
        public bool Disposed;
        public int SubmitCount;

        // IVideoSource
        public VideoFormat Format => new(64, 64, PixelFormat.Bgra32, new Rational(60, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats => [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public bool TryReadNextFrame(out VideoFrame frame) { frame = null!; return false; }
        public void SelectOutputFormat(PixelFormat format) { }

        // IAudioOutput (distinct Format type ⇒ explicit)
        AudioFormat IAudioOutput.Format => new(48_000, 2);
        public void Submit(ReadOnlySpan<float> packedSamples) => SubmitCount++;

        public IVideoCompositorLayerSurface CreateLayerSurface() => new MinimalSurface();
        public void Dispose() => Disposed = true;
    }

    // Audio-only document (no clip needed): just the composition the visualizer attaches to.
    private static ShowDocument CanvasDoc(int w = 128, int h = 72, int fpsNum = 60) => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", w, h, fpsNum, 1)],
        Routes: []);

    private static ShowSession NewSurfaceSession() => new(
        MediaRegistry.Build(_ => { }),
        compositorFactory: fmt => new ClipCompositionCompositor(
            new FakeSurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));

    [Fact]
    public async Task Preserve_KeepsVisualizerAliveAcrossReload()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz));
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));

        // Reload the SAME-shaped document WITH preservation → the composition + visualizer carry over.
        await session.LoadDocumentAsync(CanvasDoc(), preserveMatchingCompositions: true);

        Assert.False(viz.Disposed, "preserved reload must NOT dispose the visualizer");
        Assert.True(await session.HasCompositionVisualizerAsync("screen"), "visualizer should still be attached");
    }

    [Fact]
    public async Task CallerOwnedSource_SurvivesReload_AndReattaches()
    {
        // The continuous-visualizer contract: with disposeSourceOnRemove:false the session unhooks the
        // source on a (default, full-rebuild) reload but does NOT dispose it - the caller re-attaches the
        // SAME instance to the fresh composition and its offscreen renderer never stops.
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz, disposeSourceOnRemove: false));

        await session.LoadDocumentAsync(CanvasDoc()); // default reload: full rebuild

        Assert.False(viz.Disposed, "a caller-owned source must survive the reload");
        Assert.False(await session.HasCompositionVisualizerAsync("screen"), "the slot itself is gone (rebuild)");

        // Re-attach the SAME instance to the fresh composition - the caller's per-track flow.
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz, disposeSourceOnRemove: false));
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
        Assert.False(viz.Disposed);

        // Replacing with null (VIZ off) also leaves a caller-owned source alive.
        Assert.True(await session.SetCompositionVisualizerAsync("screen", null));
        Assert.False(viz.Disposed, "detach must not dispose a caller-owned source");
    }

    [Fact]
    public async Task DefaultReload_RebuildsAndDisposesVisualizer()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz));

        // Default reload (no preservation) fully rebuilds - the historical behaviour the cue player relies on.
        await session.LoadDocumentAsync(CanvasDoc());

        Assert.True(viz.Disposed, "a full-rebuild reload must dispose the old visualizer");
        Assert.False(await session.HasCompositionVisualizerAsync("screen"), "visualizer should be gone after rebuild");
    }

    [Fact]
    public async Task Preserve_ButDifferentSize_RebuildsAndDisposesVisualizer()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc(w: 128, h: 72));

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz));

        // Preservation requested, but the raster changed → the composition can't be reused, so it rebuilds
        // and the visualizer is torn down (the deck relies on a FIXED canvas to keep preservation matching).
        await session.LoadDocumentAsync(CanvasDoc(w: 256, h: 144), preserveMatchingCompositions: true);

        Assert.True(viz.Disposed, "a size change must rebuild and dispose the visualizer even with preserve=true");
        Assert.False(await session.HasCompositionVisualizerAsync("screen"));
    }
}
