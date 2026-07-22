using System.Collections.Concurrent;
using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// A visualizer placement's color-stage effects (chroma key, brightness/contrast) must reach the
/// compositor's surface layer exactly like a media placement's reach its frame layer — the
/// 2026-07-21 report was chroma key/color adjust silently ignored on visualizer cues because the
/// placement chain dropped them before the surface slot (which also never built the chain).
/// </summary>
public sealed class VisualizerSurfaceEffectTests
{
    private sealed class MinimalSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    /// <summary>Surface-hosting CPU compositor recording each composited surface layer's effect
    /// chain and mapping sections.</summary>
    private sealed class EffectRecordingSurfaceHost(VideoFormat output) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public readonly ConcurrentQueue<IReadOnlyList<VideoLayerEffect>?> EffectSnapshots = new();
        public readonly ConcurrentQueue<IReadOnlyList<WarpSection>?> MappingSnapshots = new();
        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);

        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime)
        {
            foreach (var layer in surfaceLayers)
            {
                EffectSnapshots.Enqueue(layer.Effects);
                MappingSnapshots.Enqueue(layer.MappingSections);
            }
            return _inner.Composite(frameLayers, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FakeVisualizer : IAudioVisualSource, ILayerSurfaceVideoSource, IDisposable
    {
        public VideoFormat Format => new(64, 64, PixelFormat.Bgra32, new Rational(60, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats => [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public bool TryReadNextFrame(out VideoFrame frame) { frame = null!; return false; }
        public void SelectOutputFormat(PixelFormat format) { }
        AudioFormat IAudioOutput.Format => new(48_000, 2);
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public IVideoCompositorLayerSurface CreateLayerSurface() => new MinimalSurface();
        public void Dispose() { }
    }

    private static ShowDocument CanvasDoc() => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 128, 72, 60, 1)],
        Routes: []);

    [Fact]
    public async Task VisualizerPlacementWithChromaKey_ReachesSurfaceLayerAsEffectChain()
    {
        var host = default(EffectRecordingSurfaceHost);
        await using var session = new ShowSession(
            MediaRegistry.Build(_ => { }),
            compositorFactory: fmt => new ClipCompositionCompositor(
                host = new EffectRecordingSurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placement: new VideoPlacementSpec(
                "screen", 1,
                ChromaKey: ChromaKeySettings.GreenScreen,
                ColorAdjust: new Compositor.Effects.BrightnessContrastSettings(0.1f, 1.2f))));

        await WaitUntilAsync(() => !host!.EffectSnapshots.IsEmpty, TimeSpan.FromSeconds(2));
        Assert.True(host!.EffectSnapshots.TryDequeue(out var effects));
        Assert.NotNull(effects);
        // Chroma key must run FIRST (keying sees original colors), then brightness/contrast.
        Assert.Equal(2, effects.Count);
        Assert.Equal(Compositor.Effects.ChromaKeyVideoEffect.EffectId, effects[0].Descriptor.Id);
        Assert.Equal(Compositor.Effects.BrightnessContrastVideoEffect.EffectId, effects[1].Descriptor.Id);
    }

    [Fact]
    public async Task VisualizerPlacementUpdate_SwapsAndClearsTheEffectChain()
    {
        var host = default(EffectRecordingSurfaceHost);
        await using var session = new ShowSession(
            MediaRegistry.Build(_ => { }),
            compositorFactory: fmt => new ClipCompositionCompositor(
                host = new EffectRecordingSurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placement: new VideoPlacementSpec("screen", 1, ChromaKey: ChromaKeySettings.GreenScreen)));
        await WaitUntilAsync(() => host!.EffectSnapshots.Any(e => e is { Count: 1 }), TimeSpan.FromSeconds(2));

        // Live edit removing the key (Effects tab toggle) must clear the chain on the running surface.
        Assert.True(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen", new VideoPlacementSpec("screen", 1)));
        host!.EffectSnapshots.Clear();
        await WaitUntilAsync(() => host.EffectSnapshots.Any(e => e is null), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task VisualizerPlacementWithVideoFx_ReachesSurfaceLayerAsMappingSections()
    {
        var host = default(EffectRecordingSurfaceHost);
        await using var session = new ShowSession(
            MediaRegistry.Build(_ => { }),
            compositorFactory: fmt => new ClipCompositionCompositor(
                host = new EffectRecordingSurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));
        await session.LoadDocumentAsync(CanvasDoc());

        var viz = new FakeVisualizer();
        var mapping = new ClipOutputMappingSpec(
            Sections:
            [
                new ClipOutputMappingSection(
                    "s1", Enabled: true,
                    SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                    DestX: 0, DestY: 0, DestWidth: 64, DestHeight: 72,
                    RotationDegrees: 0, Opacity: 1, Brightness: 1,
                    MeshColumns: 0, MeshRows: 0, MeshPoints: null),
                new ClipOutputMappingSection(
                    "s2", Enabled: true,
                    SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                    DestX: 64, DestY: 0, DestWidth: 64, DestHeight: 72,
                    RotationDegrees: 0, Opacity: 0.5, Brightness: 1,
                    MeshColumns: 0, MeshRows: 0, MeshPoints: null),
            ],
            OutputWidth: 128,
            OutputHeight: 72);
        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz,
            placement: new VideoPlacementSpec("screen", 1, VideoFx: mapping)));

        await WaitUntilAsync(() => !host!.MappingSnapshots.IsEmpty, TimeSpan.FromSeconds(2));
        Assert.True(host!.MappingSnapshots.TryDequeue(out var sections));
        Assert.NotNull(sections);
        Assert.Equal(2, sections.Count);
        Assert.Equal(0.5f, sections[1].Opacity, 3);

        // Live edit clearing VideoFx must drop the sections and return to the direct path.
        Assert.True(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen", new VideoPlacementSpec("screen", 1)));
        host.MappingSnapshots.Clear();
        await WaitUntilAsync(() => host.MappingSnapshots.Any(s => s is null), TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within timeout");
            await Task.Delay(20);
        }
    }
}
