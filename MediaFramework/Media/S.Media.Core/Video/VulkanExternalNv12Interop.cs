using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// <see cref="IHardwareVideoInterop"/> that packs NV12 plane handles for Vulkan
/// <see href="https://registry.khronos.org/vulkan/specs/latest/man/html/VkImportMemoryWin32HandleInfoKHR.html">external memory import</see>
/// (Win32 NT handle, POSIX FD, … depending on <see cref="VulkanExternalNv12InteropToken.ExternalMemoryHandleType"/>).
/// The opaque token is a <see cref="GCHandle"/> to <see cref="VulkanExternalNv12InteropToken"/>.
/// Call <see cref="FreeToken"/> when the descriptor is no longer needed.
/// </summary>
/// <remarks>
/// This type only fills <see cref="HardwareVideoSurfaceDescriptor"/> — it does not create Vulkan
/// devices or call <c>vkAllocateMemory</c>. A host performs import using the handles and
/// <see cref="HardwareVideoPlaneDescriptor.ExternalMemoryHandleType"/>.
/// For a single <c>VkDeviceMemory</c> block with Y and UV sub-ranges, pass the same handle for both
/// planes and set <see cref="VulkanExternalNv12InteropToken.UvPlaneByteOffset"/> to the UV base offset;
/// it is surfaced as <see cref="HardwareVideoPlaneDescriptor.Modifier"/> on plane 1 (bytes from the
/// allocation start — not a DRM format modifier).
/// </remarks>
public sealed class VulkanExternalNv12Interop : IHardwareVideoInterop
{
    /// <inheritdoc />
    public bool IsGpuImportSupported => IsVulkanInteropHostOs();

    /// <inheritdoc />
    public bool TryDescribeImportedSurface(nint opaqueToken, out HardwareVideoSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (!IsVulkanInteropHostOs() || opaqueToken == 0)
            return false;

        var handle = GCHandle.FromIntPtr(opaqueToken);
        if (handle.Target is not VulkanExternalNv12InteropToken token)
            return false;

        var uvHandle = token.UvPlaneMemoryHandle == 0 ? token.YPlaneMemoryHandle : token.UvPlaneMemoryHandle;
        descriptor = new HardwareVideoSurfaceDescriptor
        {
            WidthPixels = token.WidthPixels,
            HeightPixels = token.HeightPixels,
            PlaneCount = 2,
            Plane0 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.VulkanExternal,
                HandleOrDescriptor = token.YPlaneMemoryHandle,
                RowPitchBytes = token.YRowPitchBytes,
                Modifier = 0,
                ExternalMemoryHandleType = token.ExternalMemoryHandleType,
            },
            Plane1 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.VulkanExternal,
                HandleOrDescriptor = uvHandle,
                RowPitchBytes = token.UvRowPitchBytes,
                Modifier = token.UvPlaneByteOffset,
                ExternalMemoryHandleType = token.ExternalMemoryHandleType,
            },
        };
        return true;
    }

    /// <summary>Allocates a token describing NV12 external-memory handles and layout.</summary>
    /// <param name="yPlaneMemoryHandle">Opaque memory import handle for the Y plane (or whole NV12 block).</param>
    /// <param name="uvPlaneMemoryHandle"><c>0</c> to reuse <paramref name="yPlaneMemoryHandle"/>.</param>
    /// <param name="externalMemoryHandleType">Vulkan <c>VkExternalMemoryHandleTypeFlagBits</c> value.</param>
    /// <param name="uvPlaneByteOffset">When Y/UV share one allocation, byte offset to UV data (stored in plane1 <see cref="HardwareVideoPlaneDescriptor.Modifier"/>).</param>
    public static nint AllocToken(
        nint yPlaneMemoryHandle,
        nint uvPlaneMemoryHandle,
        uint externalMemoryHandleType,
        int widthPixels,
        int heightPixels,
        nuint yRowPitchBytes,
        nuint uvRowPitchBytes,
        ulong uvPlaneByteOffset = 0)
    {
        if (!IsVulkanInteropHostOs())
            throw new PlatformNotSupportedException(nameof(VulkanExternalNv12Interop) + " is not supported on this OS.");
        if (yPlaneMemoryHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(yPlaneMemoryHandle), "Memory import handle must be non-zero.");
        if (externalMemoryHandleType == 0)
            throw new ArgumentOutOfRangeException(nameof(externalMemoryHandleType), "Vulkan external memory handle type must be non-zero.");
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));

        var token = new VulkanExternalNv12InteropToken(
            yPlaneMemoryHandle,
            uvPlaneMemoryHandle,
            externalMemoryHandleType,
            widthPixels,
            heightPixels,
            yRowPitchBytes,
            uvRowPitchBytes,
            uvPlaneByteOffset);
        return GCHandle.ToIntPtr(GCHandle.Alloc(token, GCHandleType.Normal));
    }

    /// <summary>Frees a token allocated with <see cref="AllocToken"/>.</summary>
    public static void FreeToken(nint opaqueToken)
    {
        if (opaqueToken == 0) return;
        var h = GCHandle.FromIntPtr(opaqueToken);
        h.Free();
    }

    private static bool IsVulkanInteropHostOs()
        => OperatingSystem.IsLinux()
           || OperatingSystem.IsWindows()
           || OperatingSystem.IsMacOS()
           || OperatingSystem.IsMacCatalyst()
           || OperatingSystem.IsAndroid()
           || OperatingSystem.IsIOS()
           || OperatingSystem.IsTvOS();
}

/// <summary>Payload for <see cref="VulkanExternalNv12Interop.AllocToken"/>.</summary>
public sealed class VulkanExternalNv12InteropToken(
    nint yPlaneMemoryHandle,
    nint uvPlaneMemoryHandle,
    uint externalMemoryHandleType,
    int widthPixels,
    int heightPixels,
    nuint yRowPitchBytes,
    nuint uvRowPitchBytes,
    ulong uvPlaneByteOffset)
{
    public nint YPlaneMemoryHandle { get; } = yPlaneMemoryHandle;
    /// <summary><c>0</c> means “same as <see cref="YPlaneMemoryHandle"/>”.</summary>
    public nint UvPlaneMemoryHandle { get; } = uvPlaneMemoryHandle;
    public uint ExternalMemoryHandleType { get; } = externalMemoryHandleType;
    public int WidthPixels { get; } = widthPixels;
    public int HeightPixels { get; } = heightPixels;
    public nuint YRowPitchBytes { get; } = yRowPitchBytes;
    public nuint UvRowPitchBytes { get; } = uvRowPitchBytes;
    /// <summary>Byte offset from shared allocation base to UV plane (plane1 <see cref="HardwareVideoPlaneDescriptor.Modifier"/>).</summary>
    public ulong UvPlaneByteOffset { get; } = uvPlaneByteOffset;
}
