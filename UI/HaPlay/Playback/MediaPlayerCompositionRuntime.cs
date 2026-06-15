using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using S.Media.Playback;

namespace HaPlay.Playback;

/// <summary>
/// Routes a media-player deck's video through the composition pipeline (opt-in alternative to the per-output
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
    private readonly ClipCompositionRuntime.LayerSlot? _logoLayer;
    private bool _disposed;

    /// <param name="canvasFormat">Composition canvas size/rate (from the sizing rules — output res or 1080p).</param>
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
        _composition = new ClipCompositionRuntime(definition, outputs, compositorFactory);

        // Layer 0 — the deck's video, covering the canvas. The decoder feeds this via VideoSink.
        _videoLayer = _composition.AddLayer(
            videoSourceFormat,
            new VideoPlacementSpec(CompositionId, LayerIndex: 0, Opacity: 1.0, Placement: "Cover",
                DestX: 0, DestY: 0, DestWidth: 1, DestHeight: 1));

        // Layer 1 — the hold/logo image, on top, hidden until SetHold(true). Submitted once; the compositor
        // retains the latest layer frame, so a static logo needs no continuous feed.
        if (logoFrame is not null)
        {
            try
            {
                _logoLayer = _composition.AddLayer(
                    logoFrame.Format,
                    new VideoPlacementSpec(CompositionId, LayerIndex: 1, Opacity: 0.0, Placement: "Letterbox",
                        DestX: 0, DestY: 0, DestWidth: 1, DestHeight: 1));
                _logoLayer.Output.Configure(logoFrame.Format);
                _logoLayer.Output.Submit(VideoCpuFrameConverter.DuplicateCpuBacking(logoFrame, logoFrame.ColorTransferHint));
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "MediaPlayerCompositionRuntime: logo layer setup failed; continuing without a hold layer");
                _logoLayer?.Dispose();
                _logoLayer = null;
            }
        }
    }

    /// <summary>The layer-0 sink the deck's video router fans its decoded frames into.</summary>
    public IVideoOutput VideoSink => _videoLayer.Output;

    /// <summary>True when a hold/logo layer is present (hold has an effect).</summary>
    public bool HasHoldLayer => _logoLayer is not null;

    /// <summary>Raises (hold on) or clears (hold off) the logo layer over the video. No-op without a logo layer.</summary>
    public void SetHold(bool hold)
    {
        if (_logoLayer is not null)
            _logoLayer.Opacity = hold ? 1f : 0f;
    }

    /// <summary>Per-deck video fade: layer-0 opacity (1 = full, 0 = black).</summary>
    public void SetVideoOpacity(float opacity) =>
        _videoLayer.Opacity = Math.Clamp(opacity, 0f, 1f);

    /// <summary>Paces the composition off the deck's master clock (and media timeline for frame selection).</summary>
    public void SetClockMaster(IPlaybackClock master, IPlayhead? timeline = null) =>
        _composition.SetClockMaster(master, timeline);

    public void EnsurePumpStarted() => _composition.EnsurePumpStarted();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logoLayer?.Dispose();
        _videoLayer.Dispose();
        _composition.Dispose();
    }
}
