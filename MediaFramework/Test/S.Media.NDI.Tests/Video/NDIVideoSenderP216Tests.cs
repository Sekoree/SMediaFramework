using S.Media.Core.Video;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests.Video;

public sealed class NDIVideoSenderP216Tests
{
    [Fact]
    public void StagingBytes_P216_IsFourBytesPerPixelTimesArea()
    {
        var fmt = new VideoFormat(64, 64, PixelFormat.P216, new Rational(30, 1));
        var bytes = InvokeStagingBytes(fmt);
        Assert.Equal(64 * 64 * 4, bytes);
    }

    [Fact]
    public void StagingBytes_Pa16_IsSixBytesPerPixelTimesArea()
    {
        var fmt = new VideoFormat(64, 64, PixelFormat.Pa16, new Rational(30, 1));
        var bytes = InvokeStagingBytes(fmt);
        Assert.Equal(64 * 64 * 6, bytes);
    }

    [Fact]
    public void LineStride_P216_IsWidthTimesTwoBytes()
    {
        var stride = InvokeLineStride(new VideoFormat(1920, 1080, PixelFormat.P216, new Rational(30, 1)));
        Assert.Equal(1920 * 2, stride);
    }

    private static int InvokeStagingBytes(VideoFormat fmt) =>
        (int)typeof(NDIVideoSender).GetMethod("StagingBytes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [fmt])!;

    private static int InvokeLineStride(VideoFormat fmt) =>
        (int)typeof(NDIVideoSender).GetMethod("LineStrideForFormat",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [fmt])!;
}
