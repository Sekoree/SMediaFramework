using System.Collections.Generic;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace HaPlay.Playback;

/// <summary>
/// Wraps a video sink and can substitute a static logo frame (for example during live faults).
/// Template pixels match the negotiated <see cref="VideoFormat"/> (including NV12 / UYVY, not only BGRA).
/// When hold is on and a template is set, decoded frames are dropped and the UI pumps
/// <see cref="SubmitTemplateFrame"/> so outputs stay live from the play clock alone.
/// Also keeps a deep-copied cache of the most recent real frame so <see cref="ResubmitLastCachedAt"/>
/// can restore the source after the user toggles hold off — important for single-frame sources
/// (audio with cover art) where the decoder doesn't produce more frames on its own.
/// </summary>
internal sealed class LogoFallbackVideoSink : IVideoSink, IDisposable
{
    private readonly IVideoSink _inner;
    private readonly bool _disposeInner;
    private readonly object _logoGate = new();
    private VideoFormat _format;
    private bool _configured;
    private VideoFormat? _decoderFormat;
    private volatile bool _imageOverrideActive;
    private volatile bool _holdFallback;
    private volatile bool _holdEverEngaged;
    private volatile bool _singleFrameSourceMode;
    private VideoFrame? _holdTemplate;
    private VideoFrame? _lastRealFrameCache;
    private bool _disposed;

    public LogoFallbackVideoSink(IVideoSink inner, bool disposeInnerOnDispose = true)
    {
        _inner = inner;
        _disposeInner = disposeInnerOnDispose;
    }

    public VideoFormat Format => _configured ? _format : throw new InvalidOperationException("Configure first");

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // When an image override is in effect, the inner sink stays at the image's format and we just
        // remember the decoder's negotiated format so we can revert when the override clears. Decoded
        // frames are still dropped by Submit while hold is on.
        _decoderFormat = format;
        if (_imageOverrideActive)
        {
            _configured = true;
            return;
        }
        _inner.Configure(format);
        _format = format;
        _configured = true;
    }

    /// <summary>
    /// Phase 3 — switches the wrapped sink to an image-native format (e.g. the dimensions of the
    /// user-supplied hold image). Subsequent template pushes use the image format; decoded frames are
    /// already dropped while hold is on. Pass <c>null</c> to revert to the decoder's format.
    /// </summary>
    public void ApplyImageOverrideFormat(VideoFormat? imageFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (imageFormat is null)
        {
            if (!_imageOverrideActive)
                return;
            _imageOverrideActive = false;
            if (_decoderFormat is { } df)
            {
                _inner.Configure(df);
                _format = df;
            }
            return;
        }

        _imageOverrideActive = true;
        _inner.Configure(imageFormat.Value);
        _format = imageFormat.Value;
        _configured = true;
    }

    /// <summary>True while the wrapped sink is presenting at the image-override format.</summary>
    public bool IsImageOverrideActive => _imageOverrideActive;

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
            tpl = _holdTemplate;
        }

        if (tpl is null)
            return;
        var logoFrame = new VideoFrame(
            presentationTime,
            tpl.Format,
            tpl.Planes,
            tpl.Strides,
            tpl.ColorTransferHint,
            release: null);
        _inner.Submit(logoFrame);
    }

    /// <summary>Submits a frame directly to the inner sink (used for black priming; inner takes ownership / disposes).</summary>
    internal void SubmitBypassHold(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Submit(frame);
    }

    /// <summary>Replaces the hold template; disposes any previous template.</summary>
    public void TrySetHoldTemplate(VideoFrame? template)
    {
        lock (_logoGate)
        {
            _holdTemplate?.Dispose();
            _holdTemplate = template;
        }
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_holdFallback)
        {
            VideoFrame? tpl;
            lock (_logoGate)
            {
                tpl = _holdTemplate;
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

        _inner.Submit(frame);
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
            cache.ColorTransferHint,
            release: null);

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
            _holdTemplate?.Dispose();
            _holdTemplate = null;
            _lastRealFrameCache?.Dispose();
            _lastRealFrameCache = null;
        }

        if (_disposeInner && _inner is IDisposable d)
            d.Dispose();
    }
}
