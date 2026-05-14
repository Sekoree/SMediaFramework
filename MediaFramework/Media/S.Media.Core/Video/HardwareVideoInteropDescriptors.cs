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
    /// <summary>
    /// Windows D3D11: single NV12 <c>ID3D11Texture2D</c> COM pointer (non-owning; lifetime follows libav / decode pool).
    /// Pair with <see cref="HardwareVideoSurfaceDescriptor.D3D11DeviceComPtr"/> for the device that created the texture.
    /// </summary>
    Win32D3D11Nv12Texture = 5,
}

/// <summary>One decoded plane (Y, UV, or surface layer) for zero-copy import.</summary>
public readonly struct HardwareVideoPlaneDescriptor
{
    public HardwareVideoMemoryKind Kind { get; init; }
    /// <summary>
    /// FD, NT handle, IOSurface ref, Vulkan handle, D3D11 <c>ID3D11Texture2D*</c> COM pointer for
    /// <see cref="HardwareVideoMemoryKind.Win32D3D11Nv12Texture"/>, … — zero when unspecified.
    /// </summary>
    public nint HandleOrDescriptor { get; init; }
    public nuint RowPitchBytes { get; init; }
    public ulong Modifier { get; init; }

    /// <summary>
    /// When <see cref="Kind"/> is <see cref="HardwareVideoMemoryKind.VulkanExternal"/>, the Vulkan
    /// <c>VkExternalMemoryHandleTypeFlagBits</c> value used to obtain <see cref="HandleOrDescriptor"/>.
    /// When <see cref="Kind"/> is <see cref="HardwareVideoMemoryKind.Win32D3D11Nv12Texture"/>,
    /// <see cref="Modifier"/> is the D3D11 texture array slice (use <c>0</c> for non-array textures).
    /// When <see cref="Kind"/> is another kind, callers should leave <see cref="Modifier"/> <c>0</c> unless a backend documents otherwise.
    /// </summary>
    public uint ExternalMemoryHandleType { get; init; }
}

/// <summary>Portable bundle for up to four planes (covers most YUV + RGB surfaces).</summary>
public readonly struct HardwareVideoSurfaceDescriptor
{
    public int WidthPixels { get; init; }
    public int HeightPixels { get; init; }
    public byte PlaneCount { get; init; }
    /// <summary>
    /// When planes use <see cref="HardwareVideoMemoryKind.Win32D3D11Nv12Texture"/>, the libav / decode
    /// <c>ID3D11Device</c> COM pointer (non-owning) that owns the texture in <see cref="Plane0"/> / <see cref="Plane1"/>.
    /// For <see cref="HardwareVideoMemoryKind.Win32SharedHandle"/> NV12, callers must leave this <c>0</c> (see
    /// <see cref="HardwareVideoWin32Nv12.TryCreateWin32Nv12Backing"/>); the GL importer still uses a separate
    /// consumer <c>ID3D11Device</c> for <c>OpenSharedResource</c> on those NT handles — that device is not part
    /// of this descriptor. Eliminating decode-path and consumer-device COM from the portable descriptor while
    /// retaining safe DXGI import is product backlog (**PO-01**, <c>Doc/Todo.md</c> §Tier F row 34 <c>Open</c> tail).
    /// </summary>
    public nint D3D11DeviceComPtr { get; init; }
    public HardwareVideoPlaneDescriptor Plane0 { get; init; }
    public HardwareVideoPlaneDescriptor Plane1 { get; init; }
    public HardwareVideoPlaneDescriptor Plane2 { get; init; }
    public HardwareVideoPlaneDescriptor Plane3 { get; init; }
}

/// <summary>Default stub — satisfies <see cref="IHardwareVideoInterop"/> until a platform backend lands.</summary>
public sealed class NoOpHardwareVideoInterop : IHardwareVideoInterop;
