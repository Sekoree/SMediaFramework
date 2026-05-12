namespace S.Media.Core.Video;

/// <summary>Classifies native memory attached to a hardware video plane.</summary>
public enum HardwareVideoMemoryKind : byte
{
    None = 0,
    /// <summary>Linux DRM PRIME / DMA-BUF file descriptor.</summary>
    LinuxDmabufFd = 1,
    /// <summary>Windows NT shared handle (e.g. DXGI shared resource).</summary>
    Win32SharedHandle = 2,
    /// <summary>Apple IOSurface / Metal texture token.</summary>
    MetalIoSurface = 3,
    /// <summary>Vulkan external memory handle (exact type is platform-specific).</summary>
    VulkanExternal = 4,
}

/// <summary>One decoded plane (Y, UV, or surface layer) for zero-copy import.</summary>
public readonly struct HardwareVideoPlaneDescriptor
{
    public HardwareVideoMemoryKind Kind { get; init; }
    /// <summary>FD, NT handle, IOSurface ref, Vulkan handle, … — zero when unspecified.</summary>
    public nint HandleOrDescriptor { get; init; }
    public nuint RowPitchBytes { get; init; }
    public ulong Modifier { get; init; }

    /// <summary>
    /// When <see cref="Kind"/> is <see cref="HardwareVideoMemoryKind.VulkanExternal"/>, the Vulkan
    /// <c>VkExternalMemoryHandleTypeFlagBits</c> value used to obtain <see cref="HandleOrDescriptor"/>.
    /// When <see cref="Kind"/> is another kind, callers should leave this <c>0</c> unless a backend documents otherwise.
    /// </summary>
    public uint ExternalMemoryHandleType { get; init; }
}

/// <summary>Portable bundle for up to four planes (covers most YUV + RGB surfaces).</summary>
public readonly struct HardwareVideoSurfaceDescriptor
{
    public int WidthPixels { get; init; }
    public int HeightPixels { get; init; }
    public byte PlaneCount { get; init; }
    public HardwareVideoPlaneDescriptor Plane0 { get; init; }
    public HardwareVideoPlaneDescriptor Plane1 { get; init; }
    public HardwareVideoPlaneDescriptor Plane2 { get; init; }
    public HardwareVideoPlaneDescriptor Plane3 { get; init; }
}

/// <summary>Default stub — satisfies <see cref="IHardwareVideoInterop"/> until a platform backend lands.</summary>
public sealed class NoOpHardwareVideoInterop : IHardwareVideoInterop;
