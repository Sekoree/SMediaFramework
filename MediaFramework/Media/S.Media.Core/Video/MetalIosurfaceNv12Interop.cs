using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Apple-platform <see cref="IHardwareVideoInterop"/> that packs NV12 metadata for
/// <see href="https://developer.apple.com/documentation/iosurface">IOSurface</see> references
/// (Metal / CoreVideo zero-copy). The opaque token is a <see cref="GCHandle"/> to
/// <see cref="MetalIosurfaceNv12InteropToken"/>. Call <see cref="FreeToken"/> when the descriptor is no longer needed.
/// </summary>
/// <remarks>
/// <see cref="HardwareVideoPlaneDescriptor.HandleOrDescriptor"/> carries the raw <c>IOSurfaceRef</c>
/// (<see cref="nint"/>). This assembly does not P/Invoke IOSurface APIs — hosts map the pointer to Metal
/// textures or GL/EGL consumers. When luma and chroma share one IOSurface, pass <c>0</c> for
/// <paramref name="uvIosurfaceRef"/> to reuse the luma surface pointer.
/// </remarks>
public sealed class MetalIosurfaceNv12Interop : IHardwareVideoInterop
{
    /// <inheritdoc />
    public bool IsGpuImportSupported => IsAppleHardwareVideoOs();

    /// <inheritdoc />
    public bool TryDescribeImportedSurface(nint opaqueToken, out HardwareVideoSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (!IsAppleHardwareVideoOs() || opaqueToken == 0)
            return false;

        var handle = GCHandle.FromIntPtr(opaqueToken);
        if (handle.Target is not MetalIosurfaceNv12InteropToken token)
            return false;

        var uvRef = token.UvIosurfaceRef == 0 ? token.YIosurfaceRef : token.UvIosurfaceRef;
        descriptor = new HardwareVideoSurfaceDescriptor
        {
            WidthPixels = token.WidthPixels,
            HeightPixels = token.HeightPixels,
            PlaneCount = 2,
            Plane0 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.MetalIoSurface,
                HandleOrDescriptor = token.YIosurfaceRef,
                RowPitchBytes = token.YRowPitchBytes,
                Modifier = 0,
            },
            Plane1 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.MetalIoSurface,
                HandleOrDescriptor = uvRef,
                RowPitchBytes = token.UvRowPitchBytes,
                Modifier = 0,
            },
        };
        return true;
    }

    /// <summary>Allocates a token describing NV12 IOSurface pointers and layout.</summary>
    /// <param name="yIosurfaceRef">Non-zero <c>IOSurfaceRef</c> for the Y plane (or combined NV12 surface).</param>
    /// <param name="uvIosurfaceRef"><c>0</c> to reuse <paramref name="yIosurfaceRef"/>.</param>
    public static nint AllocToken(
        nint yIosurfaceRef,
        nint uvIosurfaceRef,
        int widthPixels,
        int heightPixels,
        nuint yRowPitchBytes,
        nuint uvRowPitchBytes)
    {
        if (!IsAppleHardwareVideoOs())
            throw new PlatformNotSupportedException(nameof(MetalIosurfaceNv12Interop) + " is Apple-platform-only.");
        if (yIosurfaceRef == 0)
            throw new ArgumentOutOfRangeException(nameof(yIosurfaceRef), "IOSurfaceRef must be non-zero.");
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));

        var token = new MetalIosurfaceNv12InteropToken(yIosurfaceRef, uvIosurfaceRef, widthPixels, heightPixels, yRowPitchBytes, uvRowPitchBytes);
        return GCHandle.ToIntPtr(GCHandle.Alloc(token, GCHandleType.Normal));
    }

    /// <summary>Frees a token allocated with <see cref="AllocToken"/>.</summary>
    public static void FreeToken(nint opaqueToken)
    {
        if (opaqueToken == 0) return;
        var h = GCHandle.FromIntPtr(opaqueToken);
        h.Free();
    }

    private static bool IsAppleHardwareVideoOs()
        => OperatingSystem.IsMacOS()
           || OperatingSystem.IsMacCatalyst()
           || OperatingSystem.IsIOS()
           || OperatingSystem.IsTvOS();
}

/// <summary>Payload for <see cref="MetalIosurfaceNv12Interop.AllocToken"/>.</summary>
public sealed class MetalIosurfaceNv12InteropToken(
    nint yIosurfaceRef,
    nint uvIosurfaceRef,
    int widthPixels,
    int heightPixels,
    nuint yRowPitchBytes,
    nuint uvRowPitchBytes)
{
    public nint YIosurfaceRef { get; } = yIosurfaceRef;
    public nint UvIosurfaceRef { get; } = uvIosurfaceRef;
    public int WidthPixels { get; } = widthPixels;
    public int HeightPixels { get; } = heightPixels;
    public nuint YRowPitchBytes { get; } = yRowPitchBytes;
    public nuint UvRowPitchBytes { get; } = uvRowPitchBytes;
}
