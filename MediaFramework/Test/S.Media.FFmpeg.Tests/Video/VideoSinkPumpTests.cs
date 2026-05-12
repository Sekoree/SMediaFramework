using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class VideoSinkPumpTests
{
    public VideoSinkPumpTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void Submit_EnqueuesAndDrainsToInner()
    {
        var inner = new CountingSink([PixelFormat.I420]);
        using var pump = new VideoSinkPump(inner, maxQueuedFrames: 4, disposeInnerOnDispose: true);
        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        pump.Configure(vf);

        var y = new byte[32 * 32];
        var u = new byte[16 * 16];
        var v = new byte[16 * 16];
        for (var i = 0; i < 3; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(i * 40), vf, [y, u, v], [32, 16, 16]);
            pump.Submit(f);
        }

        Thread.Sleep(200);
        Assert.Equal(3, inner.SubmitCount);
        Assert.True(pump.SubmittedFrames >= 3);
    }

    private sealed class CountingSink : IVideoSink
    {
        private readonly PixelFormat[] _acc;
        public CountingSink(PixelFormat[] acc) => _acc = acc;
        public int SubmitCount { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _acc;
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            SubmitCount++;
            Thread.Sleep(5);
            frame.Dispose();
        }
    }
}
