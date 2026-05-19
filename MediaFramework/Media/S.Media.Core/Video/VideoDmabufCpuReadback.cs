using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Best-effort Linux <strong>mmap</strong> readback from DRM PRIME / dma-buf file descriptors into CPU-backed
/// <see cref="VideoFrame"/> planes for <see cref="PixelFormat.Nv12"/>, <see cref="PixelFormat.P010"/>, and
/// <see cref="PixelFormat.P016"/> semi-planar layouts. Intended for FFmpeg <c>VideoRouter</c> branch <c>swscale</c> conversion when hardware frames cannot be shared with every sink.
/// </summary>
/// <remarks>
/// <para>
/// This path only works when the driver exposes dma-buf memory that is <c>mmap</c>-readable (often true for
/// linear / generic modifiers; tiled or protected buffers may return <see langword="false"/>). It is a
/// blocking full-frame copy — use sparingly (e.g. one mixed fan-out branch).
/// </para>
/// <para>
/// <strong>Win32</strong> shared NV12 textures are not supported here — callers should keep using all-NV12 sinks,
/// a single output, or software decode until a D3D11 staging readback exists.
/// </para>
/// </remarks>
public static unsafe class VideoDmabufCpuReadback
{
    private const int ProtRead = 1;
    private const int MapShared = 0x01;
    private const int SeekEnd = 2;

    /// <summary>Attempts to build a CPU <see cref="VideoFrame"/> copy of an NV12 dma-buf frame.</summary>
    public static bool TryCreateNv12CpuCopy(VideoFrame source, [NotNullWhen(true)] out VideoFrame? cpuFrame)
    {
        cpuFrame = null;
        if (!OperatingSystem.IsLinux()) return false;
        var b = source.DmabufNv12;
        if (b is null || source.Format.PixelFormat != PixelFormat.Nv12) return false;

        var h = source.Format.Height;
        if (h <= 0 || (h & 1) != 0) return false;

        var yRows = h;
        var uvRows = h / 2;
        var yPitch = b.YPlanePitchBytes;
        var uvPitch = b.UvPlanePitchBytes;
        var yBuf = new byte[(long)yPitch * yRows];
        var uvBuf = new byte[(long)uvPitch * uvRows];
        if (!TryCopyPlane(b.YPlaneFd, b.YPlaneOffsetBytes, yPitch, yRows, yBuf, yPitch))
            return false;
        if (!TryCopyPlane(b.UvPlaneFd, b.UvPlaneOffsetBytes, uvPitch, uvRows, uvBuf, uvPitch))
            return false;

        cpuFrame = new VideoFrame(
            source.PresentationTime,
            source.Format,
            [yBuf, uvBuf],
            [yPitch, uvPitch],
            metadata: source.Metadata);
        return true;
    }

    /// <summary>Attempts to build a CPU <see cref="VideoFrame"/> copy of a P010 dma-buf frame.</summary>
    public static bool TryCreateP010CpuCopy(VideoFrame source, [NotNullWhen(true)] out VideoFrame? cpuFrame)
    {
        cpuFrame = null;
        if (!OperatingSystem.IsLinux()) return false;
        var b = source.DmabufP010;
        if (b is null || source.Format.PixelFormat != PixelFormat.P010) return false;

        var h = source.Format.Height;
        if (h <= 0 || (h & 1) != 0) return false;

        var yRows = h;
        var uvRows = h / 2;
        var yPitch = b.YPlanePitchBytes;
        var uvPitch = b.UvPlanePitchBytes;
        var yBuf = new byte[(long)yPitch * yRows];
        var uvBuf = new byte[(long)uvPitch * uvRows];
        if (!TryCopyPlane(b.YPlaneFd, b.YPlaneOffsetBytes, yPitch, yRows, yBuf, yPitch))
            return false;
        if (!TryCopyPlane(b.UvPlaneFd, b.UvPlaneOffsetBytes, uvPitch, uvRows, uvBuf, uvPitch))
            return false;

        cpuFrame = new VideoFrame(
            source.PresentationTime,
            source.Format,
            [yBuf, uvBuf],
            [yPitch, uvPitch],
            metadata: source.Metadata);
        return true;
    }

    /// <summary>Attempts to build a CPU <see cref="VideoFrame"/> copy of a P016 dma-buf frame.</summary>
    public static bool TryCreateP016CpuCopy(VideoFrame source, [NotNullWhen(true)] out VideoFrame? cpuFrame)
    {
        cpuFrame = null;
        if (!OperatingSystem.IsLinux()) return false;
        var b = source.DmabufP016;
        if (b is null || source.Format.PixelFormat != PixelFormat.P016) return false;

        var h = source.Format.Height;
        if (h <= 0 || (h & 1) != 0) return false;

        var yRows = h;
        var uvRows = h / 2;
        var yPitch = b.YPlanePitchBytes;
        var uvPitch = b.UvPlanePitchBytes;
        var yBuf = new byte[(long)yPitch * yRows];
        var uvBuf = new byte[(long)uvPitch * uvRows];
        if (!TryCopyPlane(b.YPlaneFd, b.YPlaneOffsetBytes, yPitch, yRows, yBuf, yPitch))
            return false;
        if (!TryCopyPlane(b.UvPlaneFd, b.UvPlaneOffsetBytes, uvPitch, uvRows, uvBuf, uvPitch))
            return false;

        cpuFrame = new VideoFrame(
            source.PresentationTime,
            source.Format,
            [yBuf, uvBuf],
            [yPitch, uvPitch],
            metadata: source.Metadata);
        return true;
    }

    private static bool TryCopyPlane(int fd, nint offsetBytes, int rowPitchBytes, int rowCount, byte[] dst, int dstRowStride)
    {
        if (fd < 0 || rowPitchBytes <= 0 || rowCount <= 0) return false;
        var needEnd = (ulong)offsetBytes + (ulong)rowPitchBytes * (ulong)rowCount;
        if (needEnd == 0) return false;

        var fileSize = GetDmabufFileSize(fd);
        if (fileSize < needEnd) return false;

        var mapped = Mmap(IntPtr.Zero, (UIntPtr)fileSize, ProtRead, MapShared, fd, IntPtr.Zero);
        if (mapped == MapFailed) return false;
        try
        {
            fixed (byte* d = dst)
            {
                var srcBase = (byte*)mapped + (nint)offsetBytes;
                for (var r = 0; r < rowCount; r++)
                {
                    var srcRow = srcBase + (nint)r * rowPitchBytes;
                    var dstRow = d + (nint)r * dstRowStride;
                    Buffer.MemoryCopy(srcRow, dstRow, dstRowStride, rowPitchBytes);
                }
            }

            return true;
        }
        finally
        {
            _ = Munmap(mapped, (UIntPtr)fileSize);
        }
    }

    private static ulong GetDmabufFileSize(int fd)
    {
        var sz = LSeek(fd, 0, SeekEnd);
        if (sz < 0) return 0;
        return (ulong)sz;
    }

    private static readonly IntPtr MapFailed = new(-1);

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern IntPtr Mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int Munmap(IntPtr addr, UIntPtr length);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long LSeek(int fd, long offset, int whence);
}
