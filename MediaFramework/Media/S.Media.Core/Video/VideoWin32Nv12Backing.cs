using System.Runtime.InteropServices;
using System.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Owns NT shared handles plus layout metadata for decoded NV12 exported from a
/// D3D11/DXGI shared texture (single resource; luma/chroma handles may alias).
/// </summary>
public sealed class VideoWin32Nv12Backing : IDisposable
{
    private nint _lumaNtHandle;
    private nint _chromaNtHandle;
    private int _closed;
    private int _refCount = 1;

    /// <param name="d3d11TextureArraySliceIndex">Array slice for D3D11VA pool textures; 0 for non-array.</param>
    public VideoWin32Nv12Backing(
        nint sharedLumaNtHandle,
        nint sharedChromaNtHandle,
        int yPlanePitchBytes,
        int uvPlanePitchBytes,
        int d3d11TextureArraySliceIndex)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 NV12 shared-handle backing is Windows-only.");
        if (sharedLumaNtHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(sharedLumaNtHandle));
        if (yPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(yPlanePitchBytes));
        if (uvPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(uvPlanePitchBytes));
        if (d3d11TextureArraySliceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(d3d11TextureArraySliceIndex));

        _lumaNtHandle = sharedLumaNtHandle;
        _chromaNtHandle = sharedChromaNtHandle == 0 ? sharedLumaNtHandle : sharedChromaNtHandle;
        YPlanePitchBytes = yPlanePitchBytes;
        UvPlanePitchBytes = uvPlanePitchBytes;
        D3D11TextureArraySliceIndex = d3d11TextureArraySliceIndex;
    }

    public int YPlanePitchBytes { get; }
    public int UvPlanePitchBytes { get; }
    public int D3D11TextureArraySliceIndex { get; }

    public nint LumaSharedNtHandle => _lumaNtHandle;
    public nint ChromaSharedNtHandle => _chromaNtHandle;

    public bool UsesDistinctSharedObjects => _lumaNtHandle != _chromaNtHandle;

    public void AddReference()
    {
        if (Volatile.Read(ref _closed) != 0)
            throw new ObjectDisposedException(nameof(VideoWin32Nv12Backing));
        Interlocked.Increment(ref _refCount);
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining > 0)
            return;
        if (remaining < 0)
            return;

        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

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
