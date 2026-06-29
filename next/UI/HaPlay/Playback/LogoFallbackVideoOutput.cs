using System.Collections.Generic;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Decode.FFmpeg.Video;

namespace HaPlay.Playback;

/// <summary>
/// Wraps a video output and can substitute a static logo frame (for example during live faults).
/// Template pixels match the negotiated <see cref="VideoFormat"/> (including NV12 / UYVY, not only BGRA).
/// When hold is on and a template is set, decoded frames are dropped and the UI pumps
/// <see cref="SubmitTemplateFrame"/> so outputs stay live from the play clock alone.
/// Also keeps a deep-copied cache of the most recent real frame so <see cref="ResubmitLastCachedAt"/>
/// can restore the source after the user toggles hold off — important for single-frame sources
/// (audio with cover art) where the decoder doesn't produce more frames on its own.
/// </summary>
internal sealed class LogoFallbackVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly bool _disposeInner;
    private readonly object _logoGate = new();
    private VideoFormat _format;
    private bool _configured;
    private volatile bool _holdFallback;
    private volatile bool _holdEverEngaged;
    private volatile bool _singleFrameSourceMode;
    private VideoFrame? _holdTemplateSource;
    private VideoFrame? _holdTemplateRendered;
    private VideoFrame? _lastRealFrameCache;
    private volatile float _outputOpacity = 1f;
    private VideoCpuFrameConverter? _fromBgra;
    private bool _disposed;

    /// <summary>Linear opacity applied to decoded CPU frames (fade toward black / neutral chroma). 1 = pass-through.</summary>
    public void SetOutputOpacity(float opacity) => _outputOpacity = Math.Clamp(opacity, 0f, 1f);

    public LogoFallbackVideoOutput(IVideoOutput inner, bool disposeInnerOnDispose = true)
    {
        _inner = inner;
        _disposeInner = disposeInnerOnDispose;
    }

    public VideoFormat Format => _configured ? _format : throw new InvalidOperationException("Configure first");

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;
    internal IVideoOutput InnerOutput => _inner;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Configure(format);
        _format = format;
        _configured = true;
        RebuildRenderedHoldTemplate();
    }

    /// <summary>
    /// Legacy shim: compositor-based hold no longer reconfigures the wrapped output to image-native
    /// dimensions. The output format now stays at the negotiated playback format.
    /// </summary>
    public void ApplyImageOverrideFormat(VideoFormat? imageFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = imageFormat;
    }

    /// <summary>Compositor hold keeps the wrapped output at one negotiated output format.</summary>
    public bool IsImageOverrideActive => false;

    public void SetHoldFallback(bool hold)
    {
        if (hold)
            _holdEverEngaged = true;
        _holdFallback = hold;
    }

    /// <summary>
    /// Tells <see cref="Submit"/> to keep the cached "last real frame" refreshed so a later hold-toggle-off
    /// can re-show the source. Enable only for single-frame sources (attached_pic cover art, still images) —
    /// the cache is a per-frame ~8 MB deep-copy at 1080p BGRA and would cost ~500 MB/s for regular video.
    /// </summary>
    public void SetSingleFrameSourceMode(bool isSingleFrameSource) =>
        _singleFrameSourceMode = isSingleFrameSource;

    /// <summary>
    /// Pushes the configured template at <paramref name="presentationTime"/> (idle slate / no mux decode).
    /// </summary>
    public void SubmitTemplateFrame(TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? tpl;
        lock (_logoGate)
        {
            tpl = _holdTemplateRendered;
        }

        if (tpl is null)
            return;
        var logoFrame = new VideoFrame(
            presentationTime,
            tpl.Format,
            tpl.Planes,
            tpl.Strides,
            release: null,
            metadata: tpl.Metadata);
        _inner.Submit(logoFrame);
    }

    /// <summary>Submits a frame directly to the inner output (used for black priming; inner takes ownership / disposes).</summary>
    internal void SubmitBypassHold(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Submit(frame);
    }

    /// <summary>Replaces the hold template; disposes any previous template.</summary>
    public void TrySetHoldTemplate(VideoFrame? template)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? oldSource;
        VideoFrame? oldRendered;
        lock (_logoGate)
        {
            oldSource = _holdTemplateSource;
            oldRendered = _holdTemplateRendered;
            _holdTemplateSource = template;
            _holdTemplateRendered = null;
        }

        oldSource?.Dispose();
        oldRendered?.Dispose();
        RebuildRenderedHoldTemplate();
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_holdFallback)
        {
            VideoFrame? tpl;
            lock (_logoGate)
            {
                tpl = _holdTemplateRendered;
            }

            if (tpl is not null)
            {
                // Hold image is driven by <see cref="SubmitTemplateFrame"/> (idle slate + playback pump timer)
                // so outputs keep updating even when the mux sends no / few video frames (e.g. audio-heavy).
                frame.Dispose();
                return;
            }
        }

        // Cache strategy: snapshot the FIRST pass-through frame so single-frame sources (cover art,
        // still images) survive a future hold toggle. For sources that opt into single-frame mode,
        // also refresh the cache on every submit after hold has engaged once. For regular video,
        // skip the per-frame deep-copy entirely — the live decode loop will produce a current frame
        // on the next tick anyway, and 8 MB × frame-rate is too expensive (~500 MB/s at 1080p60).
        var shouldCache = _holdEverEngaged && _singleFrameSourceMode;
        if (!shouldCache)
        {
            lock (_logoGate)
                shouldCache = _lastRealFrameCache is null;
        }

        if (shouldCache)
        {
            try
            {
                var copy = VideoCpuFrameConverter.DuplicateCpuBacking(frame, frame.ColorTransferHint);
                lock (_logoGate)
                {
                    _lastRealFrameCache?.Dispose();
                    _lastRealFrameCache = copy;
                }
            }
            catch
            {
                // GPU-backed frames (DRM PRIME / D3D11 shared) can't be CPU-duplicated. The toggle-off
                // re-submit won't fire for those — real video paths refresh on the next decode tick.
            }
        }

        SubmitToInner(frame);
    }

    public void AbandonQueuedFrames()
    {
        if (_inner is IVideoOutputQueueControl control)
            control.AbandonQueuedFrames();
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _inner is IVideoOutputQueueControl control
            ? control.WaitForIdle(timeout, cancellationToken)
            : true;

    private void SubmitToInner(VideoFrame frame)
    {
        var opacity = _outputOpacity;
        if (opacity >= 0.999f || VideoCpuOpacity.IsHardwareBacked(frame))
        {
            _inner.Submit(frame);
            return;
        }

        try
        {
            var faded = ApplyOutputOpacityViaCompositor(frame, opacity);
            _inner.Submit(faded);
            frame.Dispose();
        }
        catch
        {
            _inner.Submit(frame);
        }
    }

    private void RebuildRenderedHoldTemplate()
    {
        VideoFrame? sourceCopy = null;
        VideoFormat target;
        lock (_logoGate)
        {
            target = _format;
            if (!_configured || _holdTemplateSource is null)
            {
                _holdTemplateRendered?.Dispose();
                _holdTemplateRendered = null;
                return;
            }

            sourceCopy = VideoCpuFrameConverter.DuplicateCpuBacking(
                _holdTemplateSource,
                _holdTemplateSource.ColorTransferHint);
        }

        try
        {
            var rendered = RenderTemplateToFormat(sourceCopy, target);
            lock (_logoGate)
            {
                _holdTemplateRendered?.Dispose();
                _holdTemplateRendered = rendered;
            }
        }
        finally
        {
            sourceCopy?.Dispose();
        }
    }

    private static VideoFrame? RenderTemplateToFormat(VideoFrame template, VideoFormat target)
    {
        VideoFrame? sourceForLayer = null;
        VideoFrame? renderedBgra = null;
        try
        {
            if (template.Format.PixelFormat == PixelFormat.Bgra32)
            {
                sourceForLayer = new VideoFrame(
                    TimeSpan.Zero,
                    template.Format,
                    template.Planes,
                    template.Strides,
                    release: null,
                    metadata: template.Metadata);
            }
            else
            {
                if (!VideoCpuFrameConverter.CanConvert(
                        template.Format.PixelFormat, PixelFormat.Bgra32, template.Format.Width, template.Format.Height))
                    return null;

                using var toBgra = new VideoCpuFrameConverter();
                toBgra.Configure(template.Format.PixelFormat, PixelFormat.Bgra32, template.Format.Width, template.Format.Height);
                using var converted = toBgra.Convert(template, template.ColorTransferHint);
                sourceForLayer = VideoCpuFrameConverter.DuplicateCpuBacking(converted, converted.ColorTransferHint);
            }

            var composedBgraFormat = new VideoFormat(target.Width, target.Height, PixelFormat.Bgra32, target.FrameRate);
            using var scaler = new CompositorOutputScaler(composedBgraFormat, LayerConfig.Background);
            if (!scaler.TryComposite(sourceForLayer, out renderedBgra) || renderedBgra is null)
                return null;
            sourceForLayer = null;

            if (target.PixelFormat == PixelFormat.Bgra32)
            {
                var direct = VideoCpuFrameConverter.DuplicateCpuBacking(renderedBgra, renderedBgra.ColorTransferHint);
                renderedBgra.Dispose();
                renderedBgra = null;
                return direct;
            }

            if (!VideoCpuFrameConverter.CanConvert(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height))
                return null;

            using var fromBgra = new VideoCpuFrameConverter();
            fromBgra.Configure(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height);
            using var convertedToTarget = fromBgra.Convert(renderedBgra, renderedBgra.ColorTransferHint);
            var result = VideoCpuFrameConverter.DuplicateCpuBacking(convertedToTarget, convertedToTarget.ColorTransferHint);
            renderedBgra.Dispose();
            renderedBgra = null;
            return result;
        }
        finally
        {
            sourceForLayer?.Dispose();
            renderedBgra?.Dispose();
        }
    }

    private VideoFrame ApplyOutputOpacityViaCompositor(VideoFrame frame, float opacity)
    {
        var fmt = frame.Format;
        var bgraOut = new VideoFormat(fmt.Width, fmt.Height, PixelFormat.Bgra32, fmt.FrameRate);
        var fadeConfig = new LayerConfig(LayerPosition.Center, 1f, opacity);
        using var scaler = new CompositorOutputScaler(bgraOut, fadeConfig);
        if (!scaler.TryComposite(frame, out var bgra) || bgra is null)
            throw new NotSupportedException($"Cannot fade {fmt.PixelFormat} via compositor.");

        if (fmt.PixelFormat == PixelFormat.Bgra32)
            return bgra;

        if (!VideoCpuFrameConverter.CanConvert(PixelFormat.Bgra32, fmt.PixelFormat, fmt.Width, fmt.Height))
            throw new NotSupportedException($"Cannot convert faded BGRA back to {fmt.PixelFormat}.");

        _fromBgra ??= new VideoCpuFrameConverter();
        _fromBgra.Configure(PixelFormat.Bgra32, fmt.PixelFormat, fmt.Width, fmt.Height);
        var restored = _fromBgra.Convert(bgra, frame.ColorTransferHint);
        bgra.Dispose();
        return restored;
    }

    /// <summary>
    /// Submits an alias of the cached last real frame at <paramref name="presentationTime"/>. Use after
    /// toggling hold off so receivers see the source content again at the current playhead (cover art
    /// re-show without re-decoding).
    /// </summary>
    public void ResubmitLastCachedAt(TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? cache;
        lock (_logoGate)
        {
            cache = _lastRealFrameCache;
        }

        if (cache is null)
            return;

        // Alias the cached planes (release: null) — the cache stays alive and owns the buffers; the
        // alias is consumed by inner (NDI deep-copies into staging, then Dispose runs the no-op release).
        var alias = new VideoFrame(
            presentationTime,
            cache.Format,
            cache.Planes,
            cache.Strides,
            release: null,
            metadata: cache.Metadata);

        try
        {
            _inner.Submit(alias);
        }
        catch
        {
            alias.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_logoGate)
        {
            _holdTemplateSource?.Dispose();
            _holdTemplateSource = null;
            _holdTemplateRendered?.Dispose();
            _holdTemplateRendered = null;
            _lastRealFrameCache?.Dispose();
            _lastRealFrameCache = null;
        }

        if (_disposeInner && _inner is IDisposable d)
            d.Dispose();

        _fromBgra?.Dispose();
        _fromBgra = null;
    }
}
