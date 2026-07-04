using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpVideoSourceVTable</c> to the managed <see cref="IVideoSource"/>. The resolved
/// format + native pixel formats are queried up front (get_format / native_pixel_formats); each read pulls one
/// <c>MfpVideoFrame</c>, copies its CPU planes into a managed <see cref="VideoFrame"/> via <see cref="AbiFrameMarshal"/>,
/// then returns the native frame to the plugin via release_frame. CPU, Linux dma-buf, and Windows D3D11 shared
/// frames are imported; hardware handles are duplicated before the native frame is released.
/// </summary>
internal sealed unsafe class NativeVideoSource : IVideoSource, IDisposable
{
    private readonly MfpVideoSourceVTable* _vt;
    private readonly void* _src;
    private readonly VideoFormat _format;
    private readonly PixelFormat[] _native;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    private NativeVideoSource(
        MfpVideoSourceVTable* vt, void* src, VideoFormat format, PixelFormat[] native, AbiPluginLease lease)
    {
        _vt = vt;
        _src = src;
        _format = format;
        _native = native;
        _lease = lease;
    }

    public static NativeVideoSource Create(nint vtable, nint src, AbiPluginLease lease)
    {
        var vt = (MfpVideoSourceVTable*)vtable;
        var s = (void*)src;

        var native = QueryNativeFormats(vt, s);

        MfpVideoFormat mf = default;
        AbiPluginHost.ClearLastError();
        var status = vt->GetFormat(s, &mf);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin video source format query", status);
        if (mf.Width == 0 || mf.Height == 0 || AbiFrameMarshal.ToCore(mf.PixelFormat) == PixelFormat.Unknown)
            throw new InvalidOperationException(
                $"plugin video source returned invalid format {mf.Width}x{mf.Height}, pixel format {mf.PixelFormat}.");
        var rate = mf.FpsNum > 0 && mf.FpsDen > 0
            ? new Rational((int)mf.FpsNum, (int)mf.FpsDen)
            : new Rational(30, 1);
        var format = new VideoFormat((int)mf.Width, (int)mf.Height, AbiFrameMarshal.ToCore(mf.PixelFormat), rate);

        return new NativeVideoSource(vt, s, format, native, lease);
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed || (_vt->IsExhausted != null && _vt->IsExhausted(_src) != 0);

    public void SelectOutputFormat(PixelFormat format)
    {
        if (_vt->SelectOutputFormat == null)
            return;
        AbiPluginHost.ClearLastError();
        var rc = _vt->SelectOutputFormat(_src, AbiFrameMarshal.FromCore(format));
        if (rc != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException($"plugin video source output format {format}", rc);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        frame = null!;
        if (_disposed || _vt->TryReadFrame == null)
            return false;

        MfpVideoFrame mf = default;
        AbiPluginHost.ClearLastError();
        var status = _vt->TryReadFrame(_src, &mf);
        if (status is (int)MfpStatus.ErrAgain or (int)MfpStatus.ErrEnd)
            return false;
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin video source read", status);

        try
        {
            if ((_vt->SupportedFrameKinds & (1u << (int)mf.Kind)) == 0)
                throw new InvalidOperationException(
                    $"plugin video source returned unadvertised ABI frame kind {mf.Kind}.");
            if ((uint)mf.Sync.Kind >= 32 || (_vt->SupportedSyncKinds & (1u << mf.Sync.Kind)) == 0)
                throw new InvalidOperationException(
                    $"plugin video source returned unadvertised ABI sync kind {mf.Sync.Kind}.");
            frame = AbiFrameMarshal.ToManagedFrame(in mf, _format.FrameRate);
            return true;
        }
        finally
        {
            if (_vt->ReleaseFrame != null)
                _vt->ReleaseFrame(_src, &mf);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_src);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeVideoSource() => Dispose();

    private static PixelFormat[] QueryNativeFormats(MfpVideoSourceVTable* vt, void* s)
    {
        if (vt->NativePixelFormats == null)
            return [];
        const int cap = 32;
        var buf = stackalloc int[cap];
        var count = 0;
        AbiPluginHost.ClearLastError();
        var status = vt->NativePixelFormats(s, buf, cap, &count);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin video source pixel-format query", status);
        if (count <= 0)
            return [];
        count = Math.Min(count, cap);
        var result = new PixelFormat[count];
        for (var i = 0; i < count; i++)
            result[i] = AbiFrameMarshal.ToCore(buf[i]);
        return result;
    }
}
