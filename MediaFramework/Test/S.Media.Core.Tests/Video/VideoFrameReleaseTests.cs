using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoFrameReleaseTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Dispose_InvokesActionRelease()
    {
        var calls = 0;
        using var f = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, new byte[4 * 4 * 4], 16,
            release: DisposableRelease.Wrap(() => calls++));
        f.Dispose();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_InvokesDisposableRelease()
    {
        var disposable = new CountingDisposable();
        using var f = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, new byte[4 * 4 * 4], 16,
            release: disposable);
        f.Dispose();
        Assert.Equal(1, disposable.Disposes);
    }

    [Fact]
    public void Dispose_InvokesChainedRelease()
    {
        var calls = 0;
        var disposable = new CountingDisposable();
        using var f = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, new byte[4 * 4 * 4], 16,
            release: DisposableRelease.Chain(disposable, DisposableRelease.Wrap(() => calls++)));
        f.Dispose();
        Assert.Equal(1, calls);
        Assert.Equal(1, disposable.Disposes);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var calls = 0;
        var disposable = new CountingDisposable();
        var f = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, new byte[4 * 4 * 4], 16,
            release: DisposableRelease.Chain(disposable, DisposableRelease.Wrap(() => calls++)));
        f.Dispose();
        f.Dispose();
        f.Dispose();
        Assert.Equal(1, calls);
        Assert.Equal(1, disposable.Disposes);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Disposes;
        public void Dispose() => Disposes++;
    }
}
