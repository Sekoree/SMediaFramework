using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Compositor;
using Microsoft.Extensions.Logging;

namespace HaPlay.Playback;

/// <summary>
/// Phase C polish (§4.3.5 follow-up / framework gap "NDI pixel-format / resolution lock") - per-branch
/// <see cref="IVideoOutput"/> wrapper that pins the negotiated pixel format and/or dimensions an NDI
/// (or any) output presents to its receivers, regardless of what the source produces.
/// </summary>
internal sealed class LockedFormatVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.LockedFormatVideoOutput");

    private readonly IVideoOutput _inner;
    private readonly bool _disposeInnerOnDispose;
    private readonly PixelFormat? _pixelFormatLock;
    private readonly int? _resolutionLockWidth;
    private readonly int? _resolutionLockHeight;
    private readonly string _name;

    private CompositorOutputScaler? _scaler;
    private VideoFormat _negotiatedFormat;
    private VideoFormat _innerFormat;
    private bool _configured;
    private bool _disposed;

    public LockedFormatVideoOutput(
        IVideoOutput inner,
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

        if (_scaler is not null && _innerFormat == target)
        {
            _inner.Configure(target);
            _configured = true;
            return;
        }

        DisposeScaler();
        _scaler = new CompositorOutputScaler(target, LayerConfig.Background);
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
            throw new InvalidOperationException("LockedFormatVideoOutput.Submit called before Configure");
        }

        if (_scaler is null)
        {
            _inner.Submit(frame);
            return;
        }

        if (!_scaler.TryComposite(frame, out var scaled) || scaled is null)
        {
            frame.Dispose();
            return;
        }

        frame.Dispose();
        _inner.Submit(scaled);
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

    private void DisposeScaler()
    {
        var s = _scaler;
        _scaler = null;
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
