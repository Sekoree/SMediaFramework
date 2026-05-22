namespace S.Media.Core.Video;

/// <summary>
/// Optional platform/hardware video plumbing (Vulkan external memory,
/// DRM PRIME FDs, DXGI shared handles, Metal IOSurface, …). Defaults are no-ops until a backend
/// populates <see cref="HardwareVideoSurfaceDescriptor"/>s; <see cref="HardwareVideoWin32Nv12"/>
/// shows the consumer-side import shape used by the OpenGL backend.
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
