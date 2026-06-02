using S.Media.Core.Video;
using S.Media.Effects;
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
    public void TryCreateNv12CpuFanOutViews_ReleasesOnceAfterAllViewsDisposed()
    {
        var vf = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        var calls = 0;
        var y = new byte[64 * 64];
        var uv = new byte[64 * 32];
        var source = new VideoFrame(TimeSpan.Zero, vf, [y, uv], [64, 64], release: DisposableRelease.Wrap(() => Interlocked.Increment(ref calls)));

        Assert.True(VideoFrame.TryCreateNv12CpuFanOutViews(source, 3, default, out var views));
        source.Dispose();

        foreach (var v in views)
            v.Dispose();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void TryCreateNv12CpuFanOutViews_ReturnsFalseWithoutRelease()
    {
        var vf = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        var y = new byte[64 * 64];
        var uv = new byte[64 * 32];
        using var source = new VideoFrame(TimeSpan.Zero, vf, [y, uv], [64, 64]);

        Assert.False(VideoFrame.TryCreateNv12CpuFanOutViews(source, 2, default, out var views));
        Assert.Null(views);
    }

    [Fact]
    public void ValidateCpuGeometry_RejectsShortPackedPlane()
    {
        using var frame = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1)),
            new byte[4 * 4 * 4 - 1],
            stride: 4 * 4);

        Assert.Throws<InvalidOperationException>(() => frame.ValidateCpuGeometry());
    }

    [Fact]
    public void ValidateCpuGeometry_RejectsMissingNv12Plane()
    {
        using var frame = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(4, 4, PixelFormat.Nv12, new Rational(30, 1)),
            planes: [new byte[4 * 4]],
            strides: [4]);

        Assert.Throws<InvalidOperationException>(() => frame.ValidateCpuGeometry());
    }

    [Fact]
    public void ValidateCpuGeometry_AcceptsValidI420()
    {
        using var frame = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(4, 4, PixelFormat.I420, new Rational(30, 1)),
            planes: [new byte[16], new byte[4], new byte[4]],
            strides: [4, 2, 2]);

        frame.ValidateCpuGeometry();
    }

    [Fact]
    public void Dispose_InvokesReleaseExactlyOnce()
    {
        var calls = 0;
        var frame = new VideoFrame(TimeSpan.Zero, Bgra1080P, new byte[1], 1, release: DisposableRelease.Wrap(() => Interlocked.Increment(ref calls)));

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
