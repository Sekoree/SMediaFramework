
namespace S.Media.FFmpeg.Common;

/// <summary>Maps <see cref="PixelFormat"/> to libav <see cref="AVPixelFormat"/> for swscale / codec paths.</summary>
internal static class FfmpegVideoPixelMaps
{
    /// <summary>Returns <c>null</c> when there is no stable libav mapping for this build.</summary>
    internal static AVPixelFormat? ToAvPixelFormat(PixelFormat fmt) => fmt switch
    {
        PixelFormat.I420 => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Nv12 => AVPixelFormat.AV_PIX_FMT_NV12,
        PixelFormat.Nv21 => AVPixelFormat.AV_PIX_FMT_NV21,
        PixelFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
        PixelFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
        PixelFormat.Rgba16 => AVPixelFormat.AV_PIX_FMT_RGBA64LE,
        PixelFormat.Rgba16F => AVPixelFormat.AV_PIX_FMT_RGBAF16LE,
        PixelFormat.Bgr24 => AVPixelFormat.AV_PIX_FMT_BGR24,
        PixelFormat.Rgb24 => AVPixelFormat.AV_PIX_FMT_RGB24,
        PixelFormat.Uyvy => AVPixelFormat.AV_PIX_FMT_UYVY422,
        PixelFormat.Yuyv => AVPixelFormat.AV_PIX_FMT_YUYV422,
        PixelFormat.Yv12 => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Yuv422P => AVPixelFormat.AV_PIX_FMT_YUV422P,
        PixelFormat.Yuv444P => AVPixelFormat.AV_PIX_FMT_YUV444P,
        PixelFormat.P010 => AVPixelFormat.AV_PIX_FMT_P010LE,
        PixelFormat.P016 => AVPixelFormat.AV_PIX_FMT_P016LE,
        PixelFormat.P216 => AVPixelFormat.AV_PIX_FMT_P216LE,
        PixelFormat.Pa16 => AVPixelFormat.AV_PIX_FMT_YUVA422P16LE,
        PixelFormat.Yuv422P10Le => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        PixelFormat.Yuv420P10Le => AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
        PixelFormat.Yuv420P12Le => AVPixelFormat.AV_PIX_FMT_YUV420P12LE,
        PixelFormat.Yuv444P10Le => AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
        PixelFormat.Gray8 => AVPixelFormat.AV_PIX_FMT_GRAY8,
        PixelFormat.Gray16 => AVPixelFormat.AV_PIX_FMT_GRAY16LE,
        PixelFormat.Yuva420p => AVPixelFormat.AV_PIX_FMT_YUVA420P,
        PixelFormat.Yuva422P => AVPixelFormat.AV_PIX_FMT_YUVA422P,
        PixelFormat.Yuva444P => AVPixelFormat.AV_PIX_FMT_YUVA444P,
        PixelFormat.Yuva420P10Le => AVPixelFormat.AV_PIX_FMT_YUVA420P10LE,
        PixelFormat.Yuva422P10Le => AVPixelFormat.AV_PIX_FMT_YUVA422P10LE,
        PixelFormat.Yuva444P10Le => AVPixelFormat.AV_PIX_FMT_YUVA444P10LE,
        PixelFormat.Yuva422P12Le => AVPixelFormat.AV_PIX_FMT_YUVA422P12LE,
        PixelFormat.Yuva444P12Le => AVPixelFormat.AV_PIX_FMT_YUVA444P12LE,
        PixelFormat.Yuva420P16Le => AVPixelFormat.AV_PIX_FMT_YUVA420P16LE,
        PixelFormat.Yuva422P16Le => AVPixelFormat.AV_PIX_FMT_YUVA422P16LE,
        PixelFormat.Yuva444P16Le => AVPixelFormat.AV_PIX_FMT_YUVA444P16LE,
        PixelFormat.Yuv422P12Le => AVPixelFormat.AV_PIX_FMT_YUV422P12LE,
        PixelFormat.Yuv444P12Le => AVPixelFormat.AV_PIX_FMT_YUV444P12LE,
        PixelFormat.Argb32 => AVPixelFormat.AV_PIX_FMT_ARGB,
        PixelFormat.Abgr32 => AVPixelFormat.AV_PIX_FMT_ABGR,
        _ => null,
    };
}
