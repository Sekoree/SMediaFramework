using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class StaticFrameSourceTests
{
    private static readonly VideoFormat Bgra32_64x64 = new(64, 64, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void EmitsFramesForever_WithAdvancingPts()
    {
        var pixels = new byte[64 * 64 * 4];
        using var src = new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels)],
            [64 * 4]);

        TimeSpan? firstPts = null;
        TimeSpan? lastPts = null;
        for (var i = 0; i < 10; i++)
        {
            Assert.True(src.TryReadNextFrame(out var frame));
            if (firstPts is null) firstPts = frame.PresentationTime;
            lastPts = frame.PresentationTime;
            Assert.Equal(Bgra32_64x64, frame.Format);
            Assert.Single(frame.Planes);
            frame.Dispose();
        }

        Assert.False(src.IsExhausted);
        Assert.NotNull(firstPts);
        Assert.NotNull(lastPts);
        Assert.True(lastPts > firstPts, "PTS must advance monotonically across reads.");
        // 10 frames at 30 FPS ≈ 9/30 second span (frames 0..9).
        Assert.True(lastPts.Value > TimeSpan.FromMilliseconds(250) && lastPts.Value < TimeSpan.FromMilliseconds(350),
            $"unexpected lastPts {lastPts}");
    }

    [Fact]
    public void Dispose_FiresReleaseExactlyOnce()
    {
        var pixels = new byte[64 * 64 * 4];
        var calls = 0;
        var src = new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels)],
            [64 * 4],
            releaseBuffersOnDispose: () => calls++);

        src.Dispose();
        src.Dispose();
        Assert.Equal(1, calls);
        Assert.True(src.IsExhausted);
        Assert.False(src.TryReadNextFrame(out _));
    }

    [Fact]
    public void Dispose_WithReleaseHook_DefersReleaseUntilInFlightFramesDisposed()
    {
        // P0-6: emitted frames alias the source-owned (pooled/native) backing. Disposing the source
        // must NOT fire releaseBuffersOnDispose while a frame is still in flight — otherwise the buffer
        // is returned/freed under a frame still reading it. The hook fires once, after the source AND
        // every emitted frame are disposed.
        var pixels = new byte[64 * 64 * 4];
        var released = 0;
        var src = new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels)],
            [64 * 4],
            releaseBuffersOnDispose: () => released++);

        Assert.True(src.TryReadNextFrame(out var f1));
        Assert.True(src.TryReadNextFrame(out var f2));

        src.Dispose();
        Assert.Equal(0, released);              // two frames still alive → must not release yet
        Assert.False(src.TryReadNextFrame(out _)); // disposed → no new frames

        f1.Dispose();
        Assert.Equal(0, released);              // one frame still alive
        f2.Dispose();
        Assert.Equal(1, released);              // last ref gone → release fires exactly once
    }

    [Fact]
    public void ReleaseHook_FiresOnce_WhenFrameOutlivesSource_AndFrameDisposeIsIdempotent()
    {
        var pixels = new byte[64 * 64 * 4];
        var released = 0;
        var src = new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels)],
            [64 * 4],
            releaseBuffersOnDispose: () => released++);

        Assert.True(src.TryReadNextFrame(out var f));
        f.Dispose();
        Assert.Equal(0, released); // source still holds its ref
        f.Dispose();               // idempotent — must not decrement twice
        src.Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void SelectOutputFormat_ThrowsForMismatch()
    {
        var pixels = new byte[64 * 64 * 4];
        using var src = new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels)],
            [64 * 4]);

        src.SelectOutputFormat(PixelFormat.Bgra32);
        Assert.Throws<InvalidOperationException>(() => src.SelectOutputFormat(PixelFormat.Nv12));
    }

    [Fact]
    public void PlanesAndStridesMustMatchLength()
    {
        var pixels = new byte[64 * 64 * 4];
        Assert.Throws<ArgumentException>(() => new StaticFrameSource(
            Bgra32_64x64,
            [new ReadOnlyMemory<byte>(pixels), new ReadOnlyMemory<byte>(pixels)],
            [64 * 4]));
    }
}
