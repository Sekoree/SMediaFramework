using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace S.Media.Gpu.Internal;

/// <summary>
/// <para>
/// DXGI <see cref="IDXGIKeyedMutex"/> hand-off for D3D11 surfaces created with
/// <see cref="ResourceOptionFlags.SharedKeyedMutex"/> (common for D3D11 video decode pools and DXGI shared textures).
/// FFmpeg <c>d3d11va</c> typically completes GPU work on a frame, then releases keyed mutex <b>key 0</b> so consumers can
/// <see cref="IDXGIKeyedMutex.AcquireSync"/> that key before reading the texture from another device or API (this path,
/// staging <c>Map</c>, or <c>WGL_NV_DX_interop</c>).
/// </para>
/// <para>
/// <b>Contract (consumer side, this library):</b> call <see cref="TryAcquireForGpuRead"/> before any D3D11 or GL access
/// to the texture. If the texture exposes a keyed mutex and acquire fails, the upload must abort - reading without the
/// mutex is undefined when the mutex exists. If the texture has no keyed mutex interface, acquire succeeds with a
/// <see langword="null"/> scope and the caller may read immediately (subject to normal D3D11 ordering with the producer).
/// </para>
/// <para>
/// <b>Contract (producer side, libav / host):</b> do not use the same subresource concurrently with this consumer window.
/// A decoded <see cref="S.Media.Core.Video.VideoFrame"/> must only be presented after the decoder has released mutex key
/// <see cref="DecoderToConsumerReleaseKey"/> for that texture (libav’s D3D11VA path does this before returning the frame
/// to the caller in normal operation).
/// </para>
/// </summary>
internal sealed class D3d11TextureKeyedMutexScope : IDisposable
{
    /// <summary>Keyed mutex value used by FFmpeg D3D11VA and DXGI shared surfaces for decode-to-consumer hand-off.</summary>
    internal const ulong DecoderToConsumerReleaseKey = 0;

    private IDXGIKeyedMutex? _mutex;
    private readonly ulong _releaseKey;

    private D3d11TextureKeyedMutexScope(IDXGIKeyedMutex mutex, ulong releaseKey)
    {
        _mutex = mutex;
        _releaseKey = releaseKey;
    }

    /// <summary>
    /// When the texture has no <see cref="IDXGIKeyedMutex"/>, returns <see langword="true"/> and sets
    /// <paramref name="scope"/> to <see langword="null"/> (caller may read the texture under normal D3D11 rules).
    /// When a mutex exists but <see cref="IDXGIKeyedMutex.AcquireSync"/> fails, returns <see langword="false"/>; the caller
    /// must not read the texture.
    /// When acquire succeeds, returns <see langword="true"/> with a non-null <paramref name="scope"/>; dispose it after
    /// GPU work completes to <see cref="IDXGIKeyedMutex.ReleaseSync"/>.
    /// </summary>
    public static bool TryAcquireForGpuRead(
        ID3D11Texture2D texture,
        out D3d11TextureKeyedMutexScope? scope,
        int timeoutMilliseconds = 2000)
    {
        scope = null;
        var km = texture.QueryInterfaceOrNull<IDXGIKeyedMutex>();
        if (km is null)
            return true;

        try
        {
            km.AcquireSync(DecoderToConsumerReleaseKey, timeoutMilliseconds);
        }
        catch
        {
            km.Dispose();
            return false;
        }

        scope = new D3d11TextureKeyedMutexScope(km, DecoderToConsumerReleaseKey);
        return true;
    }

    public void Dispose()
    {
        var km = Interlocked.Exchange(ref _mutex, null);
        if (km is null)
            return;

        try
        {
            km.ReleaseSync(_releaseKey);
        }
        catch
        {
            /* best effort */
        }

        try
        {
            km.Dispose();
        }
        catch
        {
            /* best effort */
        }
    }
}
