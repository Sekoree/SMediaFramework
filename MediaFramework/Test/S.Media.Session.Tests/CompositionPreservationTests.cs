using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// Opt-in composition preservation (LoadDocumentAsync preserveMatchingCompositions): a live composition
/// whose id + raster + rate match the incoming document is kept alive across the reload, and with it any
/// attached visualizer (surface + audio tap + source). Default reloads still fully rebuild; cue-graph edits
/// explicitly opt into preservation. This is the mechanism behind a visualizer that runs continuously while
/// a playlist of tracks feeds into it, including tracks added during playback.
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
    private sealed class FakeSurfaceHost(
        VideoFormat output,
        ConcurrentQueue<float>? surfaceOpacities = null) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);
        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime)
        {
            if (surfaceOpacities is not null)
                foreach (var layer in surfaceLayers)
                    surfaceOpacities.Enqueue(layer.Opacity);
            return _inner.Composite(frameLayers, presentationTime);
        }
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
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref SubmitCount);

        public IVideoCompositorLayerSurface CreateLayerSurface() => new MinimalSurface();
        public void Dispose() => Disposed = true;
    }

    private sealed class ForwardingRateAdapter(IAudioOutput inner, AudioFormat routerFormat)
        : IAudioOutput, IDisposable
    {
        public AudioFormat Format => routerFormat;
        public void Submit(ReadOnlySpan<float> packedSamples) => inner.Submit(packedSamples);
        public void Dispose() { }
    }

    // Audio-only document (no clip needed): just the composition the visualizer attaches to.
    private static ShowDocument CanvasDoc(int w = 128, int h = 72, int fpsNum = 60) => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", w, h, fpsNum, 1)],
        Routes: []);

    private static ShowSession NewSurfaceSession(
        ConcurrentQueue<float>? surfaceOpacities = null,
        IMediaRegistry? registry = null) => new(
        registry ?? MediaRegistry.Build(_ => { }),
        compositorFactory: fmt => new ClipCompositionCompositor(
            new FakeSurfaceHost(fmt, surfaceOpacities), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));

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
    public async Task Preserve_KeepsVisualizerWhenTheCueGraphGainsANewCue()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz));
        var editedDocument = CanvasDoc() with
        {
            Cues = [new CueDefinition("new-song", 1, "Song added during playback")],
        };

        await session.LoadDocumentAsync(editedDocument, preserveMatchingCompositions: true);

        Assert.False(viz.Disposed);
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task FullRebuild_ReattachesVisualizerMarkedPersistent()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        var placement = new VideoPlacementSpec(
            "screen", 1, Opacity: 0.69, Placement: "contain",
            DestX: 0.1, DestY: 0.2, DestWidth: 0.8, DestHeight: 0.7);
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz, placement: placement, preserveAcrossDocumentReload: true));

        // Output-topology changes require the composition itself to be rebuilt. The durable visualizer
        // source/tap remains live and only its compositor surface is recreated on the replacement canvas.
        await session.LoadDocumentAsync(CanvasDoc(), preserveMatchingCompositions: false);

        Assert.False(viz.Disposed, "a persistent visualizer source must survive a full graph rebuild");
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task PersistentVisualizer_AfterFullRebuild_ReceivesLaterDifferentRateClip()
    {
        var registry = MediaRegistry.Build(builder => builder
            .AddDecoder(new FakeAudioDecoderProvider(chunks: 2048, sampleRate: 44_100))
            .SetResamplingOutputFactory(static (inner, routerFormat) =>
                new ForwardingRateAdapter(inner, routerFormat)));
        await using var session = NewSurfaceSession(registry: registry);
        var document = CanvasDoc() with
        {
            Cues = [new CueDefinition("song", 1, "44.1 kHz song")],
            Clips = [new ShowClipBinding("song", "fake://song")],
        };
        await session.LoadDocumentAsync(document);
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz, preserveAcrossDocumentReload: true));

        await session.LoadDocumentAsync(document, preserveMatchingCompositions: false);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("song"));
        await WaitUntilAsync(() => Volatile.Read(ref viz.SubmitCount) > 0, TimeSpan.FromSeconds(2));

        Assert.False(viz.Disposed);
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task RunningVisualizer_PlacementCanBeUpdatedInPlace()
    {
        await using var session = NewSurfaceSession();
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync("screen", viz));

        var updated = await session.UpdateCompositionVisualizerPlacementAsync(
            "screen",
            new VideoPlacementSpec(
                "screen", int.MaxValue - 1, Opacity: 0.6, Placement: "contain",
                DestX: 0.25, DestY: 0, DestWidth: 0.5, DestHeight: 1));

        Assert.True(updated);
        Assert.False(await session.UpdateCompositionVisualizerPlacementAsync(
            "missing", new VideoPlacementSpec("missing", 0)));
        Assert.False(viz.Disposed, "a placement edit must not replace/restart the visualizer source");
    }

    [Fact]
    public async Task StopAll_FadesVisualizerFromAuthoredOpacityBeforeDetach()
    {
        var opacities = new ConcurrentQueue<float>();
        await using var session = NewSurfaceSession(opacities);
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placement: new VideoPlacementSpec("screen", 10, Opacity: 0.8)));

        await WaitUntilAsync(() => opacities.Any(value => value >= 0.79f), TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        await session.StopAllAsync();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500), "soft stop should await its fade");
        Assert.True(viz.Disposed);
        Assert.False(await session.HasCompositionVisualizerAsync("screen"));
        var samples = opacities.ToArray();
        Assert.DoesNotContain(samples, value => value > 0.801f);
        Assert.Contains(samples, value => value is > 0.05f and < 0.75f);
    }

    [Fact]
    public async Task StopOneVisualizer_FadesBeforeDetach()
    {
        var opacities = new ConcurrentQueue<float>();
        await using var session = NewSurfaceSession(opacities);
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placement: new VideoPlacementSpec("screen", 10, Opacity: 0.65)));

        await WaitUntilAsync(() => opacities.Any(value => value >= 0.64f), TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        await session.FadeOutCompositionVisualizerAsync("screen");

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500));
        Assert.True(viz.Disposed);
        Assert.False(await session.HasCompositionVisualizerAsync("screen"));
        Assert.Contains(opacities, value => value is > 0.03f and < 0.6f);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.Elapsed < timeout)
            await Task.Delay(15);
        Assert.True(predicate(), "condition did not become true before timeout");
    }

    [Fact]
    public async Task MultiPlacement_AttachesOneLayerPerPlacement_AndUpdatesByIndex()
    {
        // #26 multi-placement: a visualizer cue can place the same source into several sections of ONE
        // canvas. Every placement must stay live, and a live-move must address exactly one of them.
        var opacities = new ConcurrentQueue<float>();
        await using var session = NewSurfaceSession(opacities);
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();

        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placements:
            [
                new VideoPlacementSpec("screen", 0, Opacity: 0.9, DestX: 0, DestY: 0, DestWidth: 0.5, DestHeight: 1),
                new VideoPlacementSpec("screen", 1, Opacity: 0.3, DestX: 0.5, DestY: 0, DestWidth: 0.5, DestHeight: 1),
            ]));
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));

        // The compositor must receive BOTH surface layers each tick, not just the last-attached one.
        await WaitUntilAsync(
            () => opacities.Any(v => Math.Abs(v - 0.9f) < 0.01f) && opacities.Any(v => Math.Abs(v - 0.3f) < 0.01f),
            TimeSpan.FromSeconds(2));

        // A live-move of the SECOND placement must leave the first one untouched.
        Assert.True(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen",
            new VideoPlacementSpec("screen", 1, Opacity: 0.5, DestX: 0.25, DestY: 0.25, DestWidth: 0.5, DestHeight: 0.5),
            placementIndex: 1));
        opacities.Clear();
        await WaitUntilAsync(
            () => opacities.Any(v => Math.Abs(v - 0.5f) < 0.01f) && opacities.Any(v => Math.Abs(v - 0.9f) < 0.01f),
            TimeSpan.FromSeconds(2));

        // Out-of-range placement indexes are refused rather than silently retargeting another layer.
        Assert.False(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen", new VideoPlacementSpec("screen", 2), placementIndex: 2));
        Assert.False(viz.Disposed);
    }

    [Fact]
    public async Task MultiPlacement_PersistentReattach_RecreatesEveryLayer()
    {
        var opacities = new ConcurrentQueue<float>();
        await using var session = NewSurfaceSession(opacities);
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz, preserveAcrossDocumentReload: true,
            placements:
            [
                new VideoPlacementSpec("screen", 0, Opacity: 0.8, DestWidth: 0.5, DestHeight: 1),
                new VideoPlacementSpec("screen", 1, Opacity: 0.4, DestX: 0.5, DestWidth: 0.5, DestHeight: 1),
            ]));

        // Full rebuild (output-topology change): the persistent slot recreates its surfaces on the
        // replacement composition - ALL of them, not just the first placement.
        await session.LoadDocumentAsync(CanvasDoc(), preserveMatchingCompositions: false);

        Assert.False(viz.Disposed);
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
        opacities.Clear();
        await WaitUntilAsync(
            () => opacities.Any(v => Math.Abs(v - 0.8f) < 0.01f) && opacities.Any(v => Math.Abs(v - 0.4f) < 0.01f),
            TimeSpan.FromSeconds(2));
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
