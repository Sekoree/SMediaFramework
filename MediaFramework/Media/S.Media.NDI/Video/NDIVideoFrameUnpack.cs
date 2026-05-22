using System.Buffers;
using System.Buffers.Binary;
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

        if (!TryResolveGeometry(native, pixelFormat, out var width, out var height, out var stride))
            return false;

        var rate = native.FrameRateD > 0
            ? new Rational(native.FrameRateN, native.FrameRateD)
            : new Rational(30, 1);
        var format = new VideoFormat(width, height, pixelFormat, rate);

        try
        {
            frame = native.FourCC switch
            {
                // Alpha-bearing variants keep UYVY color data in the first plane.
                NDIFourCCVideoType.Uyva =>
                    UnpackPacked(native, format, stride, presentationTime),
                // High bit-depth 4:2:2 paths (P216/PA16) are converted to 8-bit UYVY so
                // the existing sinks/renderers can display them without dropping frames.
                NDIFourCCVideoType.P216 or NDIFourCCVideoType.Pa16 =>
                    UnpackP216LikeToUyvy(native, format, stride, presentationTime),
                _ => pixelFormat switch
                {
                    PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Uyvy =>
                        UnpackPacked(native, format, stride, presentationTime),
                    PixelFormat.Nv12 => UnpackNv12(native, format, stride, presentationTime),
                    PixelFormat.I420 => UnpackI420(native, format, stride, presentationTime),
                    PixelFormat.Yv12 => UnpackYv12(native, format, stride, presentationTime),
                    _ => null,
                },
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
            NDIFourCCVideoType.Uyva => PixelFormat.Uyvy,
            NDIFourCCVideoType.P216 => PixelFormat.Uyvy,
            NDIFourCCVideoType.Pa16 => PixelFormat.Uyvy,
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
        PixelFormat.Uyvy or PixelFormat.Yuyv => width * 2,
        PixelFormat.Nv12 or PixelFormat.I420 or PixelFormat.Yv12 => width,
        _ => width,
    };

    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => 4,
        PixelFormat.Uyvy or PixelFormat.Yuyv => 2,
        PixelFormat.Nv12 or PixelFormat.I420 or PixelFormat.Yv12 => 1,
        _ => 0,
    };

    /// <summary>
    /// Derives pixel width/height and per-line byte stride from NDI's sometimes ambiguous
    /// <see cref="NDIVideoFrameV2.Xres"/> / <see cref="NDIVideoFrameV2.LineStrideInBytes"/> pair.
    /// </summary>
    /// <remarks>
    /// Live receivers often report <c>Xres</c> equal to the line stride in bytes (not luma width),
    /// which doubles the interpreted width and looks "extremely zoomed in" in UYVY/BGRA shaders.
    /// The union field <see cref="NDIVideoFrameV2.LineStrideInBytes"/> can also carry the total
    /// <c>PData</c> size for some payloads — treat that as "use default line stride".
    /// </remarks>
    internal static bool TryResolveGeometry(
        in NDIVideoFrameV2 native,
        PixelFormat pixelFormat,
        out int width,
        out int height,
        out int lineStrideBytes)
    {
        width = native.Xres;
        height = native.Yres;
        lineStrideBytes = native.LineStrideInBytes;
        if (width <= 0 || height <= 0)
            return false;

        var bpp = BytesPerPixel(pixelFormat);
        if (bpp <= 0)
            return false;

        if (lineStrideBytes <= 0)
        {
            lineStrideBytes = DefaultLineStride(pixelFormat, width);
            return true;
        }

        if (LooksLikeTotalBufferSize(lineStrideBytes, width, height, bpp))
            lineStrideBytes = DefaultLineStride(pixelFormat, width);

        // When stride-in-bytes was copied into Xres (Xres == LineStrideInBytes), recover luma width.
        if (lineStrideBytes == width && width % bpp == 0)
        {
            width = width / bpp;
            return width >= 16;
        }

        if (lineStrideBytes % bpp != 0)
            return true;

        var widthFromStride = lineStrideBytes / bpp;
        // Tight stride with Xres 2× the active line width (mis-labelled pixel count).
        if (widthFromStride >= 16 && widthFromStride * 2 == width)
            width = widthFromStride;

        return width >= 16;
    }

    private static bool LooksLikeTotalBufferSize(int lineStride, int width, int height, int bpp)
    {
        if (width <= 0 || height <= 0)
            return false;
        long tight = bpp switch
        {
            1 => (long)width * height + (long)PixelFormatInfo.ChromaWidth420(width) * PixelFormatInfo.ChromaHeight420(height) * 2,
            _ => (long)width * height * bpp,
        };
        return lineStride >= tight - bpp && lineStride <= tight + bpp;
    }

    private static VideoFrame UnpackPacked(in NDIVideoFrameV2 native, VideoFormat format, int stride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var visibleStride = VisiblePackedStride(format.PixelFormat, width);
        if (stride < visibleStride)
            throw new InvalidOperationException(
                $"NDI packed stride too small for {format.PixelFormat} {width}x{height}: stride={stride} visible={visibleStride}.");

        // Copy row-by-row when the SDK pads lines (LineStrideInBytes > visible width). A single
        // bulk memcpy would misalign rows and show as shifting abstract colours in YUV previews.
        if (stride == visibleStride)
        {
            var totalBytes = visibleStride * height;
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
                    visibleStride,
                    release: () => ArrayPool<byte>.Shared.Return(buffer));
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        var tightBytes = visibleStride * height;
        var tight = ArrayPool<byte>.Shared.Rent(tightBytes);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                var dst = tight.AsSpan(0, tightBytes);
                for (var row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>(src + (row * stride), visibleStride)
                        .CopyTo(dst.Slice(row * visibleStride, visibleStride));
                }
            }

            return new VideoFrame(
                pts,
                format,
                tight.AsMemory(0, tightBytes),
                visibleStride,
                release: () => ArrayPool<byte>.Shared.Return(tight));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(tight);
            throw;
        }
    }

    private static int VisiblePackedStride(PixelFormat format, int width) => format switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 => width * 4,
        PixelFormat.Uyvy or PixelFormat.Yuyv => width * 2,
        _ => throw new InvalidOperationException($"not a packed pixel format: {format}"),
    };

    private static VideoFrame UnpackNv12(in NDIVideoFrameV2 native, VideoFormat format, int yStride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var uvRows = height / 2;
        var yTight = width * height;
        var uvTight = width * uvRows;

        var yBuf = ArrayPool<byte>.Shared.Rent(yTight);
        var uvBuf = ArrayPool<byte>.Shared.Rent(uvTight);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                CopyPlaneRows(src, yStride, width, height, yBuf.AsSpan(0, yTight));
                CopyPlaneRows(src + (yStride * height), yStride, width, uvRows, uvBuf.AsSpan(0, uvTight));
            }

            var rented = new[] { yBuf, uvBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yTight), uvBuf.AsMemory(0, uvTight)],
                [width, width],
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
        var yTight = width * height;
        var uTight = chromaW * chromaH;
        var vTight = chromaW * chromaH;

        var yBuf = ArrayPool<byte>.Shared.Rent(yTight);
        var uBuf = ArrayPool<byte>.Shared.Rent(uTight);
        var vBuf = ArrayPool<byte>.Shared.Rent(vTight);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                CopyPlaneRows(src, yStride, width, height, yBuf.AsSpan(0, yTight));
                var uBase = src + (yStride * height);
                CopyPlaneRows(uBase, yStride, chromaW, chromaH, uBuf.AsSpan(0, uTight));
                var vBase = uBase + (yStride * chromaH);
                CopyPlaneRows(vBase, chromaW, chromaW, chromaH, vBuf.AsSpan(0, vTight));
            }

            var rented = new[] { yBuf, uBuf, vBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yTight), uBuf.AsMemory(0, uTight), vBuf.AsMemory(0, vTight)],
                [width, chromaW, chromaW],
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
        var yTight = width * height;
        var uTight = chromaW * chromaH;
        var vTight = chromaW * chromaH;

        var yBuf = ArrayPool<byte>.Shared.Rent(yTight);
        var uBuf = ArrayPool<byte>.Shared.Rent(uTight);
        var vBuf = ArrayPool<byte>.Shared.Rent(vTight);
        try
        {
            unsafe
            {
                var src = (byte*)native.PData;
                CopyPlaneRows(src, yStride, width, height, yBuf.AsSpan(0, yTight));
                // YV12 stores chroma as Y + V + U (the inverse of I420's Y + U + V).
                var vBase = src + (yStride * height);
                CopyPlaneRows(vBase, yStride, chromaW, chromaH, vBuf.AsSpan(0, vTight));
                var uBase = vBase + (yStride * chromaH);
                CopyPlaneRows(uBase, chromaW, chromaW, chromaH, uBuf.AsSpan(0, uTight));
            }

            var rented = new[] { yBuf, uBuf, vBuf };
            return new VideoFrame(
                pts,
                format,
                [yBuf.AsMemory(0, yTight), uBuf.AsMemory(0, uTight), vBuf.AsMemory(0, vTight)],
                [width, chromaW, chromaW],
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

    private static unsafe void CopyPlaneRows(byte* src, int srcStride, int visibleBytesPerRow, int rows, Span<byte> dst)
    {
        if (srcStride < visibleBytesPerRow)
            throw new InvalidOperationException(
                $"NDI plane stride too small: stride={srcStride} visible={visibleBytesPerRow} rows={rows}.");
        if (srcStride == visibleBytesPerRow)
        {
            new ReadOnlySpan<byte>(src, visibleBytesPerRow * rows).CopyTo(dst);
            return;
        }

        for (var row = 0; row < rows; row++)
            new ReadOnlySpan<byte>(src + (row * srcStride), visibleBytesPerRow)
                .CopyTo(dst.Slice(row * visibleBytesPerRow, visibleBytesPerRow));
    }

    private static VideoFrame UnpackP216LikeToUyvy(in NDIVideoFrameV2 native, VideoFormat format, int yStride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        if ((width & 1) != 0)
            throw new InvalidOperationException($"P216/PA16 width must be even; got {width}.");
        if (yStride < width * sizeof(ushort))
            throw new InvalidOperationException(
                $"P216/PA16 stride too small for width={width}: stride={yStride} bytes.");

        var yBytes = yStride * height;
        var uvStride = yStride;
        var uvBytes = uvStride * height;
        var totalBytes = yBytes + uvBytes;

        var uyvyStride = width * 2;
        var uyvyBytes = uyvyStride * height;
        var uyvyBuffer = ArrayPool<byte>.Shared.Rent(uyvyBytes);
        try
        {
            unsafe
            {
                var src = new ReadOnlySpan<byte>((void*)native.PData, totalBytes);
                var dst = uyvyBuffer.AsSpan(0, uyvyBytes);
                for (var row = 0; row < height; row++)
                {
                    var yRowBase = row * yStride;
                    var uvRowBase = yBytes + (row * uvStride);
                    var outBase = row * uyvyStride;
                    for (var x = 0; x < width; x += 2)
                    {
                        var y0 = BinaryPrimitives.ReadUInt16LittleEndian(src[(yRowBase + (x * 2))..]);
                        var y1 = BinaryPrimitives.ReadUInt16LittleEndian(src[(yRowBase + ((x + 1) * 2))..]);
                        var u = BinaryPrimitives.ReadUInt16LittleEndian(src[(uvRowBase + (x * 2))..]);
                        var v = BinaryPrimitives.ReadUInt16LittleEndian(src[(uvRowBase + ((x + 1) * 2))..]);

                        dst[outBase++] = ToByte8(u);
                        dst[outBase++] = ToByte8(y0);
                        dst[outBase++] = ToByte8(v);
                        dst[outBase++] = ToByte8(y1);
                    }
                }
            }

            var uyvyFormat = format with { PixelFormat = PixelFormat.Uyvy };
            return new VideoFrame(
                pts,
                uyvyFormat,
                uyvyBuffer.AsMemory(0, uyvyBytes),
                uyvyStride,
                release: () => ArrayPool<byte>.Shared.Return(uyvyBuffer));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(uyvyBuffer);
            throw;
        }
    }

    private static byte ToByte8(ushort value) => (byte)((value + 128) >> 8);
}
