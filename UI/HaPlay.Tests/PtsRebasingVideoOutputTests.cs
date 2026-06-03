using HaPlay.Playback;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class PtsRebasingVideoOutputTests
{
    private static readonly VideoFormat Bgra2X1 = new(2, 1, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Submit_SubtractsOffsetAndTransfersOwnership()
    {
        var releaseCount = 0;
        var pixels = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(82),
            Bgra2X1,
            [pixels],
            [8],
            release: new CountingRelease(() => releaseCount++));
        var inner = new CapturingVideoOutput();
        var output = new PtsRebasingVideoOutput(inner, TimeSpan.FromSeconds(80));

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.NotSame(frame, inner.Frame);
        Assert.NotNull(inner.Frame);
        Assert.Equal(TimeSpan.FromSeconds(2), inner.Frame.PresentationTime);
        Assert.Equal(0, releaseCount);

        inner.Frame.Dispose();
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void Submit_ClampsNegativePtsToZero()
    {
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(79),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8]);
        var inner = new CapturingVideoOutput();
        var output = new PtsRebasingVideoOutput(inner, TimeSpan.FromSeconds(80));

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Equal(TimeSpan.Zero, inner.Frame!.PresentationTime);
        inner.Frame.Dispose();
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
