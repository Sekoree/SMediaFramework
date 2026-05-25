using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class UyvyToBgraConversionTests
{
    public UyvyToBgraConversionTests()
    {
        FFmpegRuntime.EnsureInitialized();
    }

    [Fact]
    public void Convert_Uyvy_full_range_mid_grey_produces_visible_bgra()
    {
        const int w = 64;
        const int h = 32;
        var stride = w * 2;
        var bytes = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x += 2)
            {
                var i = row + x * 2;
                bytes[i] = 128;
                bytes[i + 1] = 180;
                bytes[i + 2] = 128;
                bytes[i + 3] = 180;
            }
        }

        var fmt = new VideoFormat(w, h, PixelFormat.Uyvy, new Rational(60, 1));
        var meta = new VideoFrameMetadata(
            ColorTransferHint: VideoTransferHint.Sdr,
            ColorSpace: VideoColorSpace.Bt709,
            ColorRange: VideoColorRange.Full);
        using var src = new VideoFrame(TimeSpan.Zero, fmt, bytes, stride, metadata: meta);

        using var conv = new VideoCpuFrameConverter();
        conv.Configure(PixelFormat.Uyvy, PixelFormat.Bgra32, w, h);
        using var dst = conv.Convert(src, VideoTransferHint.Sdr);

        var avgG = AverageBgraGreen(dst);
        Assert.True(avgG > 80,
            $"Expected visible mid-tones after UYVY→BGRA (avg green={avgG:F1}); check swscale / unpack range.");
    }

    private static double AverageBgraGreen(VideoFrame frame)
    {
        var span = frame.Planes[0].Span;
        long sum = 0;
        var n = 0;
        for (var i = 0; i < span.Length - 3; i += 16)
        {
            sum += span[i + 2];
            n++;
        }

        return n == 0 ? 0 : (double)sum / n;
    }
}
