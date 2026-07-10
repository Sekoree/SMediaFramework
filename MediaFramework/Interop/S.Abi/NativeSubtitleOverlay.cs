using System.Text;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpSubtitleProviderVTable</c> - opens a sidecar/stream URI at a canvas size into a
/// plugin subtitle instance, wrapped as <see cref="NativeSubtitleOverlay"/> (an <see cref="IVideoOverlaySource"/>).
/// Subtitles are consumed via a factory delegate (e.g. <c>ShowSession</c>'s overlay factory), not a registry, so
/// this exposes <see cref="TryOpen"/> rather than registering into one.
/// </summary>
public sealed unsafe class NativeSubtitleProvider : IDisposable
{
    private readonly MfpSubtitleProviderVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeSubtitleProvider(nint vtable, nint self, AbiPluginLease lease)
    {
        _vt = (MfpSubtitleProviderVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
    }

    public bool CanOpen(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->CanOpen == null)
            return false;
        var bytes = Utf8(uri);
        fixed (byte* p = bytes)
            return _vt->CanOpen(_self, p) != 0;
    }

    public IVideoOverlaySource? TryOpen(string uri, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Open == null || _vt->SubtitleVTable == null)
            return null;

        var bytes = Utf8(uri);
        void* instance;
        fixed (byte* p = bytes)
            instance = _vt->Open(_self, p, (uint)canvasWidth, (uint)canvasHeight);

        return instance == null
            ? null
            : new NativeSubtitleOverlay(_vt->SubtitleVTable, instance, _lease.AcquireDependent());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
    }

    private static byte[] Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }
}

/// <summary>A native plugin subtitle/overlay instance: <see cref="RenderAt"/> forwards to the plugin's render_at,
/// copying the returned CPU frame into a managed <see cref="VideoFrame"/> (then releasing the native one).</summary>
internal sealed unsafe class NativeSubtitleOverlay : IVideoOverlaySource
{
    private readonly MfpSubtitleVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeSubtitleOverlay(MfpSubtitleVTable* vt, void* self, AbiPluginLease lease)
    {
        _vt = vt;
        _self = self;
        _lease = lease;
    }

    public VideoFrame? RenderAt(TimeSpan position)
    {
        if (_disposed || _vt->RenderAt == null)
            return null;

        MfpVideoFrame mf = default;
        AbiPluginHost.ClearLastError();
        var status = _vt->RenderAt(_self, position.Ticks, &mf);
        if (status == (int)MfpStatus.ErrAgain)
            return null;
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin subtitle render", status);

        try
        {
            return mf.Kind == MfpFrameKind.Cpu ? AbiFrameMarshal.ToManagedCpuFrame(in mf, new Rational(30, 1)) : null;
        }
        finally
        {
            if (_vt->ReleaseFrame != null)
                _vt->ReleaseFrame(_self, &mf);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_self);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeSubtitleOverlay() => Dispose();
}
