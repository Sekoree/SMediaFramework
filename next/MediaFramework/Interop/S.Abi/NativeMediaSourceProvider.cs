using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpMediaSourceProviderVTable</c> to the framework's <see cref="IMediaDecoderProvider"/>
/// — so a plugin that handles a URI scheme is registered into a live <see cref="IMediaRegistry"/> and used exactly
/// like the built-in FFmpeg/NDI providers. Opens the URI into the plugin's video source and wraps it as a managed
/// <see cref="IVideoSource"/> via <see cref="NativeVideoSource"/>. (Audio-source adapting is not modelled yet — the
/// provider only claims video in <see cref="Probe"/>.)
/// </summary>
public sealed unsafe class NativeMediaSourceProvider : IMediaDecoderProvider
{
    private readonly MfpMediaSourceProviderVTable* _vt;
    private readonly void* _self;

    internal NativeMediaSourceProvider(string name, nint vtable, nint self)
    {
        Name = name;
        _vt = (MfpMediaSourceProviderVTable*)vtable;
        _self = (void*)self;
    }

    public string Name { get; }

    public double Probe(string uri, MediaKind kind)
    {
        // Only claim video for now (audio adapting unmodelled), and only when the plugin says it handles the URI.
        if (kind != MediaKind.Video || _vt->VideoSourceVTable == null)
            return 0.0;
        return CanOpen(uri) ? 1.0 : 0.0;
    }

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        TryOpenVideo(uri) ?? throw new InvalidOperationException($"plugin '{Name}' could not open video for '{uri}'.");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException($"plugin '{Name}': native audio-source adapting is not implemented yet.");

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
