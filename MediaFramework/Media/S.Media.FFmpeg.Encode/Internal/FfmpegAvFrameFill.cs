using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg.Encode.Internal;

internal static unsafe class FfmpegAvFrameFill
{
    internal static void CopyVideoFrame(VideoFrame frame, AVFrame* dst, PixelFormat dstPixel)
    {
        if (frame.DmabufNv12 is not null || frame.DmabufP010 is not null || frame.DmabufP016 is not null || frame.Win32Nv12 is not null)
            throw new NotSupportedException("Hardware-backed frames must be converted to CPU memory before encoding.");

        var avDst = FfmpegVideoPixelMaps.ToAvPixelFormat(dstPixel)
            ?? throw new NotSupportedException($"no FFmpeg mapping for encode pixel {dstPixel}");

        dst->format = (int)avDst;
        dst->width = frame.Format.Width;
        dst->height = frame.Format.Height;

        var ret = av_frame_get_buffer(dst, 32);
        FFmpegException.ThrowIfError(ret, nameof(av_frame_get_buffer));

        var w = frame.Format.Width;
        var h = frame.Format.Height;

        if (frame.Format.PixelFormat == dstPixel)
        {
            CopyMatchingLayout(frame, dst, dstPixel, w, h);
            return;
        }

        throw new InvalidOperationException(
            $"encoder expected pre-converted frames ({dstPixel}); got {frame.Format.PixelFormat}");
    }

    private static void CopyMatchingLayout(VideoFrame frame, AVFrame* dst, PixelFormat fmt, int w, int h)
    {
        if (fmt == PixelFormat.Nv12)
        {
            CopyPlane(frame.Planes[0].Span, frame.Strides[0], dst->data[0], dst->linesize[0], w, h);
            CopyPlane(frame.Planes[1].Span, frame.Strides[1], dst->data[1], dst->linesize[1], w, h / 2);
            return;
        }

        var planes = PixelFormatInfo.PlaneCount(fmt);
        for (var i = 0; i < planes; i++)
        {
            var ph = PixelFormatInfo.PlaneHeight(fmt, h, i);
            var pw = PixelFormatInfo.PlaneByteWidth(fmt, w, i);
            CopyPlane(frame.Planes[i].Span, frame.Strides[i], dst->data[(uint)i], dst->linesize[(uint)i], pw, ph);
        }
    }

    private static void CopyPlane(ReadOnlySpan<byte> src, int srcStride, byte* dst, int dstStride, int widthBytes, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = src.Slice(y * srcStride, Math.Min(widthBytes, srcStride));
            row.CopyTo(new Span<byte>(dst + y * dstStride, widthBytes));
        }
    }
}
