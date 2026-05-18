using System.Runtime.InteropServices;
using System.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Owns NT shared handles plus layout metadata for decoded NV12 exported from a
/// D3D11/DXGI shared texture (single resource; luma/chroma handles may alias).
/// Optionally carries non-owning libav <c>ID3D11Device</c> / <c>ID3D11Texture2D</c> COM pointers so GL can
/// import the same texture on the decode device without <c>OpenSharedResource</c> while the frame is valid.
/// </summary>
/// <remarks>
/// For D3D11 surfaces that use <c>IDXGIKeyedMutex</c> (e.g. <c>SharedKeyedMutex</c> textures), the decode path should
/// release keyed mutex key <c>0</c> for the NV12 texture before the <see cref="VideoFrame"/> is handed to sinks;
/// the Windows GL upload path in <c>S.Media.OpenGL</c> acquires that key for the duration of D3D11 copy / WGL interop.
/// Handle-only instances (non-zero NT handles, zero COM pointers) align the shipped DXGI export path with a
/// zero-libav-COM <see cref="HardwareVideoSurfaceDescriptor"/>; a separate consumer <c>ID3D11Device</c> for
/// <c>OpenSharedResource</c> remains GL-host-owned until product backlog **PO-01** closes the full descriptor story
/// (<c>Doc/Todo.md</c> §Tier F row 34 <c>Open</c> tail).
/// </remarks>
public sealed class VideoWin32Nv12Backing : IDisposable
{
    private nint _lumaNtHandle;
    private nint _chromaNtHandle;
    private int _refCount = 1;

    /// <param name="d3d11TextureArraySliceIndex">Array slice for D3D11VA pool textures; 0 for non-array.</param>
    /// <param name="libavD3D11DeviceComPtr">
    /// Optional non-owning <c>ID3D11Device</c> COM pointer for the decode device that owns the texture below.
    /// When non-zero together with <paramref name="libavD3D11Texture2DComPtr"/>, GL upload may use the texture directly
    /// on the same device without <c>OpenSharedResource</c> (valid only while libav holds the frame).
    /// </param>
    /// <param name="libavD3D11Texture2DComPtr">Optional non-owning <c>ID3D11Texture2D</c> COM pointer (same lifetime as the decoded frame).</param>
    public VideoWin32Nv12Backing(
        nint sharedLumaNtHandle,
        nint sharedChromaNtHandle,
        int yPlanePitchBytes,
        int uvPlanePitchBytes,
        int d3d11TextureArraySliceIndex,
        nint libavD3D11DeviceComPtr = 0,
        nint libavD3D11Texture2DComPtr = 0)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 NV12 shared-handle backing is Windows-only.");
        if (sharedLumaNtHandle == 0 && libavD3D11Texture2DComPtr == 0)
            throw new ArgumentOutOfRangeException(nameof(sharedLumaNtHandle),
                "NT luma handle is required unless libav D3D11 texture COM pointers are supplied for same-device import.");
        if (yPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(yPlanePitchBytes));
        if (uvPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(uvPlanePitchBytes));
        if (d3d11TextureArraySliceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(d3d11TextureArraySliceIndex));
        if (libavD3D11Texture2DComPtr != 0 && libavD3D11DeviceComPtr == 0)
            throw new ArgumentOutOfRangeException(nameof(libavD3D11DeviceComPtr), "Device COM pointer is required when a libav texture COM pointer is set.");

        _lumaNtHandle = sharedLumaNtHandle;
        _chromaNtHandle = sharedChromaNtHandle == 0 ? sharedLumaNtHandle : sharedChromaNtHandle;
        YPlanePitchBytes = yPlanePitchBytes;
        UvPlanePitchBytes = uvPlanePitchBytes;
        D3D11TextureArraySliceIndex = d3d11TextureArraySliceIndex;
        LibavD3D11DeviceComPtr = libavD3D11DeviceComPtr;
        LibavD3D11Texture2DComPtr = libavD3D11Texture2DComPtr;
    }

    public int YPlanePitchBytes { get; }
    public int UvPlanePitchBytes { get; }
    public int D3D11TextureArraySliceIndex { get; }

    /// <summary>Non-owning libav decode <c>ID3D11Device</c> COM pointer when <see cref="LibavD3D11Texture2DComPtr"/> is set; otherwise <c>0</c>.</summary>
    public nint LibavD3D11DeviceComPtr { get; }

    /// <summary>Non-owning libav <c>ID3D11Texture2D</c> COM pointer for same-device GL upload; <c>0</c> when only NT handles are used.</summary>
    public nint LibavD3D11Texture2DComPtr { get; }

    public nint LumaSharedNtHandle => _lumaNtHandle;
    public nint ChromaSharedNtHandle => _chromaNtHandle;

    public bool UsesDistinctSharedObjects => _lumaNtHandle != _chromaNtHandle;

    /// <summary>Atomic against a racing <see cref="Dispose"/> that would otherwise close the handles between a disposed-check and the increment.</summary>
    /// <exception cref="ObjectDisposedException">Backing handles are already closed.</exception>
    public void AddReference()
    {
        while (true)
        {
            var n = Volatile.Read(ref _refCount);
            if (n <= 0)
                throw new ObjectDisposedException(nameof(VideoWin32Nv12Backing));
            if (Interlocked.CompareExchange(ref _refCount, n + 1, n) == n)
                return;
        }
    }

    public void Dispose()
    {
        while (true)
        {
            var n = Volatile.Read(ref _refCount);
            if (n <= 0) return;
            if (Interlocked.CompareExchange(ref _refCount, n - 1, n) == n)
            {
                if (n - 1 > 0) return;
                break;
            }
        }

        var l = _lumaNtHandle;
        var c = _chromaNtHandle;
        _lumaNtHandle = 0;
        _chromaNtHandle = 0;
        if (l != 0)
            _ = CloseHandle(l);
        if (c != 0 && c != l)
            _ = CloseHandle(c);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
