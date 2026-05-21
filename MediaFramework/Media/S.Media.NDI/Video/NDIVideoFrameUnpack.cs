using System.Buffers;
using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Video;

namespace S.Media.NDI.Video;

/// <summary>Copies CPU-accessible <see cref="NDIVideoFrameV2"/> payloads into owned <see cref="VideoFrame"/> buffers.</summary>
internal static class NDIVideoFrameUnpack
{
    public static bool TryUnpack(in NDIVideoFrameV2 native, TimeSpan presentationTime, out VideoFrame? frame)
    {
        frame = null;
        if (native.Xres <= 0 || native.Yres <= 0 || native.PData == nint.Zero)
            return false;

        if (!TryMapFourCc(native.FourCC, out var pixelFormat))
            return false;

        var width = native.Xres;
        var height = native.Yres;
        var rate = native.FrameRateD > 0
            ? new Rational(native.FrameRateN, native.FrameRateD)
            : new Rational(30, 1);
        var format = new VideoFormat(width, height, pixelFormat, rate);
        var stride = native.LineStrideInBytes > 0
            ? native.LineStrideInBytes
            : DefaultLineStride(pixelFormat, width);

        try
        {
            frame = pixelFormat switch
            {
                PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Uyvy =>
                    UnpackPacked(native, format, stride, presentationTime),
                PixelFormat.Nv12 => UnpackNv12(native, format, stride, presentationTime),
                PixelFormat.I420 => UnpackI420(native, format, stride, presentationTime),
                PixelFormat.Yv12 => UnpackYv12(native, format, stride, presentationTime),
                _ => null,
            };
            return frame is not null;
        }
        catch
        {
            frame?.Dispose();
            frame = null;
            return false;
        }
    }

    private static bool TryMapFourCc(NDIFourCCVideoType fourCc, out PixelFormat pixelFormat)
    {
        pixelFormat = fourCc switch
        {
            NDIFourCCVideoType.Bgra => PixelFormat.Bgra32,
            NDIFourCCVideoType.Bgrx => PixelFormat.Bgra32,
            NDIFourCCVideoType.Rgba => PixelFormat.Rgba32,
            NDIFourCCVideoType.Rgbx => PixelFormat.Rgba32,
            NDIFourCCVideoType.Uyvy => PixelFormat.Uyvy,
            NDIFourCCVideoType.Nv12 => PixelFormat.Nv12,
            NDIFourCCVideoType.I420 => PixelFormat.I420,
            NDIFourCCVideoType.Yv12 => PixelFormat.Yv12,
            _ => PixelFormat.Unknown,
        };
        return pixelFormat != PixelFormat.Unknown;
    }

    private static int DefaultLineStride(PixelFormat format, int width) => format switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => width * 4,
        PixelFormat.Uyvy => width * 2,
        PixelFormat.Nv12 or PixelFormat.I420 or PixelFormat.Yv12 => width,
        _ => width,
    };

    private static VideoFrame UnpackPacked(in NDIVideoFrameV2 native, VideoFormat format, int stride, TimeSpan pts)
    {
        var visibleStride = format.PixelFormat switch
        {
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => format.Width * 4,
            PixelFormat.Uyvy => format.Width * 2,
            _ => stride,
        };
        var totalBytes = stride * format.Height;
        var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            unsafe
            {
                new ReadOnlySpan<byte>((void*)native.PData, totalBytes).CopyTo(buffer);
            }

            return new VideoFrame(
                pts,
                format,
                buffer.AsMemory(0, totalBytes),
                stride,
                release: () => ArrayPool<byte>.Shared.Return(buffer));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static VideoFrame UnpackNv12(in NDIVideoFrameV2 native, VideoFormat format, int yStride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var yBytes = yStride * height;
        var uvRows = height / 2;
        var uvBytes = width * uvRows;
        var total = yBytes + uvBytes;

        var yBuf = ArrayPool<byte>.Shared.Rent(yBytes);
        var uvBuf = ArrayPool<byte>.Shared.Rent(uvBytes);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                new ReadOnlySpan<byte>(src, yBytes).CopyTo(yBuf);
                new ReadOnlySpan<byte>(src + yBytes, uvBytes).CopyTo(uvBuf);
            }

            var rented = new[] { yBuf, uvBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yBytes), uvBuf.AsMemory(0, uvBytes)],
                [yStride, width],
                release: () =>
                {
                    foreach (var b in rented)
                        ArrayPool<byte>.Shared.Return(b);
                });
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(yBuf);
            ArrayPool<byte>.Shared.Return(uvBuf);
            throw;
        }
    }

    private static VideoFrame UnpackI420(in NDIVideoFrameV2 native, VideoFormat format, int yStride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var chromaW = PixelFormatInfo.ChromaWidth420(width);
        var chromaH = PixelFormatInfo.ChromaHeight420(height);
        var yBytes = yStride * height;
        var uBytes = chromaW * chromaH;
        var vBytes = chromaW * chromaH;
        var total = yBytes + uBytes + vBytes;

        var yBuf = ArrayPool<byte>.Shared.Rent(yBytes);
        var uBuf = ArrayPool<byte>.Shared.Rent(uBytes);
        var vBuf = ArrayPool<byte>.Shared.Rent(vBytes);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                new ReadOnlySpan<byte>(src, yBytes).CopyTo(yBuf);
                new ReadOnlySpan<byte>(src + yBytes, uBytes).CopyTo(uBuf);
                new ReadOnlySpan<byte>(src + yBytes + uBytes, vBytes).CopyTo(vBuf);
            }

            var rented = new[] { yBuf, uBuf, vBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yBytes), uBuf.AsMemory(0, uBytes), vBuf.AsMemory(0, vBytes)],
                [yStride, chromaW, chromaW],
                release: () =>
                {
                    foreach (var b in rented)
                        ArrayPool<byte>.Shared.Return(b);
                });
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(yBuf);
            ArrayPool<byte>.Shared.Return(uBuf);
            ArrayPool<byte>.Shared.Return(vBuf);
            throw;
        }
    }

    private static VideoFrame UnpackYv12(in NDIVideoFrameV2 native, VideoFormat format, int yStride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var chromaW = PixelFormatInfo.ChromaWidth420(width);
        var chromaH = PixelFormatInfo.ChromaHeight420(height);
        var yBytes = yStride * height;
        var uBytes = chromaW * chromaH;
        var vBytes = chromaW * chromaH;

        var yBuf = ArrayPool<byte>.Shared.Rent(yBytes);
        var uBuf = ArrayPool<byte>.Shared.Rent(uBytes);
        var vBuf = ArrayPool<byte>.Shared.Rent(vBytes);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                new ReadOnlySpan<byte>(src, yBytes).CopyTo(yBuf);
                // YV12 stores chroma as Y + V + U (the inverse of I420's Y + U + V).
                new ReadOnlySpan<byte>(src + yBytes, vBytes).CopyTo(vBuf);
                new ReadOnlySpan<byte>(src + yBytes + vBytes, uBytes).CopyTo(uBuf);
            }

            var rented = new[] { yBuf, uBuf, vBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yBytes), uBuf.AsMemory(0, uBytes), vBuf.AsMemory(0, vBytes)],
                [yStride, chromaW, chromaW],
                release: () =>
                {
                    foreach (var b in rented)
                        ArrayPool<byte>.Shared.Return(b);
                });
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(yBuf);
            ArrayPool<byte>.Shared.Return(uBuf);
            ArrayPool<byte>.Shared.Return(vBuf);
            throw;
        }
    }
}
