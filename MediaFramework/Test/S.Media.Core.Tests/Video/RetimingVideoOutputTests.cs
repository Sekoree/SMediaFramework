using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class RetimingVideoOutputTests
{
    private static readonly VideoFormat Bgra2X1 = new(2, 1, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Submit_AddsOffsetAndTransfersOwnership()
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
        // Rebase a clip starting 80 s in to a zero-based timeline: add −80 s.
        var output = new RetimingVideoOutput(inner, TimeSpan.FromSeconds(-80));

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
    public void Submit_PositiveOffsetShiftsLater()
    {
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(2),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8]);
        var inner = new CapturingVideoOutput();
        var output = new RetimingVideoOutput(inner, TimeSpan.FromSeconds(5));

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Equal(TimeSpan.FromSeconds(7), inner.Frame!.PresentationTime);
        inner.Frame.Dispose();
    }

    [Fact]
    public void Submit_ClampsNegativePtsToZeroByDefault()
    {
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(1),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8]);
        var inner = new CapturingVideoOutput();
        var output = new RetimingVideoOutput(inner, TimeSpan.FromSeconds(-80));

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Equal(TimeSpan.Zero, inner.Frame!.PresentationTime);
        inner.Frame.Dispose();
    }

    [Fact]
    public void Submit_NoClampWhenDisabled_AllowsNegativePts()
    {
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(1),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8]);
        var inner = new CapturingVideoOutput();
        var output = new RetimingVideoOutput(inner, TimeSpan.FromSeconds(-5), clampNegativeToZero: false);

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Equal(TimeSpan.FromSeconds(-4), inner.Frame!.PresentationTime);
        inner.Frame.Dispose();
    }

    [Fact]
    public void Submit_ZeroOffsetForwardsSameFrame()
    {
        var frame = new VideoFrame(
            TimeSpan.FromSeconds(3),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8]);
        var inner = new CapturingVideoOutput();
        var output = new RetimingVideoOutput(inner, TimeSpan.Zero);

        output.Configure(Bgra2X1);
        output.Submit(frame);

        Assert.Same(frame, inner.Frame);
        inner.Frame!.Dispose();
    }

    [Fact]
    public void QueueControl_ForwardsToInnerOutput()
    {
        var inner = new QueueControlledVideoOutput();
        var output = new RetimingVideoOutput(inner, TimeSpan.FromSeconds(-1));

        output.AbandonQueuedFrames();

        Assert.Equal(1, inner.AbandonCalls);
        Assert.True(output.WaitForIdle(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, inner.WaitCalls);
    }

    private sealed class CapturingVideoOutput : IVideoOutput
    {
        public VideoFrame? Frame { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame) => Frame = frame;
    }

    private sealed class QueueControlledVideoOutput : IVideoOutput, IVideoOutputQueueControl
    {
        public int AbandonCalls { get; private set; }
        public int WaitCalls { get; private set; }

        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame) => frame.Dispose();

        public void AbandonQueuedFrames() => AbandonCalls++;

        public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            return true;
        }
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
