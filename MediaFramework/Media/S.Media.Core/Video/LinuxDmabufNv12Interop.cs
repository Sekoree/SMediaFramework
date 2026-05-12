using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Linux-only <see cref="IHardwareVideoInterop"/> that describes NV12 DRM PRIME
/// dma-bufs for import into GL/Vulkan. The opaque token is a <see cref="GCHandle"/>
/// (type <see cref="GCHandleType.Normal"/>) to <see cref="DmabufNv12InteropToken"/>.
/// Call <see cref="FreeToken"/> when the descriptor is no longer needed.
/// </summary>
public sealed class LinuxDmabufNv12Interop : IHardwareVideoInterop
{
    /// <inheritdoc />
    public bool IsGpuImportSupported => OperatingSystem.IsLinux();

    /// <inheritdoc />
    public bool TryDescribeImportedSurface(nint opaqueToken, out HardwareVideoSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (!OperatingSystem.IsLinux() || opaqueToken == 0)
            return false;

        var handle = GCHandle.FromIntPtr(opaqueToken);
        if (handle.Target is not DmabufNv12InteropToken token)
            return false;

        var b = token.Backing;
        descriptor = new HardwareVideoSurfaceDescriptor
        {
            WidthPixels = token.WidthPixels,
            HeightPixels = token.HeightPixels,
            PlaneCount = 2,
            Plane0 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.LinuxDmabufFd,
                HandleOrDescriptor = b.YPlaneFd,
                RowPitchBytes = (nuint)b.YPlanePitchBytes,
                Modifier = b.YPlaneDrmFormatModifier,
            },
            Plane1 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.LinuxDmabufFd,
                HandleOrDescriptor = b.UvPlaneFd,
                RowPitchBytes = (nuint)b.UvPlanePitchBytes,
                Modifier = b.UvPlaneDrmFormatModifier,
            },
        };
        return true;
    }

    /// <summary>Packs backing + dimensions for <see cref="TryDescribeImportedSurface"/>.</summary>
    public static nint AllocToken(VideoDmabufNv12Backing backing, int widthPixels, int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(backing);
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(nameof(LinuxDmabufNv12Interop) + " is Linux-only.");

        var token = new DmabufNv12InteropToken(backing, widthPixels, heightPixels);
        return GCHandle.ToIntPtr(GCHandle.Alloc(token));
    }

    /// <summary>Frees a token allocated with <see cref="AllocToken"/>.</summary>
    public static void FreeToken(nint opaqueToken)
    {
        if (opaqueToken == 0) return;
        var h = GCHandle.FromIntPtr(opaqueToken);
        h.Free();
    }
}

/// <summary>Payload for <see cref="LinuxDmabufNv12Interop.AllocToken"/>.</summary>
public sealed class DmabufNv12InteropToken(VideoDmabufNv12Backing backing, int widthPixels, int heightPixels)
{
    public VideoDmabufNv12Backing Backing { get; } = backing;
    public int WidthPixels { get; } = widthPixels;
    public int HeightPixels { get; } = heightPixels;
}
