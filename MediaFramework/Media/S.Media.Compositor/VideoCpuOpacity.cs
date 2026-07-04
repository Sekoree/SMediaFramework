using System.Runtime.InteropServices;

namespace S.Media.Compositor;

/// <summary>
/// CPU-side fade toward black (and neutral chroma for YUV) for cue/output opacity ramps.
/// Operates on array-backed planes (for example after <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/>).
/// </summary>
public static class VideoCpuOpacity
{
    /// <summary>True when the frame cannot be faded in CPU memory (DMA-BUF / D3D11 shared).</summary>
    public static bool IsHardwareBacked(VideoFrame frame) =>
        frame.DmabufNv12 is not null || frame.DmabufP010 is not null ||
        frame.DmabufP016 is not null || frame.Win32Nv12 is not null;

    /// <summary>True when <see cref="ApplyInPlace"/> handles this pixel format without a scaler.</summary>
    public static bool SupportsInPlace(PixelFormat format) => format switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Bgr24 or PixelFormat.Rgb24 or PixelFormat.Gray8
            or PixelFormat.Argb32 or PixelFormat.Abgr32
            or PixelFormat.I420 or PixelFormat.Yv12 or PixelFormat.Nv12 or PixelFormat.Nv21
            or PixelFormat.Uyvy or PixelFormat.Yuyv
            or PixelFormat.Yuv422P or PixelFormat.Yuv444P
            or PixelFormat.Yuva420p or PixelFormat.Yuva422P or PixelFormat.Yuva444P => true,
        _ => false,
    };

    /// <summary>
    /// Scales frame content toward black by <paramref name="opacity"/> (1 = unchanged, 0 = black / neutral chroma).
    /// Mutates array-backed plane memory in place.
    /// </summary>
    public static void ApplyInPlace(VideoFrame frame, float opacity, VideoColorRange range = VideoColorRange.Limited)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsHardwareBacked(frame))
            throw new NotSupportedException("Hardware-backed frames cannot be faded in CPU memory.");
        if (opacity >= 0.999f)
            return;

        var mul256 = (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 256f);
        if (mul256 >= 256)
            return;

        var blackY = range == VideoColorRange.Full ? (byte)0 : (byte)16;
        const byte neutralChroma = 128;

        switch (frame.Format.PixelFormat)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
            case PixelFormat.Argb32:
            case PixelFormat.Abgr32:
                ApplyPackedRgbOpacity(frame, mul256, bytesPerPixel: 4, hasAlpha: true);
                break;
            case PixelFormat.Bgr24:
            case PixelFormat.Rgb24:
                ApplyPackedRgbOpacity(frame, mul256, bytesPerPixel: 3, hasAlpha: false);
                break;
            case PixelFormat.Gray8:
                ApplyPlanarOpacity(frame, mul256, blackY, neutralChroma);
                break;
            case PixelFormat.Uyvy:
                ApplyUyvyOpacity(frame, mul256, blackY, neutralChroma);
                break;
            case PixelFormat.Yuyv:
                ApplyYuyvOpacity(frame, mul256, blackY, neutralChroma);
                break;
            case PixelFormat.I420:
            case PixelFormat.Yv12:
            case PixelFormat.Nv12:
            case PixelFormat.Nv21:
            case PixelFormat.Yuv422P:
            case PixelFormat.Yuv444P:
            case PixelFormat.Yuva420p:
            case PixelFormat.Yuva422P:
            case PixelFormat.Yuva444P:
                ApplyPlanarOpacity(frame, mul256, blackY, neutralChroma,
                    alphaPlaneIndex: frame.Format.PixelFormat is PixelFormat.Yuva420p or PixelFormat.Yuva422P
                        or PixelFormat.Yuva444P
                        ? 3
                        : -1);
                break;
            default:
                throw new NotSupportedException($"VideoCpuOpacity does not support {frame.Format.PixelFormat} in place.");
        }
    }

    private static void ApplyPackedRgbOpacity(VideoFrame frame, int mul256, int bytesPerPixel, bool hasAlpha)
    {
        if (!TryGetMutablePlane(frame, 0, out var buffer, out var baseOffset, out _))
            throw new InvalidOperationException("Frame plane is not array-backed.");

        var stride = frame.Strides[0];
        var w = frame.Format.Width;
        var h = frame.Format.Height;
        for (var y = 0; y < h; y++)
        {
            var row = baseOffset + y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * bytesPerPixel;
                buffer[i + 0] = LerpByte(buffer[i + 0], 0, mul256);
                buffer[i + 1] = LerpByte(buffer[i + 1], 0, mul256);
                buffer[i + 2] = LerpByte(buffer[i + 2], 0, mul256);
                if (hasAlpha && bytesPerPixel == 4)
                    buffer[i + 3] = LerpByte(buffer[i + 3], 0, mul256);
            }
        }
    }

    private static void ApplyUyvyOpacity(VideoFrame frame, int mul256, byte blackY, byte neutralChroma)
    {
        if (!TryGetMutablePlane(frame, 0, out var buffer, out var baseOffset, out _))
            throw new InvalidOperationException("Frame plane is not array-backed.");

        var stride = frame.Strides[0];
        var w = frame.Format.Width;
        var h = frame.Format.Height;
        for (var y = 0; y < h; y++)
        {
            var row = baseOffset + y * stride;
            for (var x = 0; x < w; x += 2)
            {
                var i = row + x * 2;
                buffer[i + 0] = LerpByte(buffer[i + 0], neutralChroma, mul256);
                buffer[i + 1] = LerpByte(buffer[i + 1], blackY, mul256);
                buffer[i + 2] = LerpByte(buffer[i + 2], neutralChroma, mul256);
                buffer[i + 3] = LerpByte(buffer[i + 3], blackY, mul256);
            }
        }
    }

    private static void ApplyYuyvOpacity(VideoFrame frame, int mul256, byte blackY, byte neutralChroma)
    {
        if (!TryGetMutablePlane(frame, 0, out var buffer, out var baseOffset, out _))
            throw new InvalidOperationException("Frame plane is not array-backed.");

        var stride = frame.Strides[0];
        var w = frame.Format.Width;
        var h = frame.Format.Height;
        for (var y = 0; y < h; y++)
        {
            var row = baseOffset + y * stride;
            for (var x = 0; x < w; x += 2)
            {
                var i = row + x * 2;
                buffer[i + 0] = LerpByte(buffer[i + 0], blackY, mul256);
                buffer[i + 1] = LerpByte(buffer[i + 1], neutralChroma, mul256);
                buffer[i + 2] = LerpByte(buffer[i + 2], blackY, mul256);
                buffer[i + 3] = LerpByte(buffer[i + 3], neutralChroma, mul256);
            }
        }
    }

    private static void ApplyPlanarOpacity(
        VideoFrame frame,
        int mul256,
        byte blackY,
        byte neutralChroma,
        int alphaPlaneIndex = -1)
    {
        var fmt = frame.Format.PixelFormat;
        var w = frame.Format.Width;
        var h = frame.Format.Height;
        for (var p = 0; p < frame.PlaneCount; p++)
        {
            if (!TryGetMutablePlane(frame, p, out var buffer, out var baseOffset, out var planeLen))
                throw new InvalidOperationException($"plane[{p}] is not array-backed.");

            byte target = p switch
            {
                _ when p == alphaPlaneIndex => 0,
                0 => blackY,
                _ => neutralChroma,
            };

            var stride = frame.Strides[p];
            var rowBytes = PixelFormatInfo.PlaneByteWidth(fmt, w, p);
            var planeH = PixelFormatInfo.PlaneHeight(fmt, h, p);
            LerpPlane(buffer, baseOffset, stride, rowBytes, planeH, planeLen, target, mul256);
        }
    }

    private static void LerpPlane(
        byte[] buffer,
        int baseOffset,
        int stride,
        int rowBytes,
        int height,
        int planeLength,
        byte target,
        int mul256)
    {
        for (var y = 0; y < height; y++)
        {
            var row = baseOffset + y * stride;
            var count = Math.Min(rowBytes, planeLength - row);
            if (count <= 0)
                continue;
            for (var x = 0; x < count; x++)
            {
                var i = row + x;
                buffer[i] = LerpByte(buffer[i], target, mul256);
            }
        }
    }

    private static byte LerpByte(byte value, byte target, int mul256) =>
        (byte)((value * mul256 + target * (256 - mul256)) >> 8);

    private static bool TryGetMutablePlane(
        VideoFrame frame,
        int planeIndex,
        out byte[] buffer,
        out int baseOffset,
        out int length)
    {
        buffer = null!;
        baseOffset = 0;
        length = 0;
        if (planeIndex >= frame.PlaneCount)
            return false;
        var plane = frame.Planes[planeIndex];
        length = plane.Length;
        if (!MemoryMarshal.TryGetArray(plane, out var segment) || segment.Array is null)
            return false;
        buffer = segment.Array;
        baseOffset = segment.Offset;
        return true;
    }
}
