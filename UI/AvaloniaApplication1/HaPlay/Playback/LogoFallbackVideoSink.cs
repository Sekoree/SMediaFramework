using System.Collections.Generic;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Wraps a video sink and can substitute a static logo frame (for example during live faults).
/// Template pixels match the negotiated <see cref="VideoFormat"/> (including NV12 / UYVY, not only BGRA).
/// When hold is on and a template is set, decoded frames are dropped and the UI pumps
/// <see cref="SubmitTemplateFrame"/> so outputs stay live from the play clock alone.
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

        _inner.Submit(frame);
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
        }

        if (_disposeInner && _inner is IDisposable d)
            d.Dispose();
    }
}
