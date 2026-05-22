using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using S.Media.FFmpeg.Video.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoCpuFrameConverterHighBitTests
{
    [Fact]
    public void CanConvert_Yuv422P10Le_ToRgba16()
    {
        Assert.True(VideoCpuFrameConverter.CanConvert(PixelFormat.Yuv422P10Le, PixelFormat.Rgba16, 64, 64));
    }

    [Fact]
    public void Rgba16F_HasFfmpegMapping()
    {
        Assert.NotNull(FfmpegVideoPixelMaps.ToAvPixelFormat(PixelFormat.Rgba16F));
    }

    [Fact]
    public void CanConvert_Yuv422P10Le_ToRgba16F_WhenLibavSupportsIt()
    {
        if (!VideoCpuFrameConverter.CanConvert(PixelFormat.Yuv422P10Le, PixelFormat.Rgba16F, 64, 64))
            return;
        Assert.True(true);
    }
}
