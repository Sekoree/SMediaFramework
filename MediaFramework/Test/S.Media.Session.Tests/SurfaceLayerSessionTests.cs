using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// NXT-10 end-to-end: a clip whose video source can render itself as a GPU layer surface composites
/// surface-side when the composition's compositor hosts surfaces — no frame fan-out — while transport
/// (fire, position, stop) behaves exactly like a frame-backed clip; on a CPU-only compositor the same
/// clip falls back to its normal frame path untouched.
/// </summary>
public sealed class SurfaceLayerSessionTests
{
    private sealed class RecordingSurface : IVideoCompositorLayerSurface
    {
        public readonly List<TimeSpan> RenderTimes = []; // recorded by the fake host below (no GL here)
        public bool Disposed;
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() => Disposed = true;
    }

    /// <summary>CPU compositing behind the surface-host capability; records each surface layer's pts
    /// (standing in for the GL render call, which needs a real context).</summary>
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
            TimeSpan presentationTime)
        {
            foreach (var layer in surfaceLayers)
                if (layer.Surface is RecordingSurface recording)
                    lock (recording.RenderTimes)
                        recording.RenderTimes.Add(presentationTime);
            return _inner.Composite(frameLayers, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A held video source that ALSO advertises the surface path (the MMD shape: software
    /// raster as fallback, GPU surface when the compositor hosts one).</summary>
    private sealed class SurfaceCapableVideoSource(TimeSpan duration)
        : IVideoSource, ILayerSurfaceVideoSource
    {
        private readonly UnboundedHeldVideoSource _frames = new(duration);
        public RecordingSurface? CreatedSurface;

        public VideoFormat Format => _frames.Format;
        public IReadOnlyList<PixelFormat> NativePixelFormats => _frames.NativePixelFormats;
        public bool IsExhausted => _frames.IsExhausted;
        public bool TryReadNextFrame(out VideoFrame frame) => _frames.TryReadNextFrame(out frame);
        public void SelectOutputFormat(PixelFormat format) => _frames.SelectOutputFormat(format);
        public void Dispose() { }

        public IVideoCompositorLayerSurface CreateLayerSurface() => CreatedSurface = new RecordingSurface();
    }

    private sealed class SurfaceCapableProvider(TimeSpan duration) : IMediaDecoderProvider
    {
        public readonly List<SurfaceCapableVideoSource> Opened = [];
        public string Name => "surface-capable";
        public double Probe(string uri, MediaKind kind) =>
            kind == MediaKind.Video && uri.StartsWith("surf://", StringComparison.Ordinal) ? 1.0 : 0.0;

        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
        {
            var source = new SurfaceCapableVideoSource(duration);
            lock (Opened)
                Opened.Add(source);
            return source;
        }

        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => throw new NotSupportedException();
    }

    private static ShowDocument SurfaceClipDoc() => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "Surface")],
        Clips:
        [
            new ShowClipBinding("c", "surf://x", CompositionId: "screen")
            {
                Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                AudioRoutes = [],
            },
        ],
        Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)], Routes: []);

    [Fact]
    public async Task SurfaceCapableClip_OnSurfaceHostingCompositor_CompositesGpuSide()
    {
        var provider = new SurfaceCapableProvider(TimeSpan.FromSeconds(30));
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(provider)),
            compositorFactory: fmt => new ClipCompositionCompositor(
                new FakeSurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));
        await session.LoadDocumentAsync(SurfaceClipDoc());

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        // The standby engine may open a second (warm) source; exactly ONE — the fired clip's — took
        // the surface path.
        RecordingSurface? surface;
        lock (provider.Opened)
            surface = Assert.Single(provider.Opened.Where(s => s.CreatedSurface is not null)).CreatedSurface;
        Assert.NotNull(surface);

        // The pump composites the surface with the transport timeline's SOURCE time — poll until at
        // least two composites landed, then confirm the coordinate advanced (transport is live).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (surface.RenderTimes)
                if (surface.RenderTimes.Count >= 3)
                    break;
            await Task.Delay(25);
        }

        TimeSpan first, last;
        lock (surface.RenderTimes)
        {
            Assert.True(surface.RenderTimes.Count >= 3, $"surface composited only {surface.RenderTimes.Count}x");
            first = surface.RenderTimes[0];
            last = surface.RenderTimes[^1];
        }
        Assert.True(last > first, $"surface time did not advance ({first} → {last})");

        // No frame layer was created for the clip (frame fan-out skipped entirely).
        var stats = await session.GetCompositionStatsAsync("screen");
        Assert.Equal(0, stats!.Value.LayerCount);
        Assert.True(Assert.Single(session.Snapshot()).IsActive);

        // Stop releases the clip and the RUNTIME disposes the surface it owns (poll — the stop's
        // fade/release tail lands one dispatcher op after the stop returns).
        await session.StopAllAsync();
        var stopDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!surface.Disposed && DateTime.UtcNow < stopDeadline)
            await Task.Delay(25);
        Assert.True(surface.Disposed, "stop did not dispose the placed surface");
    }

    [Fact]
    public async Task SurfaceCapableClip_OnCpuCompositor_FallsBackToTheFramePath()
    {
        var provider = new SurfaceCapableProvider(TimeSpan.FromSeconds(30));
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(provider))); // default CPU compositor — no surface host
        await session.LoadDocumentAsync(SurfaceClipDoc());

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        lock (provider.Opened)
            Assert.All(provider.Opened, s => Assert.Null(s.CreatedSurface)); // never asked for a surface

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var stats = await session.GetCompositionStatsAsync("screen");
            if (stats is { LayerCount: 1, FramesComposited: > 0 })
                return; // the normal frame path carried the clip
            await Task.Delay(25);
        }

        var final = await session.GetCompositionStatsAsync("screen");
        Assert.Fail($"frame path did not composite (layers={final?.LayerCount}, composited={final?.FramesComposited})");
    }
}
