namespace S.Media.Core.Video;

public sealed partial class VideoFrame
{
    private static void ValidateHardwareBacking(
        VideoFrameHardwareBacking? backing,
        VideoFormat format,
        ReadOnlyMemory<byte>[] planes,
        int[] strides)
    {
        switch (backing)
        {
            case null:
                if (planes.Length == 0)
                    throw new ArgumentException("at least one plane required", nameof(planes));
                break;

            case DmabufNv12Backing nv12:
                ValidateDmabufStubPlanes(planes, strides, nameof(planes));
                if (format.PixelFormat != PixelFormat.Nv12)
                    throw new ArgumentException("DMA-BUF frames require PixelFormat.Nv12", nameof(format));
                if (strides[0] != nv12.YPlanePitchBytes || strides[1] != nv12.UvPlanePitchBytes)
                    throw new ArgumentException(
                        "strides must mirror DmabufNv12Backing pitches for NV12 dma-buf frames.", nameof(strides));
                break;

            case DmabufP010Backing p010:
                ValidateDmabufStubPlanes(planes, strides, nameof(planes));
                if (format.PixelFormat != PixelFormat.P010)
                    throw new ArgumentException("P010 DMA-BUF frames require PixelFormat.P010", nameof(format));
                if (strides[0] != p010.YPlanePitchBytes || strides[1] != p010.UvPlanePitchBytes)
                    throw new ArgumentException(
                        "strides must mirror DmabufP010Backing pitches for P010 dma-buf frames.", nameof(strides));
                break;

            case DmabufP016Backing p016:
                ValidateDmabufStubPlanes(planes, strides, nameof(planes));
                if (format.PixelFormat != PixelFormat.P016)
                    throw new ArgumentException("P016 DMA-BUF frames require PixelFormat.P016", nameof(format));
                if (strides[0] != p016.YPlanePitchBytes || strides[1] != p016.UvPlanePitchBytes)
                    throw new ArgumentException(
                        "strides must mirror DmabufP016Backing pitches for P016 dma-buf frames.", nameof(strides));
                break;

            case Win32SharedNv12Backing win32:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Win32 NV12 frames are Windows-only.");
                ValidateDmabufStubPlanes(planes, strides, nameof(planes));
                if (format.PixelFormat != PixelFormat.Nv12)
                    throw new ArgumentException("Win32 shared-handle frames require PixelFormat.Nv12", nameof(format));
                if (strides[0] != win32.YPlanePitchBytes || strides[1] != win32.UvPlanePitchBytes)
                    throw new ArgumentException(
                        "strides must mirror Win32SharedNv12Backing pitches for NV12 Win32 frames.", nameof(strides));
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported {nameof(VideoFrameHardwareBacking)} type '{backing.GetType().Name}'.",
                    nameof(backing));
        }

        if (planes.Length != strides.Length)
            throw new ArgumentException("planes and strides must have the same length", nameof(strides));
        for (var i = 0; i < strides.Length; i++)
        {
            if (strides[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(strides), strides[i], $"stride[{i}] must be positive");
        }
    }

    private static void ValidateDmabufStubPlanes(ReadOnlyMemory<byte>[] planes, int[] strides, string paramName)
    {
        if (planes.Length != 2 || strides.Length != 2)
            throw new ArgumentException("Hardware frames require two planes and strides (often empty stubs).", paramName);
        foreach (var p in planes)
        {
            if (!p.IsEmpty)
                throw new ArgumentException(
                    "CPU plane memory must be empty for hardware-backed frames; use stub ReadOnlyMemory<byte>.Empty entries.",
                    paramName);
        }
    }
}
