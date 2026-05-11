using System.Runtime.InteropServices;
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
    internal static VideoDmabufNv12Backing? TryCreateBacking(AVFrame* frame)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return TryParseNv12(frame);
    }

    private static VideoDmabufNv12Backing? TryParseNv12(AVFrame* frame)
    {
        if (frame == null || (AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_DRM_PRIME)
            return null;

        byte* blob = frame->data[0];
        if (blob == null)
            return null;

        ref readonly var hdr =
            ref *(AvDrmFrameDescriptorInterop*)(void*)blob;

        if (hdr.NbObjects is <= 0 or > AvDrmConstants.MaxObjects ||
            hdr.NbLayers is <= 0 or > AvDrmConstants.MaxObjects)
            return null;

        ref readonly AvDrmLayerDescriptor layer = ref hdr.Layer0;
        if (layer.FourccDrmFormat != DrmPixelFormats.Nv12 || layer.NbPlanes != 2)
            return null;

        ref readonly var pY = ref layer.Plane0;
        ref readonly var pUv = ref layer.Plane1;

        if ((uint)pY.ObjectIndex >= (uint)hdr.NbObjects || (uint)pUv.ObjectIndex >= (uint)hdr.NbObjects)
            return null;

        AvDrmObjectDescriptor yObj = GetObject(in hdr, pY.ObjectIndex);
        AvDrmObjectDescriptor uvObj = GetObject(in hdr, pUv.ObjectIndex);

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
            ulong modifier = uvObj.FormatModifier != 0 ? uvObj.FormatModifier : yObj.FormatModifier;
            long yPitch = (long)pY.PitchBytes;
            long uvPitch = (long)pUv.PitchBytes;
            if (yPitch <= 0 || yPitch > int.MaxValue || uvPitch <= 0 || uvPitch > int.MaxValue)
                throw new InvalidOperationException("invalid DRM dma-buf pitch.");

            return new VideoDmabufNv12Backing(dupY, pY.OffsetBytes, (int)yPitch,
                dupUv, pUv.OffsetBytes, (int)uvPitch, modifier);
        }
        catch
        {
            FFmpegLinuxDup.CloseSilently(dupUv);
            FFmpegLinuxDup.CloseSilently(dupY);
            throw;
        }
    }

    private static AvDrmObjectDescriptor GetObject(in AvDrmFrameDescriptorInterop hdr, int index) =>
        index switch
        {
            0 => hdr.Object0,
            1 => hdr.Object1,
            2 => hdr.Object2,
            3 => hdr.Object3,
            _ => default,
        };
}
