using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Routes one decoded video stream to a <strong>primary</strong> sink (full
/// <see cref="IVideoSink.AcceptedPixelFormats"/> for negotiation) plus a
/// <strong>branch</strong> sink. When the branch cannot accept the negotiated
/// primary pixel format, FFmpeg <see cref="VideoCpuFrameConverter"/> performs
/// a per-frame <c>sws_scale</c> conversion so limited sinks (for example NDI)
/// still receive valid pixels instead of black frames.
/// </summary>
/// <remarks>
/// <para>
/// <strong>CPU NV12</strong> with no conversion uses <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> so the primary and branch
/// share one plane copy (refcounted release) instead of <see cref="VideoCpuFrameConverter.DuplicateCpuBacking"/>.
/// </para>
/// <para>
/// Negotiation uses only the primary sink's format list — the local GL/CPU
/// window can keep high bit-depth YUV while the branch receives packed RGB
/// or subsampled YUV depending on <see cref="IVideoSink.AcceptedPixelFormats"/>.
/// </para>
/// <para>
/// <paramref name="disposeBranch"/> should be <c>true</c> only when this router
/// owns the branch sink; NDI child sinks stay owned by <c>NDIOutput</c> (pass
/// <c>false</c>, mirroring the old smoke-tool twin pattern).
/// </para>
/// <para>
/// <strong>DRM dma-buf NV12</strong> is supported on both sinks when the branch
/// negotiates the same <see cref="PixelFormat.Nv12"/> (no conversion). The same applies to
/// <see cref="PixelFormat.P010"/> and <see cref="PixelFormat.P016"/> when both sinks stay on that format.
/// Otherwise use CPU decode or omit the branch.
/// </para>
/// </remarks>
public sealed class VideoOutputRouter : IVideoSink, IDisposable
{
    private readonly IVideoSink _primary;
    private readonly IVideoSink _branch;
    private readonly bool _disposeBranch;
    private VideoCpuFrameConverter? _converter;
    private bool _needsConversion;
    private bool _configured;
    private bool _disposed;

    public VideoOutputRouter(IVideoSink primary, IVideoSink branch, bool disposeBranch = false)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _branch = branch ?? throw new ArgumentNullException(nameof(branch));
        _disposeBranch = disposeBranch;
        AcceptedPixelFormats = primary.AcceptedPixelFormats;
    }

    /// <inheritdoc cref="IVideoSink.AcceptedPixelFormats"/>
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; }

    public VideoFormat Format => _primary.Format;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _primary.Configure(format);

        var branchFmt = VideoSinkFanoutFormats.PickBranchPixelFormat(format, _branch.AcceptedPixelFormats);
        _needsConversion = branchFmt != format.PixelFormat;
        if (_needsConversion)
        {
            _converter ??= new VideoCpuFrameConverter();
            _converter.Configure(format.PixelFormat, branchFmt, format.Width, format.Height);
        }
        else
        {
            _converter?.Dispose();
            _converter = null;
        }

        _branch.Configure(new VideoFormat(format.Width, format.Height, branchFmt, format.FrameRate));
        _configured = true;
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("VideoOutputRouter.Submit called before Configure");
        }

        if (frame.DmabufNv12 is not null)
        {
            if (_needsConversion)
            {
                frame.Dispose();
                throw new NotSupportedException(
                    "VideoOutputRouter cannot convert DRM dma-buf NV12 for the branch — use a branch that accepts NV12 or CPU decode.");
            }

            VideoFrame? branchDma = null;
            try
            {
                branchDma = VideoFrame.CreateNv12DmabufSharedReference(frame, frame.ColorTransferHint);
                _primary.Submit(frame);
                frame = null!;
                _branch.Submit(branchDma);
                branchDma = null;
            }
            catch
            {
                branchDma?.Dispose();
                frame?.Dispose();
                throw;
            }

            return;
        }

        if (frame.DmabufP010 is not null)
        {
            if (_needsConversion)
            {
                frame.Dispose();
                throw new NotSupportedException(
                    "VideoOutputRouter cannot convert DRM dma-buf P010 for the branch — use a branch that accepts P010 or CPU decode.");
            }

            VideoFrame? branchDma = null;
            try
            {
                branchDma = VideoFrame.CreateP010DmabufSharedReference(frame, frame.ColorTransferHint);
                _primary.Submit(frame);
                frame = null!;
                _branch.Submit(branchDma);
                branchDma = null;
            }
            catch
            {
                branchDma?.Dispose();
                frame?.Dispose();
                throw;
            }

            return;
        }

        if (frame.DmabufP016 is not null)
        {
            if (_needsConversion)
            {
                frame.Dispose();
                throw new NotSupportedException(
                    "VideoOutputRouter cannot convert DRM dma-buf P016 for the branch — use a branch that accepts P016 or CPU decode.");
            }

            VideoFrame? branchDma = null;
            try
            {
                branchDma = VideoFrame.CreateP016DmabufSharedReference(frame, frame.ColorTransferHint);
                _primary.Submit(frame);
                frame = null!;
                _branch.Submit(branchDma);
                branchDma = null;
            }
            catch
            {
                branchDma?.Dispose();
                frame?.Dispose();
                throw;
            }

            return;
        }

        if (frame.Win32Nv12 is not null)
        {
            if (_needsConversion)
            {
                frame.Dispose();
                throw new NotSupportedException(
                    "VideoOutputRouter cannot convert Win32 D3D11 shared-handle NV12 for the branch — use a branch that accepts NV12 or CPU decode.");
            }

            VideoFrame? branchWin = null;
            try
            {
                branchWin = VideoFrame.CreateNv12Win32SharedReference(frame, frame.ColorTransferHint);
                _primary.Submit(frame);
                frame = null!;
                _branch.Submit(branchWin);
                branchWin = null;
            }
            catch
            {
                branchWin?.Dispose();
                frame?.Dispose();
                throw;
            }

            return;
        }

        if (!_needsConversion
            && frame.Format.PixelFormat == PixelFormat.Nv12
            && frame.DmabufNv12 is null
            && frame.DmabufP010 is null
            && frame.DmabufP016 is null
            && frame.Win32Nv12 is null
            && VideoFrame.TryCreateNv12CpuFanOutViews(frame, 2, frame.ColorTransferHint, out var fanViews))
        {
            try
            {
                frame.Dispose();
                frame = null!;
                _primary.Submit(fanViews[0]);
                _branch.Submit(fanViews[1]);
            }
            catch
            {
                fanViews[0].Dispose();
                fanViews[1].Dispose();
                throw;
            }

            return;
        }

        VideoFrame? branchFrame = null;
        try
        {
            branchFrame = _needsConversion && _converter is not null
                ? _converter.Convert(frame, frame.ColorTransferHint)
                : VideoCpuFrameConverter.DuplicateCpuBacking(frame, frame.ColorTransferHint);

            _primary.Submit(frame);
            frame = null!;
            _branch.Submit(branchFrame);
            branchFrame = null;
        }
        catch
        {
            branchFrame?.Dispose();
            if (frame is not null) frame.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _converter?.Dispose();
        if (_disposeBranch && _branch is IDisposable d)
            d.Dispose();
        if (_primary is IDisposable p)
            p.Dispose();
    }
}
