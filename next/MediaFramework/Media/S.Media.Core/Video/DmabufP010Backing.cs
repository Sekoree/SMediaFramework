using System.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Owns POSIX file descriptors plus layout metadata for decoded P010 in Linux
/// DRM PRIME / dma-buf form. Callers import via EGL; do not pin CPU <see cref="VideoFrame.Planes"/> — they are stubs.
/// </summary>
public sealed class DmabufP010Backing : VideoFrameHardwareBacking
{
    private int _yFd;
    private int _uvFd;
    private int _refCount = 1;

    public DmabufP010Backing(
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

    /// <summary>Atomic against a racing <see cref="Dispose"/> that would otherwise close the fds between a disposed-check and the increment.</summary>
    /// <exception cref="ObjectDisposedException">Backing file descriptors are already closed.</exception>
    public void AddReference()
    {
        while (true)
        {
            var n = Volatile.Read(ref _refCount);
            if (n <= 0)
                throw new ObjectDisposedException(nameof(DmabufP010Backing));
            if (Interlocked.CompareExchange(ref _refCount, n + 1, n) == n)
                return;
        }
    }

    public override void Dispose()
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

    public VideoFrame CreateFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(VideoFrame.CreateP010DmabufOnlyLinux);
        return VideoFrameHardwareBackingFactories.CreateHardwareFrame(
            this, presentationTime, format, YPlanePitchBytes, UvPlanePitchBytes, metadata, additionalRelease);
    }

    public VideoFrame CreateSharedReference(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoFrameMetadata metadata = default)
    {
        AddReference();
        return CreateFrame(presentationTime, format, metadata);
    }

    [global::System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close")]
    private static extern int libc_close(int fd);
}
