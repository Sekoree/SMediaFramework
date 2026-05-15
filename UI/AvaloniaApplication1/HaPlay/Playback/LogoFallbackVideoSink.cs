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
    private volatile bool _holdFallback;
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
        _inner.Configure(format);
        _format = format;
        _configured = true;
    }

    public void SetHoldFallback(bool hold) => _holdFallback = hold;

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

        // Deep-copy each pass-through frame into a pool-backed cache so we can restore the visible
        // pixels after a hold-off toggle. Without this, single-frame sources (attached_pic / album
        // cover art) leave the receiver stuck on the no-longer-pumped template once hold turns off.
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
            // re-submit silently won't fire for those — real video paths refresh on the next decode tick.
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
