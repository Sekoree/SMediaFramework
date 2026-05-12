namespace S.Media.Core.Video;

/// <summary>
/// Optional platform/hardware video plumbing (Vulkan external memory,
/// DRM PRIME FDs, DXGI shared handles, Metal IOSurface, …). Defaults are no-ops until a backend
/// populates descriptors. Concrete packers include <see cref="LinuxDmabufNv12Interop"/>,
/// <see cref="WindowsNv12SharedHandleInterop"/>, <see cref="WindowsNv12SharedHandleInterop.AllocToken(VideoWin32Nv12Backing, int, int)"/>, <see cref="VulkanExternalNv12Interop"/>, and
/// <see cref="MetalIosurfaceNv12Interop"/>.
/// </summary>
public interface IHardwareVideoInterop
{
    /// <summary>Often zero — reserved for handles such as Vulkan instance/device.</summary>
    nint PlatformContextHandle => 0;

    /// <summary>True once a backing implementation exposes importable textures/buffers.</summary>
    bool IsGpuImportSupported => false;

    /// <summary>
    /// Future path: opaque token tying a decoded libav / platform surface to planes.
    /// Default: not implemented.
    /// </summary>
    bool TryDescribeImportedSurface(nint opaqueToken, out HardwareVideoSurfaceDescriptor descriptor)
    {
        descriptor = default;
        return false;
    }
}
