using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class VideoCpuFrameConverterTests
{
    public VideoCpuFrameConverterTests()
    {
        FFmpegRuntime.EnsureInitialized();
    }

    [Fact]
    public void CanConvert_Yuv422P10Le_ToNv12_1080p()
    {
        Assert.True(VideoCpuFrameConverter.CanConvert(PixelFormat.Yuv422P10Le, PixelFormat.Nv12, 1920, 1080));
    }

    [Fact]
    public void CanConvert_Yuv422P10Le_ToBgra32_1080p()
    {
        Assert.True(VideoCpuFrameConverter.CanConvert(PixelFormat.Yuv422P10Le, PixelFormat.Bgra32, 1920, 1080));
    }

    [Fact]
    public void CanConvert_Yuv422P10Le_ToRgba32_1080p()
    {
        Assert.True(VideoCpuFrameConverter.CanConvert(PixelFormat.Yuv422P10Le, PixelFormat.Rgba32, 1920, 1080));
    }

    [Fact]
    public void CanConvert_I420_to_self()
    {
        Assert.True(VideoCpuFrameConverter.CanConvert(PixelFormat.I420, PixelFormat.I420, 640, 480));
    }

    /// <summary>
    /// Pass-through libav frames use <see cref="UnmanagedMemoryManager{T}"/> — conversion must not require managed arrays.
    /// </summary>
    [Fact]
    public unsafe void Convert_Yuv422P10Le_unmanaged_planes_to_Uyvy_succeeds()
    {
        const int w = 64;
        const int h = 32;
        var yStride = w * 2;
        var uvStride = w;
        var yBytes = yStride * h;
        var uBytes = uvStride * h;
        var vBytes = uvStride * h;
        var total = yBytes + uBytes + vBytes;
        var pool = (byte*)NativeMemory.AlignedAlloc((nuint)total, 64);
        NativeMemory.Clear(pool, (nuint)total);
        var mmY = new UnmanagedMemoryManager<byte>(pool, yBytes);
        var mmU = new UnmanagedMemoryManager<byte>(pool + yBytes, uBytes);
        var mmV = new UnmanagedMemoryManager<byte>(pool + yBytes + uBytes, vBytes);
        var fmt = new VideoFormat(w, h, PixelFormat.Yuv422P10Le, new Rational(60, 1));
        using var src = new VideoFrame(TimeSpan.Zero, fmt,
            [mmY.Memory, mmU.Memory, mmV.Memory],
            [yStride, uvStride, uvStride],
            release: () => NativeMemory.AlignedFree(pool));

        using var conv = new VideoCpuFrameConverter();
        conv.Configure(PixelFormat.Yuv422P10Le, PixelFormat.Uyvy, w, h);
        using var dst = conv.Convert(src, default);
        Assert.Equal(PixelFormat.Uyvy, dst.Format.PixelFormat);
        Assert.Equal(w, dst.Format.Width);
        Assert.Equal(h, dst.Format.Height);
    }
}
