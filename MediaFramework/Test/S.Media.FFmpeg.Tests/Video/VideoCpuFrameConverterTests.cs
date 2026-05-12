using S.Media.Core.Video;
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
}
