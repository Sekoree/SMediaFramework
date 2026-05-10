using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoFrameTests
{
    private static readonly VideoFormat Bgra1080P = new(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));
    private static readonly VideoFormat I420Hd = new(1280, 720, PixelFormat.I420, new Rational(30000, 1001));

    [Fact]
    public void SinglePlaneCtor_ExposesOnePlane()
    {
        var buffer = new byte[1920 * 1080 * 4];
        using var frame = new VideoFrame(TimeSpan.FromMilliseconds(500), Bgra1080P, buffer, stride: 1920 * 4);

        Assert.Single(frame.Planes);
        Assert.Single(frame.Strides);
        Assert.Equal(1920 * 4, frame.Strides[0]);
        Assert.Equal(buffer.Length, frame.Planes[0].Length);
        Assert.Equal(TimeSpan.FromMilliseconds(500), frame.PresentationTime);
        Assert.Equal(Bgra1080P, frame.Format);
    }

    [Fact]
    public void MultiPlaneCtor_PreservesPlaneOrder()
    {
        var y = new byte[1280 * 720];
        var u = new byte[1280 / 2 * 720 / 2];
        var v = new byte[1280 / 2 * 720 / 2];

        using var frame = new VideoFrame(
            TimeSpan.Zero, I420Hd,
            planes:  [y,    u,       v      ],
            strides: [1280, 1280 / 2, 1280 / 2]);

        Assert.Equal(3, frame.Planes.Length);
        Assert.Equal(y.Length, frame.Planes[0].Length);
        Assert.Equal(u.Length, frame.Planes[1].Length);
        Assert.Equal(v.Length, frame.Planes[2].Length);
        Assert.Equal(1280, frame.Strides[0]);
        Assert.Equal(640, frame.Strides[1]);
        Assert.Equal(640, frame.Strides[2]);
    }

    [Fact]
    public void Ctor_RejectsZeroPlanes()
    {
        Assert.Throws<ArgumentException>(() =>
            new VideoFrame(TimeSpan.Zero, Bgra1080P, [], []));
    }

    [Fact]
    public void Ctor_RejectsMismatchedPlaneAndStrideCounts()
    {
        Assert.Throws<ArgumentException>(() =>
            new VideoFrame(TimeSpan.Zero, I420Hd, [new byte[1], new byte[1]], [1]));
    }

    [Fact]
    public void Ctor_RejectsNonPositiveStride()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VideoFrame(TimeSpan.Zero, Bgra1080P, new byte[1], stride: 0));
    }

    [Fact]
    public void Dispose_InvokesReleaseExactlyOnce()
    {
        var calls = 0;
        var frame = new VideoFrame(TimeSpan.Zero, Bgra1080P, new byte[1], 1, release: () => Interlocked.Increment(ref calls));

        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_WithoutReleaseAction_IsNoop()
    {
        var frame = new VideoFrame(TimeSpan.Zero, Bgra1080P, new byte[1], 1);
        frame.Dispose();
        frame.Dispose();
        // no throw
    }
}
