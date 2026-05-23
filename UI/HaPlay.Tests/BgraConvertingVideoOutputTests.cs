using HaPlay.Playback;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class BgraConvertingVideoOutputTests
{
    private static readonly VideoFormat Bgra2X1 = new(2, 1, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Submit_TransparentBgra_PremultipliesBeforeForwarding()
    {
        var releaseCount = 0;
        var pixels = new byte[]
        {
            50, 100, 200, 128,
            20, 40, 60, 255,
        };
        var frame = new VideoFrame(
            TimeSpan.Zero,
            Bgra2X1,
            [pixels],
            [8],
            release: new CountingRelease(() => releaseCount++));
        var inner = new CapturingVideoOutput();
        var output = new BgraConvertingVideoOutput(inner);

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Equal(1, releaseCount);
        Assert.NotSame(frame, inner.Frame);
        Assert.NotNull(inner.Frame);
        var span = inner.Frame.Planes[0].Span;
        Assert.Equal((byte)25, span[0]);
        Assert.Equal((byte)50, span[1]);
        Assert.Equal((byte)100, span[2]);
        Assert.Equal((byte)128, span[3]);
        Assert.Equal((byte)20, span[4]);
        Assert.Equal((byte)40, span[5]);
        Assert.Equal((byte)60, span[6]);
        Assert.Equal((byte)255, span[7]);

        inner.Frame.Dispose();
    }

    [Fact]
    public void Submit_OpaqueBgra_ForwardsOriginalFrame()
    {
        var releaseCount = 0;
        var pixels = new byte[] { 50, 100, 200, 255, 20, 40, 60, 255 };
        var frame = new VideoFrame(
            TimeSpan.Zero,
            Bgra2X1,
            [pixels],
            [8],
            release: new CountingRelease(() => releaseCount++));
        var inner = new CapturingVideoOutput();
        var output = new BgraConvertingVideoOutput(inner);

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Same(frame, inner.Frame);
        Assert.Equal(0, releaseCount);

        inner.Frame!.Dispose();
        Assert.Equal(1, releaseCount);
    }

    private sealed class CapturingVideoOutput : IVideoOutput
    {
        public VideoFrame? Frame { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame) => Frame = frame;
    }

    private sealed class CountingRelease(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
