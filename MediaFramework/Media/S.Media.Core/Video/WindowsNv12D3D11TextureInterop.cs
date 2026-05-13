using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Windows <see cref="IHardwareVideoInterop"/> that describes an NV12 <c>ID3D11Texture2D</c> already held by
/// libav / D3D11VA (same process, same <c>ID3D11Device</c> as decode) — no DXGI NT shared-handle duplication.
/// The opaque token is a <see cref="GCHandle"/> to <see cref="Win32Nv12D3D11TextureInteropToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// This fills <see cref="HardwareVideoSurfaceDescriptor"/> only — it does not call D3D11, DXGI, or GL.
/// Callers must keep the underlying <c>AVFrame</c> / hw frames alive while the descriptor is used.
/// </para>
/// <para>
/// <see cref="HardwareVideoSurfaceDescriptor.D3D11DeviceComPtr"/> and both planes’
/// <see cref="HardwareVideoPlaneDescriptor.HandleOrDescriptor"/> are <b>non-owning</b> COM pointers.
/// A future GL path can bind or copy from the texture on the same device without <c>OpenSharedResource</c>.
/// </para>
/// </remarks>
public sealed class WindowsNv12D3D11TextureInterop : IHardwareVideoInterop
{
    /// <inheritdoc />
    public bool IsGpuImportSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public bool TryDescribeImportedSurface(nint opaqueToken, out HardwareVideoSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (!OperatingSystem.IsWindows() || opaqueToken == 0)
            return false;

        var handle = GCHandle.FromIntPtr(opaqueToken);
        if (handle.Target is not Win32Nv12D3D11TextureInteropToken token)
            return false;

        var slice = (ulong)Math.Max(0, token.ArraySlice);
        descriptor = new HardwareVideoSurfaceDescriptor
        {
            WidthPixels = token.WidthPixels,
            HeightPixels = token.HeightPixels,
            PlaneCount = 2,
            D3D11DeviceComPtr = token.DeviceComPtr,
            Plane0 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.Win32D3D11Nv12Texture,
                HandleOrDescriptor = token.TextureComPtr,
                RowPitchBytes = token.YRowPitchBytes,
                Modifier = slice,
            },
            Plane1 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.Win32D3D11Nv12Texture,
                HandleOrDescriptor = token.TextureComPtr,
                RowPitchBytes = token.UvRowPitchBytes,
                Modifier = slice,
            },
        };
        return true;
    }

    /// <summary>Allocates a token for a libav-owned NV12 D3D11 texture (non-owning COM pointers).</summary>
    public static nint AllocToken(
        nint d3d11DeviceComPtr,
        nint d3d11Texture2DComPtr,
        int arraySlice,
        int widthPixels,
        int heightPixels,
        nuint yRowPitchBytes,
        nuint uvRowPitchBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(nameof(WindowsNv12D3D11TextureInterop) + " is Windows-only.");
        if (d3d11DeviceComPtr == 0)
            throw new ArgumentOutOfRangeException(nameof(d3d11DeviceComPtr), "D3D11 device COM pointer must be non-zero.");
        if (d3d11Texture2DComPtr == 0)
            throw new ArgumentOutOfRangeException(nameof(d3d11Texture2DComPtr), "D3D11 texture COM pointer must be non-zero.");
        if (arraySlice < 0)
            throw new ArgumentOutOfRangeException(nameof(arraySlice));
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (yRowPitchBytes == 0) throw new ArgumentOutOfRangeException(nameof(yRowPitchBytes));
        if (uvRowPitchBytes == 0) throw new ArgumentOutOfRangeException(nameof(uvRowPitchBytes));

        var token = new Win32Nv12D3D11TextureInteropToken(
            d3d11DeviceComPtr,
            d3d11Texture2DComPtr,
            arraySlice,
            widthPixels,
            heightPixels,
            yRowPitchBytes,
            uvRowPitchBytes);
        return GCHandle.ToIntPtr(GCHandle.Alloc(token, GCHandleType.Normal));
    }

    /// <summary>Frees a token allocated with <see cref="AllocToken"/>.</summary>
    public static void FreeToken(nint opaqueToken)
    {
        if (opaqueToken == 0) return;
        var h = GCHandle.FromIntPtr(opaqueToken);
        h.Free();
    }
}

/// <summary>Payload for <see cref="WindowsNv12D3D11TextureInterop.AllocToken"/>.</summary>
public sealed class Win32Nv12D3D11TextureInteropToken(
    nint deviceComPtr,
    nint textureComPtr,
    int arraySlice,
    int widthPixels,
    int heightPixels,
    nuint yRowPitchBytes,
    nuint uvRowPitchBytes)
{
    public nint DeviceComPtr { get; } = deviceComPtr;
    public nint TextureComPtr { get; } = textureComPtr;
    public int ArraySlice { get; } = arraySlice;
    public int WidthPixels { get; } = widthPixels;
    public int HeightPixels { get; } = heightPixels;
    public nuint YRowPitchBytes { get; } = yRowPitchBytes;
    public nuint UvRowPitchBytes { get; } = uvRowPitchBytes;
}
