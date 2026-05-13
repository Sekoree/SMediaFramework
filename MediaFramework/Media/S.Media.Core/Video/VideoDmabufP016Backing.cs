using System.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Owns POSIX file descriptors plus layout metadata for decoded P016 in Linux
/// DRM PRIME / dma-buf form. Callers import via EGL; do not pin CPU <see cref="VideoFrame.Planes"/> — they are stubs.
/// </summary>
public sealed class VideoDmabufP016Backing : IDisposable
{
    private int _yFd;
    private int _uvFd;
    private int _closed;
    private int _refCount = 1;

    public VideoDmabufP016Backing(
        int yPlaneDupFd,
        nint yPlaneOffsetBytes,
        int yPlanePitchBytes,
        int uvPlaneDupFd,
        nint uvPlaneOffsetBytes,
        int uvPlanePitchBytes,
        ulong yPlaneDrmFormatModifier,
        ulong uvPlaneDrmFormatModifier)
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
        YPlaneDrmFormatModifier = yPlaneDrmFormatModifier;
        UvPlaneDrmFormatModifier = uvPlaneDrmFormatModifier;
    }

    public nint YPlaneOffsetBytes { get; }
    public int YPlanePitchBytes { get; }
    public nint UvPlaneOffsetBytes { get; }
    public int UvPlanePitchBytes { get; }

    public ulong YPlaneDrmFormatModifier { get; }
    public ulong UvPlaneDrmFormatModifier { get; }

    public bool UsesDistinctDmaBufObjects => YPlaneFd != UvPlaneFd;

    public int YPlaneFd => _yFd;
    public int UvPlaneFd => _uvFd;

    public void AddReference()
    {
        if (Volatile.Read(ref _closed) != 0)
            throw new ObjectDisposedException(nameof(VideoDmabufP016Backing));
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

    [global::System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close")]
    private static extern int libc_close(int fd);
}
