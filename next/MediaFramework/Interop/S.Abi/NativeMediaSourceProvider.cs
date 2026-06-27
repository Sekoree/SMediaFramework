using System.Text;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpMediaSourceProviderVTable</c> — opens a URI into the plugin's video source and
/// wraps it as a managed <see cref="IVideoSource"/> via <see cref="NativeVideoSource"/>. (Audio sources are not
/// modelled yet; <see cref="TryOpenVideo"/> returns the video half of the opened media.)
/// </summary>
public sealed unsafe class NativeMediaSourceProvider
{
    private readonly MfpMediaSourceProviderVTable* _vt;
    private readonly void* _self;

    internal NativeMediaSourceProvider(nint vtable, nint self)
    {
        _vt = (MfpMediaSourceProviderVTable*)vtable;
        _self = (void*)self;
    }

    public bool CanOpen(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (_vt->CanOpen == null)
            return false;
        var bytes = Utf8(uri);
        fixed (byte* p = bytes)
            return _vt->CanOpen(_self, p) != 0;
    }

    public IVideoSource? TryOpenVideo(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (_vt->Open == null || _vt->VideoSourceVTable == null)
            return null;

        var bytes = Utf8(uri);
        MfpMediaSource media = default;
        int rc;
        fixed (byte* p = bytes)
            rc = _vt->Open(_self, p, &media);

        if (rc != (int)MfpStatus.Ok || media.Video == null)
            return null;

        return NativeVideoSource.Create((nint)_vt->VideoSourceVTable, (nint)media.Video);
    }

    private static byte[] Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1]; // trailing 0 => null-terminated
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }
}
