using S.Media.Core;

namespace S.Media.Core.Video;

/// <summary>
/// Rewrites each submitted frame's presentation time by a fixed <paramref name="ptsOffset"/> before
/// forwarding it downstream. Retiming is useful wherever a source timeline must be mapped onto a
/// different output timeline: clip/trim windows (rebase a clip that starts mid-source to t=0),
/// cue/soundboard players, composition slots, loop regions, and media fragments.
/// </summary>
/// <remarks>
/// The offset is <b>added</b> to every frame's PTS, so pass a negative offset to pull a clip that
/// starts mid-source back to a zero-based timeline (e.g. <c>-clipStart</c>). With
/// <see cref="_clampNegativeToZero"/> (the default) a resulting negative PTS is floored to zero so a
/// rebased clip can't present "before the start" on a master-aligned consumer.
///
/// Frame ownership transfers to <see cref="Submit"/>: it rewrites the PTS on a shared reference
/// (zero-copy for hardware/dmabuf backings) and disposes the original, matching
/// <see cref="IVideoOutput"/> ownership semantics.
/// </remarks>
public sealed class RetimingVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly TimeSpan _ptsOffset;
    private readonly bool _clampNegativeToZero;
    private readonly bool _disposeInner;
    private bool _disposed;

    /// <param name="inner">Downstream output the retimed frames are forwarded to.</param>
    /// <param name="ptsOffset">Amount added to each frame's presentation time. Negative values shift
    /// frames earlier (the clip-rebase case).</param>
    /// <param name="clampNegativeToZero">When true (default), a resulting PTS below zero is floored to
    /// <see cref="TimeSpan.Zero"/>.</param>
    /// <param name="disposeInner">When true, disposing this also disposes <paramref name="inner"/>.</param>
    public RetimingVideoOutput(
        IVideoOutput inner,
        TimeSpan ptsOffset,
        bool clampNegativeToZero = true,
        bool disposeInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ptsOffset = ptsOffset;
        _clampNegativeToZero = clampNegativeToZero;
        _disposeInner = disposeInner;
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public VideoFormat Format => _inner.Format;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (_ptsOffset == TimeSpan.Zero)
        {
            _inner.Submit(frame);
            return;
        }

        var retimed = frame.PresentationTime + _ptsOffset;
        if (_clampNegativeToZero && retimed < TimeSpan.Zero)
            retimed = TimeSpan.Zero;

        var rebased = RewritePts(frame, retimed);
        try
        {
            _inner.Submit(rebased);
        }
        catch
        {
            rebased.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_disposeInner && _inner is IDisposable d)
            d.Dispose();
    }

    public void AbandonQueuedFrames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inner is IVideoOutputQueueControl control)
            control.AbandonQueuedFrames();
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inner is not IVideoOutputQueueControl control)
            return true;
        return control.WaitForIdle(timeout, cancellationToken);
    }

    private static VideoFrame RewritePts(VideoFrame original, TimeSpan newPts)
    {
        if (original.DmabufNv12 is not null)
        {
            var dup = original.DmabufNv12.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.DmabufP010 is not null)
        {
            var dup = original.DmabufP010.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.DmabufP016 is not null)
        {
            var dup = original.DmabufP016.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.Win32Nv12 is not null && OperatingSystem.IsWindows())
        {
            var dup = original.Win32Nv12.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }

        return new VideoFrame(
            newPts,
            original.Format,
            original.Planes,
            original.Strides,
            original.Metadata,
            release: DisposableRelease.Wrap(original.Dispose));
    }
}
