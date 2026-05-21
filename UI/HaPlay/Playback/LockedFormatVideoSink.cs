using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Microsoft.Extensions.Logging;

namespace HaPlay.Playback;

/// <summary>
/// Phase C polish (§4.3.5 follow-up / framework gap "NDI pixel-format / resolution lock") — per-branch
/// <see cref="IVideoSink"/> wrapper that pins the negotiated pixel format and/or dimensions an NDI
/// (or any) sink presents to its receivers, regardless of what the source produces.
/// </summary>
/// <remarks>
/// <para>
/// The UI side stores <c>PixelFormatLock</c> + <c>ResolutionLockWidth</c> / <c>Height</c> on
/// <see cref="HaPlay.Models.NDIOutputDefinition"/> and round-trips them through the project file
/// (Phase A forward-compat). This wrapper is the runtime-side honour: <see cref="AcceptedPixelFormats"/>
/// constrains the <see cref="VideoFormatNegotiator"/> to the lock, and <see cref="Submit"/>
/// letterboxes incoming frames into the locked raster via a <see cref="CompositorVideoSink"/> +
/// <see cref="CpuVideoCompositor"/> (same pattern as <see cref="OutputPresetVideoSource"/> but on the
/// sink side so it can be applied per-NDI-output without affecting other branches).
/// </para>
/// <para>
/// When the lock isn't accepted by the inner sink the wrapper degrades gracefully: the sink keeps
/// reporting the inner's full preference list and frames pass straight through. This means saving an
/// NDI lock for a format the inner sink later drops doesn't silently break playback.
/// </para>
/// </remarks>
internal sealed class LockedFormatVideoSink : IVideoSink, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.LockedFormatVideoSink");

    private readonly IVideoSink _inner;
    private readonly bool _disposeInnerOnDispose;
    private readonly PixelFormat? _pixelFormatLock;
    private readonly int? _resolutionLockWidth;
    private readonly int? _resolutionLockHeight;
    private readonly string _name;

    private CompositorVideoSink? _scaler;
    private CompositorVideoSink.Slot? _scalerSlot;
    private VideoFormat _negotiatedFormat;
    private VideoFormat _innerFormat;
    private bool _configured;
    private bool _disposed;

    public LockedFormatVideoSink(
        IVideoSink inner,
        PixelFormat? pixelFormatLock,
        int? resolutionLockWidth,
        int? resolutionLockHeight,
        string name,
        bool disposeInnerOnDispose)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _pixelFormatLock = pixelFormatLock;
        _resolutionLockWidth = resolutionLockWidth;
        _resolutionLockHeight = resolutionLockHeight;
        _name = name;
        _disposeInnerOnDispose = disposeInnerOnDispose;
    }

    /// <summary>When the pixel-format lock is active AND the inner sink accepts it, present a
    /// one-element list so the format negotiator must pick the locked format. Otherwise fall through
    /// to the inner sink's full preference list (graceful degradation).</summary>
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats
    {
        get
        {
            if (_pixelFormatLock is not { } pf)
                return _inner.AcceptedPixelFormats;
            var innerAccepted = _inner.AcceptedPixelFormats;
            for (var i = 0; i < innerAccepted.Count; i++)
                if (innerAccepted[i] == pf)
                    return [pf];
            return _inner.AcceptedPixelFormats;
        }
    }

    public VideoFormat Format => _configured ? _innerFormat : _inner.Format;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _negotiatedFormat = format;

        var lockedW = _resolutionLockWidth ?? format.Width;
        var lockedH = _resolutionLockHeight ?? format.Height;
        var lockedPf = _pixelFormatLock ?? format.PixelFormat;

        // No actual constraint different from the source: pass through.
        if (lockedW == format.Width && lockedH == format.Height && lockedPf == format.PixelFormat)
        {
            DisposeScaler();
            _innerFormat = format;
            _inner.Configure(format);
            _configured = true;
            Trace.LogDebug("Configure: name={Name} pass-through {Format}", _name, format);
            return;
        }

        var target = new VideoFormat(lockedW, lockedH, lockedPf, format.FrameRate);

        // Reuse the existing scaler when the in/out shape hasn't changed (router re-Configures the
        // primary on every TryAddRoute — see [[video_sink_pump_reconfigure]]).
        if (_scaler is not null && _scalerSlot is not null
            && _innerFormat == target
            && _scalerSlot.Sink.Format == format)
        {
            _inner.Configure(target);
            _configured = true;
            return;
        }

        DisposeScaler();

        var compositor = new CpuVideoCompositor(target);
        _scaler = new CompositorVideoSink(target, compositor, disposeCompositorOnDispose: true);
        _scalerSlot = _scaler.AddSlot();
        _scalerSlot.Transform = OutputPresetFormats.LetterboxTransform(format, target);
        _scalerSlot.Sink.Configure(format);

        _innerFormat = target;
        _inner.Configure(target);
        _configured = true;
        Trace.LogInformation("Configure: name={Name} lock applied {Src} → {Dst}", _name, format, target);
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("LockedFormatVideoSink.Submit called before Configure");
        }

        // Pass-through: no scaler installed.
        if (_scaler is null || _scalerSlot is null)
        {
            _inner.Submit(frame);
            return;
        }

        // Slot takes ownership of the source frame; pull the composited result and forward.
        _scalerSlot.Sink.Submit(frame);
        if (_scaler.TryReadNextFrame(out var scaled))
            _inner.Submit(scaled);
    }

    private void DisposeScaler()
    {
        var s = _scaler;
        _scaler = null;
        _scalerSlot = null;
        if (s is null) return;
        try { s.Dispose(); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeScaler();
        if (_disposeInnerOnDispose)
        {
            try { (_inner as IDisposable)?.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
