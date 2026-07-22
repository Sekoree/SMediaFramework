using System.Diagnostics.CodeAnalysis;

namespace S.Media.Core.Video;

/// <summary>
/// Builds <see cref="Win32SharedNv12Backing"/> from a <see cref="HardwareVideoSurfaceDescriptor"/> so GL / upload
/// hosts can import NV12 without constructing a full <see cref="VideoFrame"/>.
/// </summary>
/// <remarks>
/// <see cref="HardwareVideoMemoryKind.Win32SharedHandle"/> descriptors carry DXGI NT handles only
/// (<see cref="HardwareVideoSurfaceDescriptor.D3D11DeviceComPtr"/> must be zero). That clears libav COM
/// from the portable descriptor and from <see cref="Win32SharedNv12Backing"/> for the handle-only decode export path;
/// the GL stack still binds a consumer <c>ID3D11Device</c> (negotiated borrow, SDL interop host, or lazy
/// creation from a decoded COM-backed frame) to call <c>OpenSharedResource</c> on those handles. Full
/// “zero COM on the descriptor” including removing that consumer-device dependency is **PO-01**
///.
/// </remarks>
public static class HardwareVideoWin32Nv12
{
    /// <summary>
    /// Supports both Windows NV12 descriptor flavours: <see cref="HardwareVideoMemoryKind.Win32SharedHandle"/>
    /// (DXGI NT handles, zero device-COM) and <see cref="HardwareVideoMemoryKind.Win32D3D11Nv12Texture"/>
    /// (single COM texture + matching device when the decode device equals the GL D3D11 device).
    /// </summary>
    public static bool TryCreateWin32Nv12Backing(
        in HardwareVideoSurfaceDescriptor descriptor,
        [NotNullWhen(true)] out Win32SharedNv12Backing? backing,
        out string? failureMessage)
    {
        backing = null;
        failureMessage = null;

        if (!OperatingSystem.IsWindows())
        {
            failureMessage = "Win32 NV12 backing is Windows-only.";
            return false;
        }

        if (descriptor.PlaneCount != 2)
        {
            failureMessage = "Expected exactly two planes for NV12.";
            return false;
        }

        var p0 = descriptor.Plane0;
        var p1 = descriptor.Plane1;

        if (p0.Kind == HardwareVideoMemoryKind.Win32SharedHandle
            && p1.Kind == HardwareVideoMemoryKind.Win32SharedHandle)
        {
            if (p0.HandleOrDescriptor == 0)
            {
                failureMessage = "Plane0 Win32 shared handle is null.";
                return false;
            }

            var chroma = p1.HandleOrDescriptor == 0 ? p0.HandleOrDescriptor : p1.HandleOrDescriptor;
            if (descriptor.D3D11DeviceComPtr != 0)
            {
                failureMessage = "Win32SharedHandle descriptors must leave D3D11DeviceComPtr at zero.";
                return false;
            }

            backing = new Win32SharedNv12Backing(
                p0.HandleOrDescriptor,
                chroma,
                (int)p0.RowPitchBytes,
                (int)p1.RowPitchBytes,
                d3d11TextureArraySliceIndex: 0,
                libavD3D11DeviceComPtr: 0,
                libavD3D11Texture2DComPtr: 0);
            return true;
        }

        if (p0.Kind == HardwareVideoMemoryKind.Win32D3D11Nv12Texture
            && p1.Kind == HardwareVideoMemoryKind.Win32D3D11Nv12Texture)
        {
            if (descriptor.D3D11DeviceComPtr == 0)
            {
                failureMessage = "D3D11DeviceComPtr is required for Win32D3D11Nv12Texture planes.";
                return false;
            }

            if (p0.HandleOrDescriptor == 0 || p1.HandleOrDescriptor == 0)
            {
                failureMessage = "D3D11 texture COM pointers must be non-zero.";
                return false;
            }

            if (p0.HandleOrDescriptor != p1.HandleOrDescriptor)
            {
                failureMessage = "NV12 D3D11 import expects the same texture COM pointer on both planes.";
                return false;
            }

            if (p0.Modifier != p1.Modifier)
            {
                failureMessage = "NV12 D3D11 plane modifiers (array slice) must match.";
                return false;
            }

            var slice = (int)p0.Modifier;
            backing = new Win32SharedNv12Backing(
                sharedLumaNtHandle: 0,
                sharedChromaNtHandle: 0,
                (int)p0.RowPitchBytes,
                (int)p1.RowPitchBytes,
                slice,
                libavD3D11DeviceComPtr: descriptor.D3D11DeviceComPtr,
                libavD3D11Texture2DComPtr: p0.HandleOrDescriptor);
            return true;
        }

        failureMessage = "Descriptor is not a supported Win32 NV12 layout (shared handles or D3D11 COM texture).";
        return false;
    }
}
