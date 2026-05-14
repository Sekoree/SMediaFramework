using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

/// <summary>
/// Windows <see cref="IHardwareVideoInterop"/> that packs NV12 metadata for surfaces
/// shared as NT handles (for example DXGI shared textures / D3D11 resources). The opaque
/// token is a <see cref="GCHandle"/> (type <see cref="GCHandleType.Normal"/>) to
/// <see cref="Win32Nv12InteropToken"/>. Call <see cref="FreeToken"/> when the descriptor is no longer needed.
/// </summary>
/// <remarks>
/// This type only fills <see cref="HardwareVideoSurfaceDescriptor"/> — it does not open
/// DXGI devices or duplicate handles. A host that owns a decoded D3D resource duplicates
/// or exports handles before calling <see cref="AllocToken"/>.
/// Descriptors omit <see cref="HardwareVideoSurfaceDescriptor.D3D11DeviceComPtr"/> (zero); GL import still uses a
/// consumer <c>ID3D11Device</c> for <c>OpenSharedResource</c> on the NT handles until product backlog **PO-01**
/// (<c>Doc/Todo.md</c>) closes the full “zero COM on the descriptor” story.
/// When luma and chroma live in one shared resource, pass the same NT handle for both
/// planes or pass <c>0</c> for <paramref name="sharedChromaNtHandle"/> to alias the luma handle.
/// </remarks>
public sealed class WindowsNv12SharedHandleInterop : IHardwareVideoInterop
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
        if (handle.Target is not Win32Nv12InteropToken token)
            return false;

        descriptor = new HardwareVideoSurfaceDescriptor
        {
            WidthPixels = token.WidthPixels,
            HeightPixels = token.HeightPixels,
            PlaneCount = 2,
            Plane0 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.Win32SharedHandle,
                HandleOrDescriptor = token.LumaNtHandle,
                RowPitchBytes = token.YRowPitchBytes,
                Modifier = 0,
            },
            Plane1 = new HardwareVideoPlaneDescriptor
            {
                Kind = HardwareVideoMemoryKind.Win32SharedHandle,
                HandleOrDescriptor = token.ChromaNtHandle,
                RowPitchBytes = token.UvRowPitchBytes,
                Modifier = 0,
            },
        };
        return true;
    }

    /// <summary>
    /// Allocates a token describing NV12 plane NT handles and layout.
    /// </summary>
    /// <param name="sharedLumaNtHandle">Non-zero NT handle for the Y plane (or the whole NV12 resource).</param>
    /// <param name="sharedChromaNtHandle">
    /// NT handle for the UV plane, or <c>0</c> to reuse <paramref name="sharedLumaNtHandle"/> (single shared resource).
    /// </param>
    public static nint AllocToken(
        nint sharedLumaNtHandle,
        nint sharedChromaNtHandle,
        int widthPixels,
        int heightPixels,
        nuint yRowPitchBytes,
        nuint uvRowPitchBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(nameof(WindowsNv12SharedHandleInterop) + " is Windows-only.");
        if (sharedLumaNtHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(sharedLumaNtHandle), "NT handle must be non-zero.");
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));

        var chroma = sharedChromaNtHandle == 0 ? sharedLumaNtHandle : sharedChromaNtHandle;
        var token = new Win32Nv12InteropToken(sharedLumaNtHandle, chroma, widthPixels, heightPixels, yRowPitchBytes, uvRowPitchBytes);
        return GCHandle.ToIntPtr(GCHandle.Alloc(token, GCHandleType.Normal));
    }

    /// <summary>
    /// Allocates a descriptor token from <see cref="VideoWin32Nv12Backing"/> (same layout as manual <see cref="AllocToken"/>).
    /// </summary>
    public static nint AllocToken(VideoWin32Nv12Backing backing, int widthPixels, int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(backing);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(nameof(WindowsNv12SharedHandleInterop) + " is Windows-only.");

        return AllocToken(
            backing.LumaSharedNtHandle,
            backing.ChromaSharedNtHandle,
            widthPixels,
            heightPixels,
            (nuint)backing.YPlanePitchBytes,
            (nuint)backing.UvPlanePitchBytes);
    }

    /// <summary>Frees a token allocated with <see cref="AllocToken"/>.</summary>
    public static void FreeToken(nint opaqueToken)
    {
        if (opaqueToken == 0) return;
        var h = GCHandle.FromIntPtr(opaqueToken);
        h.Free();
    }
}

/// <summary>Payload for <see cref="WindowsNv12SharedHandleInterop.AllocToken"/>.</summary>
public sealed class Win32Nv12InteropToken(
    nint lumaNtHandle,
    nint chromaNtHandle,
    int widthPixels,
    int heightPixels,
    nuint yRowPitchBytes,
    nuint uvRowPitchBytes)
{
    public nint LumaNtHandle { get; } = lumaNtHandle;
    public nint ChromaNtHandle { get; } = chromaNtHandle;
    public int WidthPixels { get; } = widthPixels;
    public int HeightPixels { get; } = heightPixels;
    public nuint YRowPitchBytes { get; } = yRowPitchBytes;
    public nuint UvRowPitchBytes { get; } = uvRowPitchBytes;
}
