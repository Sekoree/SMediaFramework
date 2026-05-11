using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Owns POSIX file descriptors plus layout metadata for decoded NV12 in Linux
/// DRM PRIME / dma-buf form. Callers import via EGL; do not <see cref="MemoryExtensions.Pin"/>
/// the CPU <see cref="VideoFrame.Planes"/> on the matching frame — they are stubs.
/// </summary>
public sealed class VideoDmabufNv12Backing : IDisposable
{
    private int _yFd;
    private int _uvFd;
    private int _disposed;

    public VideoDmabufNv12Backing(
        int yPlaneDupFd,
        nint yPlaneOffsetBytes,
        int yPlanePitchBytes,
        int uvPlaneDupFd,
        nint uvPlaneOffsetBytes,
        int uvPlanePitchBytes,
        ulong drmFormatModifier)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("DMA-BUF video backing is Linux-only.");
        if (yPlaneDupFd < 0) throw new ArgumentOutOfRangeException(nameof(yPlaneDupFd));
        if (uvPlaneDupFd < 0) throw new ArgumentOutOfRangeException(nameof(uvPlaneDupFd));
        if (yPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(yPlanePitchBytes));
        if (uvPlanePitchBytes <= 0) throw new ArgumentOutOfRangeException(nameof(uvPlanePitchBytes));

        _yFd = yPlaneDupFd;
        _uvFd = uvPlaneDupFd;
        YPlaneOffsetBytes = yPlaneOffsetBytes;
        YPlanePitchBytes = yPlanePitchBytes;
        UvPlaneOffsetBytes = uvPlaneOffsetBytes;
        UvPlanePitchBytes = uvPlanePitchBytes;
        DrmFormatModifier = drmFormatModifier;
    }

    public nint YPlaneOffsetBytes { get; }
    public int YPlanePitchBytes { get; }
    public nint UvPlaneOffsetBytes { get; }
    public int UvPlanePitchBytes { get; }

    /// <summary>Per-object DRM_FORMAT_MOD_* (linear / tiled).</summary>
    public ulong DrmFormatModifier { get; }

    public int YPlaneFd => _yFd;
    public int UvPlaneFd => _uvFd;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_yFd >= 0)
        {
            _ = libc_close(_yFd);
            _yFd = -1;
        }

        if (_uvFd >= 0)
        {
            _ = libc_close(_uvFd);
            _uvFd = -1;
        }
    }

    [global::System.Runtime.InteropServices.DllImport("libc",
        EntryPoint = "close")]
    private static extern int libc_close(int fd);
}
