using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.NDI.Video;

/// <summary>Copies CPU-accessible <see cref="NDIVideoFrameV2"/> payloads into owned <see cref="VideoFrame"/> buffers.</summary>
internal static class NDIVideoFrameUnpack
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.Video.NDIVideoFrameUnpack");
    private static int _geometryHeuristicLogCount;

    /// <summary>
    /// OBS / NDI HX PGM is computer-range (full) BT.709 UYVY. Limited-range GL or swscale flags crush
    /// it to black; full-range metadata matches what worked in the field (passthrough may look gray on
    /// some GPUs — use default BGRA conversion).
    /// </summary>
    private static readonly VideoFrameMetadata NdiSdrFullRangeBt709 = new(
        ColorTransferHint: VideoTransferHint.Sdr,
        ColorSpace: VideoColorSpace.Bt709,
        ColorRange: VideoColorRange.Full);

    /// <summary>Quick sanity metric for logs — average luma (Y or BGRA G) after unpack.</summary>
    internal static double SampleAveragePackedLuma(VideoFrame frame)
    {
        try
        {
            if (frame.PlaneCount == 0 || frame.Planes[0].Length == 0)
                return 0;
            var span = frame.Planes[0].Span;
            return frame.Format.PixelFormat switch
            {
                PixelFormat.Uyvy or PixelFormat.Yuyv => SampleUyvyLuma(span, frame.Format.PixelFormat == PixelFormat.Uyvy),
                PixelFormat.Bgra32 => SampleBgraLuma(span),
                PixelFormat.Rgba32 => SampleRgbaLuma(span),
                _ => 0,
            };
        }
        catch
        {
            return -1;
        }
    }

    private static double SampleBgraLuma(ReadOnlySpan<byte> bgra)
    {
        long sum = 0;
        var n = 0;
        var step = Math.Max(16, (bgra.Length / 4) / 2048);
        for (var i = 0; i < bgra.Length - 3; i += step * 4)
        {
            sum += bgra[i + 2];
            n++;
        }

        return n == 0 ? 0 : (double)sum / n;
    }

    private static double SampleRgbaLuma(ReadOnlySpan<byte> rgba)
    {
        long sum = 0;
        var n = 0;
        var step = Math.Max(16, (rgba.Length / 4) / 2048);
        for (var i = 0; i < rgba.Length - 3; i += step * 4)
        {
            sum += rgba[i + 1];
            n++;
        }

        return n == 0 ? 0 : (double)sum / n;
    }

    private static double SampleUyvyLuma(ReadOnlySpan<byte> packed, bool isUyvy)
    {
        long sum = 0;
        var n = 0;
        var step = Math.Max(1, (packed.Length / 4) / 4096);
        for (var i = 0; i < packed.Length - 3; i += step * 4)
        {
            if (isUyvy)
            {
                sum += packed[i + 1] + packed[i + 3];
                n += 2;
            }
            else
            {
                sum += packed[i] + packed[i + 2];
                n += 2;
            }
        }

        return n == 0 ? 0 : (double)sum / n;
    }

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
                // the existing outputs/renderers can display them without dropping frames.
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
        {
            LogGeometryHeuristic(
                native,
                pixelFormat,
                "LineStrideInBytes looks like total PData size — using default line stride");
            lineStrideBytes = DefaultLineStride(pixelFormat, width);
            return true;
        }

        // When stride-in-bytes was copied into Xres (Xres == LineStrideInBytes), recover luma width.
        // Require recovered width >= height so 2560×1440 with a bogus stride=2560 is not halved to 1280.
        if (lineStrideBytes == width && width % bpp == 0)
        {
            var recovered = width / bpp;
            if (recovered != width
                && recovered >= height
                && DefaultLineStride(pixelFormat, recovered) == lineStrideBytes)
            {
                LogGeometryHeuristic(
                    native,
                    pixelFormat,
                    "Xres equals LineStrideInBytes — treating Xres as bytes-per-line, recovering pixel width");
                width = recovered;
            }
        }

        if (lineStrideBytes % bpp != 0)
            return true;

        var widthFromStride = lineStrideBytes / bpp;
        // Tight stride with Xres 2× the active line width (mis-labelled pixel count).
        if (widthFromStride > 0 && widthFromStride * 2 == width)
        {
            LogGeometryHeuristic(
                native,
                pixelFormat,
                "Xres is 2× line stride width — using stride-derived pixel width");
            width = widthFromStride;
        }

        return width > 0;
    }

    private static void LogGeometryHeuristic(in NDIVideoFrameV2 native, PixelFormat pixelFormat, string reason)
    {
        if (Interlocked.Increment(ref _geometryHeuristicLogCount) > 8)
            return;
        if (!Trace.IsEnabled(LogLevel.Debug))
            return;
        Trace.LogDebug(
            "NDIVideoFrameUnpack geometry: {Reason} fourCC={FourCc} pixelFmt={PixelFmt} xres={Xres} yres={Yres} lineStride={Stride}",
            reason, native.FourCC, pixelFormat, native.Xres, native.Yres, native.LineStrideInBytes);
    }

    private static bool LooksLikeTotalBufferSize(int lineStride, int width, int height, int bpp)
    {
        if (width <= 0 || height <= 0 || bpp <= 0)
            return false;

        long tight = bpp switch
        {
            1 => (long)width * height + (long)PixelFormatInfo.ChromaWidth420(width) * PixelFormatInfo.ChromaHeight420(height) * 2,
            _ => (long)width * height * bpp,
        };
        if (lineStride < tight - bpp || lineStride > tight + bpp)
            return false;

        // Tight-packed full-frame byte counts can equal a valid per-line stride on tiny rasters
        // (e.g. 4×2 UYVY: 16 bytes is both one tight frame and a padded line stride). Only treat the
        // field as "total PData size" when it is far larger than a plausible bytes-per-line value.
        var minLineStride = (long)width * bpp;
        return lineStride > minLineStride * 2;
    }

    private static VideoFrame UnpackPacked(in NDIVideoFrameV2 native, VideoFormat format, int stride, TimeSpan pts)
    {
        var width = format.Width;
        var height = format.Height;
        var visibleStride = VisiblePackedStride(format.PixelFormat, width);
        if (stride < visibleStride)
            throw new InvalidOperationException(
                $"NDI packed stride too small for {format.PixelFormat} {width}x{height}: stride={stride} visible={visibleStride}.");

        // Always copy row-by-row using SDK line stride — PData is not guaranteed tightly packed even
        // when LineStrideInBytes equals the visible bytes-per-line.
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
                release: () => ArrayPool<byte>.Shared.Return(tight),
                metadata: NdiSdrFullRangeBt709);
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
                },
                metadata: NdiSdrFullRangeBt709);
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
        var chromaStride = Math.Max(chromaW, yStride / 2);
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
                CopyPlaneRows(uBase, chromaStride, chromaW, chromaH, uBuf.AsSpan(0, uTight));
                var vBase = uBase + (chromaStride * chromaH);
                CopyPlaneRows(vBase, chromaStride, chromaW, chromaH, vBuf.AsSpan(0, vTight));
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
                },
                metadata: NdiSdrFullRangeBt709);
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
        var chromaStride = Math.Max(chromaW, yStride / 2);
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
                CopyPlaneRows(vBase, chromaStride, chromaW, chromaH, vBuf.AsSpan(0, vTight));
                var uBase = vBase + (chromaStride * chromaH);
                CopyPlaneRows(uBase, chromaStride, chromaW, chromaH, uBuf.AsSpan(0, uTight));
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
                },
                metadata: NdiSdrFullRangeBt709);
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
                release: () => ArrayPool<byte>.Shared.Return(uyvyBuffer),
                metadata: NdiSdrFullRangeBt709);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(uyvyBuffer);
            throw;
        }
    }

    private static byte ToByte8(ushort value) => (byte)((value + 128) >> 8);
}
