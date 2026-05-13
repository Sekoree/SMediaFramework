using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoSinkFanoutFormatsTests
{
    [Fact]
    public void PickBranchPixelFormat_prefers_Uyvy_before_Bgra_for_422_10bit_when_convertible()
    {
        FFmpegRuntime.EnsureInitialized();
        var negotiated = new VideoFormat(128, 64, PixelFormat.Yuv422P10Le, new Rational(30, 1));
        PixelFormat[] ndiLike = [PixelFormat.Uyvy, PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420];
        var p = VideoSinkFanoutFormats.PickBranchPixelFormat(negotiated, ndiLike);
        Assert.Equal(PixelFormat.Uyvy, p);
    }

    [Fact]
    public void PickBranchPixelFormat_falls_back_when_Uyvy_not_supported()
    {
        FFmpegRuntime.EnsureInitialized();
        var negotiated = new VideoFormat(128, 64, PixelFormat.Yuv422P10Le, new Rational(30, 1));
        PixelFormat[] noUyvy = [PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420];
        var p = VideoSinkFanoutFormats.PickBranchPixelFormat(negotiated, noUyvy);
        Assert.Equal(PixelFormat.Bgra32, p);
    }

    [Fact]
    public void PickBranchPixelFormat_keeps_negotiated_nv12_when_sink_accepts_nv12_even_if_uyvy_convertible()
    {
        FFmpegRuntime.EnsureInitialized();
        var negotiated = new VideoFormat(1280, 720, PixelFormat.Nv12, new Rational(24, 1));
        PixelFormat[] ndiLike = [PixelFormat.Uyvy, PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420];
        var p = VideoSinkFanoutFormats.PickBranchPixelFormat(negotiated, ndiLike);
        Assert.Equal(PixelFormat.Nv12, p);
    }
}
