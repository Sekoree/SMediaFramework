namespace S.Media.OpenGL;

/// <summary>
/// Reports which decoded pixel formats can use the shipped DRM PRIME dma-buf → EGL/GL import path today
/// (<see cref="S.Media.Core.Video.PixelFormat.Nv12"/>, <see cref="S.Media.Core.Video.PixelFormat.P010"/>, <see cref="S.Media.Core.Video.PixelFormat.P016"/>).
/// Additional FOURCCs and multi-planar FFmpeg → GL paths remain deferred (see <c>Doc/Todo.md</c> §Tier F row 36, partial).
/// </summary>
public static class LinuxDmabufGlHardwareFormats
{
    /// <summary>Returns a short diagnostic string when <paramref name="pixelFormat"/> cannot use the shipped EGL dma-buf import path, or <c>null</c> when it can.</summary>
    public static string? GetPrimeGlImportBlocker(S.Media.Core.Video.PixelFormat pixelFormat)
    {
        if (pixelFormat is S.Media.Core.Video.PixelFormat.Nv12 or S.Media.Core.Video.PixelFormat.P010
            or S.Media.Core.Video.PixelFormat.P016)
            return null;

        return pixelFormat switch
        {
            S.Media.Core.Video.PixelFormat.Yuv422P10Le or S.Media.Core.Video.PixelFormat.Yuv420P10Le
                or S.Media.Core.Video.PixelFormat.Yuv444P10Le or S.Media.Core.Video.PixelFormat.Yuv420P12Le
                => "10/12-bit YUV DRM PRIME → EGL/GL is not implemented for this layout; use NV12/P010/P016 decode or a CPU path.",
            S.Media.Core.Video.PixelFormat.Bgra32 or S.Media.Core.Video.PixelFormat.Rgba32
                or S.Media.Core.Video.PixelFormat.Bgr24 or S.Media.Core.Video.PixelFormat.Rgb24
                or S.Media.Core.Video.PixelFormat.Argb32 or S.Media.Core.Video.PixelFormat.Abgr32
                or S.Media.Core.Video.PixelFormat.Uyvy or S.Media.Core.Video.PixelFormat.Yuyv
                => "Packed / interleaved RGB or YUV DRM PRIME → EGL/GL is not implemented; shipped paths are semi-planar NV12, P010, and P016.",
            S.Media.Core.Video.PixelFormat.I420 or S.Media.Core.Video.PixelFormat.Yv12
                or S.Media.Core.Video.PixelFormat.Nv21 or S.Media.Core.Video.PixelFormat.Yuv422P
                or S.Media.Core.Video.PixelFormat.Yuv444P or S.Media.Core.Video.PixelFormat.Yuva420p
                => "Multi-plane planar YUV DRM PRIME → EGL/GL (other than semi-planar NV12 / P010 / P016) is not implemented; use NV12/P010/P016 decode or a CPU path.",
            S.Media.Core.Video.PixelFormat.Gray8 or S.Media.Core.Video.PixelFormat.Gray16
                => "Single-plane luma DRM PRIME → EGL/GL is not implemented; use NV12/P010/P016 decode or a CPU path.",
            S.Media.Core.Video.PixelFormat.Unknown
                => "Unknown pixel layout — cannot use shipped PRIME EGL dma-buf import (NV12, P010, P016).",
            _ => "EGL dma-buf hardware upload supports DRM PRIME semi-planar NV12, P010, and P016 in this build; use CPU decode or extend LinuxDmabufGlHardwareFormats / Nv12DmabufGpuUploader.",
        };
    }

    /// <summary>Returns true when <paramref name="pixelFormat"/> has a hardware GL upload path via EGL dma-buf import in this version.</summary>
    public static bool IsSupportedForPrimeGlImport(S.Media.Core.Video.PixelFormat pixelFormat) =>
        GetPrimeGlImportBlocker(pixelFormat) is null;
}
