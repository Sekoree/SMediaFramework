namespace S.Media.Core.Video;

public sealed partial class VideoFrame
{
    /// <summary>
    /// Validates that this CPU-backed frame has the planes, strides, and byte lengths required by
    /// its <see cref="Format"/>. Call at native/encoder/GPU-upload boundaries before indexing planes.
    /// </summary>
    public void ValidateCpuGeometry()
    {
        if (_hardwareBacking is not null)
            throw new NotSupportedException("Hardware-backed frames do not expose validated CPU plane geometry.");

        Format.Validate(nameof(Format));
        var requiredPlanes = PixelFormatInfo.PlaneCount(Format.PixelFormat);
        if (_planes.Length < requiredPlanes)
            throw new InvalidOperationException(
                $"frame has {_planes.Length} plane(s), but {Format.PixelFormat} requires {requiredPlanes}.");
        if (_strides.Length < requiredPlanes)
            throw new InvalidOperationException(
                $"frame has {_strides.Length} stride(s), but {Format.PixelFormat} requires {requiredPlanes}.");

        for (var i = 0; i < requiredPlanes; i++)
        {
            var rowBytes = PixelFormatInfo.PlaneByteWidth(Format.PixelFormat, Format.Width, i);
            var rows = PixelFormatInfo.PlaneHeight(Format.PixelFormat, Format.Height, i);
            var stride = _strides[i];
            if (stride < rowBytes)
                throw new InvalidOperationException(
                    $"frame stride[{i}] {stride} is shorter than required row bytes {rowBytes} for {Format.PixelFormat}.");

            var requiredLength = rows <= 0 ? 0 : checked(((rows - 1) * stride) + rowBytes);
            if (_planes[i].Length < requiredLength)
                throw new InvalidOperationException(
                    $"frame plane[{i}] length {_planes[i].Length} is shorter than required {requiredLength} bytes for {Format.PixelFormat}.");
        }
    }

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
