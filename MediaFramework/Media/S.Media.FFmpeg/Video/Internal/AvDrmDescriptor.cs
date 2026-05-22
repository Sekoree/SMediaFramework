using System.Runtime.InteropServices;
using System.Threading;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video.Internal;

internal static class AvDrmConstants
{
    internal const int MaxObjects = 4;
}

/// <summary>Matches <c>AVDRMObjectDescriptor</c> (<c>hwcontext_drm.h</c>) on gcc LP64.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct AvDrmObjectDescriptor
{
    public int Fd;
    private readonly int _alignmentPadAfterFd;
    public nuint TotalSizeBytes;
    public ulong FormatModifier;
}

/// <summary><c>AVDRMPlaneDescriptor</c></summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct AvDrmPlaneDescriptor
{
    public int ObjectIndex;
    private readonly int _alignmentPadAfterObjectIndex;
    public nint OffsetBytes;
    public nint PitchBytes;
}

/// <summary><c>AVDRMLayerDescriptor</c> (<c>AV_DRM_MAX_PLANES == 4</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct AvDrmLayerDescriptor
{
    public uint FourccDrmFormat;
    public int NbPlanes;
    public AvDrmPlaneDescriptor Plane0;
    public AvDrmPlaneDescriptor Plane1;
    public AvDrmPlaneDescriptor Plane2;
    public AvDrmPlaneDescriptor Plane3;
}

/// <summary><c>AVDRMFrameDescriptor</c>; offsets from libavutil <c>hwcontext_drm.h</c> on LP64.</summary>
[StructLayout(LayoutKind.Explicit, Size = 528)]
internal struct AvDrmFrameDescriptorInterop
{
    internal const int ExpectedSizeBytes = 528;

    private static int _loggedLayoutMismatch;

    /// <summary>One-shot warning when interop footprint disagrees with <see cref="ExpectedSizeBytes"/> (libavutil bumps).</summary>
    internal static void WarnIfInteropSizeMismatchLp64LoggedOnce()
    {
        var n = Marshal.SizeOf<AvDrmFrameDescriptorInterop>();
        if (n == ExpectedSizeBytes)
            return;
        if (Interlocked.CompareExchange(ref _loggedLayoutMismatch, 1, 0) != 0)
            return;
        MediaDiagnostics.LogWarning(
            "DRM PRIME: AvDrmFrameDescriptorInterop CLR size ({Actual} bytes) != ExpectedSizeBytes ({Expected}) — libavutil hwcontext_drm.h may have drifted from this build; DRM metadata parsing offsets may be wrong.",
            n, ExpectedSizeBytes);
    }

    [FieldOffset(0)] public int NbObjects;

    [FieldOffset(8)] public AvDrmObjectDescriptor Object0;

    [FieldOffset(32)] public AvDrmObjectDescriptor Object1;

    [FieldOffset(56)] public AvDrmObjectDescriptor Object2;

    [FieldOffset(80)] public AvDrmObjectDescriptor Object3;

    [FieldOffset(104)] public int NbLayers;

    [FieldOffset(112)] public AvDrmLayerDescriptor Layer0;

    [FieldOffset(216)] public AvDrmLayerDescriptor Layer1;

    [FieldOffset(320)] public AvDrmLayerDescriptor Layer2;

    [FieldOffset(424)] public AvDrmLayerDescriptor Layer3;
}

/// <summary>Parses DRM PRIME frame metadata (<see cref="AVPixelFormat.AV_PIX_FMT_DRM_PRIME" />) into NV12 dma-bufs.</summary>
internal static unsafe class DrmPrimeNv12BackingFactory
{
    internal static DmabufNv12Backing? TryCreateBacking(AVFrame* frame)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return TryParseNv12(frame);
    }

    private static DmabufNv12Backing? TryParseNv12(AVFrame* frame)
    {
        if (!TryParseTwoPlaneLayer(frame, DrmPixelFormats.Nv12, out var pY, out var pUv, out var hdr))
            return null;

        AvDrmObjectDescriptor yObj = GetDrmObject(in hdr, pY.ObjectIndex);
        AvDrmObjectDescriptor uvObj = GetDrmObject(in hdr, pUv.ObjectIndex);

        int dupY = FFmpegLinuxDup.Dup(yObj.Fd);
        if (dupY < 0)
            return null;

        int dupUv = FFmpegLinuxDup.Dup(uvObj.Fd);
        if (dupUv < 0)
        {
            FFmpegLinuxDup.CloseSilently(dupY);
            return null;
        }

        try
        {
            long yPitch = (long)pY.PitchBytes;
            long uvPitch = (long)pUv.PitchBytes;
            if (yPitch <= 0 || yPitch > int.MaxValue || uvPitch <= 0 || uvPitch > int.MaxValue)
                throw new InvalidOperationException("invalid DRM dma-buf pitch.");

            return new DmabufNv12Backing(dupY, pY.OffsetBytes, (int)yPitch,
                dupUv, pUv.OffsetBytes, (int)uvPitch, yObj.FormatModifier, uvObj.FormatModifier);
        }
        catch
        {
            FFmpegLinuxDup.CloseSilently(dupUv);
            FFmpegLinuxDup.CloseSilently(dupY);
            throw;
        }
    }

    internal static AvDrmObjectDescriptor GetDrmObject(in AvDrmFrameDescriptorInterop hdr, int index) =>
        index switch
        {
            0 => hdr.Object0,
            1 => hdr.Object1,
            2 => hdr.Object2,
            3 => hdr.Object3,
            _ => default,
        };

    internal static bool TryParseTwoPlaneLayer(AVFrame* frame, uint layerFourcc,
        out AvDrmPlaneDescriptor pY, out AvDrmPlaneDescriptor pUv, out AvDrmFrameDescriptorInterop hdr)
    {
        pY = default;
        pUv = default;
        hdr = default;

        if (frame == null || (AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_DRM_PRIME)
            return false;

        byte* blob = frame->data[0];
        if (blob == null)
            return false;

        AvDrmFrameDescriptorInterop.WarnIfInteropSizeMismatchLp64LoggedOnce();

        hdr = *(AvDrmFrameDescriptorInterop*)(void*)blob;

        if (hdr.NbObjects is <= 0 or > AvDrmConstants.MaxObjects ||
            hdr.NbLayers is <= 0 or > AvDrmConstants.MaxObjects)
            return false;

        ref readonly AvDrmLayerDescriptor layer = ref hdr.Layer0;
        if (layer.FourccDrmFormat != layerFourcc || layer.NbPlanes != 2)
            return false;

        pY = layer.Plane0;
        pUv = layer.Plane1;

        if ((uint)pY.ObjectIndex >= (uint)hdr.NbObjects || (uint)pUv.ObjectIndex >= (uint)hdr.NbObjects)
            return false;

        return true;
    }
}

/// <summary>Parses DRM PRIME metadata into P010 semi-planar dma-bufs (two-plane <c>DRM_FORMAT_P010</c> layer).</summary>
internal static unsafe class DrmPrimeP010BackingFactory
{
    internal static DmabufP010Backing? TryCreateBacking(AVFrame* frame)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        if (!DrmPrimeNv12BackingFactory.TryParseTwoPlaneLayer(frame, DrmPixelFormats.P010, out var pY, out var pUv,
                out var hdr))
            return null;

        AvDrmObjectDescriptor yObj = DrmPrimeNv12BackingFactory.GetDrmObject(in hdr, pY.ObjectIndex);
        AvDrmObjectDescriptor uvObj = DrmPrimeNv12BackingFactory.GetDrmObject(in hdr, pUv.ObjectIndex);

        int dupY = FFmpegLinuxDup.Dup(yObj.Fd);
        if (dupY < 0)
            return null;

        int dupUv = FFmpegLinuxDup.Dup(uvObj.Fd);
        if (dupUv < 0)
        {
            FFmpegLinuxDup.CloseSilently(dupY);
            return null;
        }

        try
        {
            long yPitch = (long)pY.PitchBytes;
            long uvPitch = (long)pUv.PitchBytes;
            if (yPitch <= 0 || yPitch > int.MaxValue || uvPitch <= 0 || uvPitch > int.MaxValue)
                throw new InvalidOperationException("invalid DRM dma-buf pitch.");

            return new DmabufP010Backing(dupY, pY.OffsetBytes, (int)yPitch,
                dupUv, pUv.OffsetBytes, (int)uvPitch, yObj.FormatModifier, uvObj.FormatModifier);
        }
        catch
        {
            FFmpegLinuxDup.CloseSilently(dupUv);
            FFmpegLinuxDup.CloseSilently(dupY);
            throw;
        }
    }
}

/// <summary>Parses DRM PRIME metadata into P016 semi-planar dma-bufs (two-plane <c>DRM_FORMAT_P016</c> layer).</summary>
internal static unsafe class DrmPrimeP016BackingFactory
{
    internal static DmabufP016Backing? TryCreateBacking(AVFrame* frame)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        if (!DrmPrimeNv12BackingFactory.TryParseTwoPlaneLayer(frame, DrmPixelFormats.P016, out var pY, out var pUv,
                out var hdr))
            return null;

        AvDrmObjectDescriptor yObj = DrmPrimeNv12BackingFactory.GetDrmObject(in hdr, pY.ObjectIndex);
        AvDrmObjectDescriptor uvObj = DrmPrimeNv12BackingFactory.GetDrmObject(in hdr, pUv.ObjectIndex);

        int dupY = FFmpegLinuxDup.Dup(yObj.Fd);
        if (dupY < 0)
            return null;

        int dupUv = FFmpegLinuxDup.Dup(uvObj.Fd);
        if (dupUv < 0)
        {
            FFmpegLinuxDup.CloseSilently(dupY);
            return null;
        }

        try
        {
            long yPitch = (long)pY.PitchBytes;
            long uvPitch = (long)pUv.PitchBytes;
            if (yPitch <= 0 || yPitch > int.MaxValue || uvPitch <= 0 || uvPitch > int.MaxValue)
                throw new InvalidOperationException("invalid DRM dma-buf pitch.");

            return new DmabufP016Backing(dupY, pY.OffsetBytes, (int)yPitch,
                dupUv, pUv.OffsetBytes, (int)uvPitch, yObj.FormatModifier, uvObj.FormatModifier);
        }
        catch
        {
            FFmpegLinuxDup.CloseSilently(dupUv);
            FFmpegLinuxDup.CloseSilently(dupY);
            throw;
        }
    }
}
