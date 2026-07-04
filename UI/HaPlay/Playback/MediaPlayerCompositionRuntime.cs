using Microsoft.Extensions.Logging;
using S.Media.Time;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Decode.FFmpeg.Video;
using S.Media.Session;
using S.Media.Present.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// Routes a media-player deck's video through the composition pipeline (default alternative to the per-output
/// <see cref="LogoFallbackVideoOutput"/> path): the decoder video is <strong>layer 0</strong> and the
/// hold/logo image is <strong>layer 1</strong> (on top), fanned to the deck's output lines by a
/// <see cref="ClipCompositionRuntime"/>. Unlike the per-output logo wrapper, the composition pump keeps every
/// output live on its own cadence, so "hold" is just raising the logo layer rather than a per-output template
/// pump. See <c>Doc/HaPlay-MediaPlayer-Compositions-Plan.md</c>.
/// </summary>
internal sealed class MediaPlayerCompositionRuntime : IDisposable
{
    private const string CompositionId = "mediaplayer";
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.MediaPlayerCompositionRuntime");

    private readonly ClipCompositionRuntime _composition;
    private readonly ClipCompositionRuntime.LayerSlot _videoLayer;
    private ClipCompositionRuntime.LayerSlot? _logoLayer;
    private VideoFrame? _logoTemplate;
    private bool _hold;
    private bool _disposed;

    /// <param name="canvasFormat">Composition canvas size/rate (locked output raster or source/program raster).</param>
    /// <param name="outputs">The deck's video output leases (local / NDI), fanned by the composition.</param>
    /// <param name="videoSourceFormat">The decoder's video format for the layer-0 source slot.</param>
    /// <param name="logoFrame">Optional hold/logo image placed on layer 1 (raised by <see cref="SetHold"/>).</param>
    /// <param name="compositorFactory">Optional compositor override (defaults to GL with CPU fallback).</param>
    public MediaPlayerCompositionRuntime(
        VideoFormat canvasFormat,
        IReadOnlyList<ClipCompositionOutputLease> outputs,
        VideoFormat videoSourceFormat,
        VideoFrame? logoFrame = null,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var definition = new ClipCompositionDefinition(
            CompositionId,
            "Media player",
            canvasFormat.Width,
            canvasFormat.Height,
            canvasFormat.FrameRate.Numerator,
            canvasFormat.FrameRate.Denominator);
        _composition = new ClipCompositionRuntime(definition, outputs, compositorFactory ?? CreateDefaultCompositor);

        // Layer 0 — the deck's video, full-frame inside the canvas. The local/NDI output may be resized
        // independently, so do not bake a cover-crop into the composition.
        _videoLayer = _composition.AddLayer(
            videoSourceFormat,
            new VideoPlacementSpec(CompositionId, LayerIndex: 0, Opacity: 1.0, Placement: "Letterbox",
                DestX: 0, DestY: 0, DestWidth: 1, DestHeight: 1));

        // Layer 1 — the hold/logo image, on top, hidden until SetHold(true). Submitted once; the compositor
        // retains the latest layer frame, so a static logo needs no continuous feed.
        if (logoFrame is not null)
            SetHoldFrame(logoFrame);
    }

    /// <summary>The layer-0 sink the deck's video router fans its decoded frames into.</summary>
    public IVideoOutput VideoSink => _videoLayer.Output;

    /// <summary>The composition canvas format (size the subtitle overlays render at).</summary>
    public VideoFormat CanvasFormat => _composition.CanvasFormat;

    /// <summary>Attaches a subtitle/overlay source as a top layer composited each frame at the deck's playhead
    /// (delegates to the framework runtime). Returns a handle that detaches + disposes the feed.</summary>
    public IDisposable AttachSubtitleOverlay(S.Media.Core.Video.IVideoOverlaySource source, Func<TimeSpan> positionProvider) =>
        _composition.AttachSubtitleOverlay(source, positionProvider);

    public int OutputCount => _composition.OutputCount;

    /// <summary>True when a hold/logo layer is present (hold has an effect).</summary>
    public bool HasHoldLayer => _logoLayer is not null;

    /// <summary>Raises (hold on) or clears (hold off) the logo layer over the video. No-op without a logo layer.</summary>
    public void SetHold(bool hold)
    {
        _hold = hold;
        if (_logoLayer is not null)
            _logoLayer.Opacity = hold ? 1f : 0f;
    }

    /// <summary>Replaces the hold/logo image. Takes ownership of <paramref name="logoFrame"/>.</summary>
    public void SetHoldFrame(VideoFrame? logoFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logoLayer?.Dispose();
        _logoLayer = null;
        _logoTemplate?.Dispose();
        _logoTemplate = null;

        if (logoFrame is null)
            return;

        try
        {
            _logoTemplate = VideoCpuFrameConverter.DuplicateCpuBacking(logoFrame, logoFrame.ColorTransferHint);
            _logoLayer = _composition.AddLayer(
                logoFrame.Format,
                new VideoPlacementSpec(CompositionId, LayerIndex: 1, Opacity: _hold ? 1.0 : 0.0, Placement: "Letterbox",
                    DestX: 0, DestY: 0, DestWidth: 1, DestHeight: 1));
            _logoLayer.Output.Configure(logoFrame.Format);
            VideoFrame? submitted = VideoCpuFrameConverter.DuplicateCpuBacking(_logoTemplate, _logoTemplate.ColorTransferHint);
            try
            {
                _logoLayer.Output.Submit(submitted);
                submitted = null;
            }
            finally
            {
                submitted?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "MediaPlayerCompositionRuntime: logo layer setup failed; continuing without a hold layer");
            _logoLayer?.Dispose();
            _logoLayer = null;
            _logoTemplate?.Dispose();
            _logoTemplate = null;
        }
        finally
        {
            logoFrame.Dispose();
        }
    }

    /// <summary>Per-deck video fade: layer-0 opacity (1 = full, 0 = black).</summary>
    public void SetVideoOpacity(float opacity) =>
        _videoLayer.Opacity = Math.Clamp(opacity, 0f, 1f);

    /// <summary>Paces the composition off the deck's master clock (and media timeline for frame selection).</summary>
    public void SetClockMaster(IPlaybackClock master, IPlayhead? timeline = null) =>
        _composition.SetClockMaster(master, timeline);

    public void EnsurePumpStarted() => _composition.EnsurePumpStarted();

    public bool RemoveOutput(string outputId) => _composition.RemoveOutput(outputId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logoLayer?.Dispose();
        _logoTemplate?.Dispose();
        _videoLayer.Dispose();
        _composition.Dispose();
    }

    private static ClipCompositionCompositor CreateDefaultCompositor(VideoFormat canvasFormat)
    {
        var requested = Environment.GetEnvironmentVariable("HAPLAY_MEDIAPLAYER_COMPOSITOR");
        if (string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase))
            return new ClipCompositionCompositor(
                new CpuVideoCompositor(canvasFormat),
                RequiresBgraLayerConversion: true,
                BackendName: "CPU");

        if (SDL3GLVideoCompositor.TryProbe(out var glError))
        {
            var gpu = new SDL3GLVideoCompositor(canvasFormat);
            return new ClipCompositionCompositor(
                gpu,
                RequiresBgraLayerConversion: false,
                BackendName: "OpenGL",
                DisposeOnDriverThread: gpu.DisposeOnOwnerThread);
        }

        if (string.Equals(requested, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "gpu", StringComparison.OrdinalIgnoreCase))
        {
            Trace.LogWarning(
                "MediaPlayerCompositionRuntime: OpenGL compositor requested but unavailable: {Error}; falling back to CPU",
                glError);
        }

        return new ClipCompositionCompositor(
            new CpuVideoCompositor(canvasFormat),
            RequiresBgraLayerConversion: true,
            BackendName: "CPU");
    }
}
