using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class PassThroughDescriptorArenaTests
{
    [Fact]
    public void Rent_Return_roundtrip_reuses_same_array_instances()
    {
        using var arena = new PassThroughDescriptorArena();
        var a = arena.Rent(2);
        Assert.NotNull(a.Planes);
        Assert.Equal(2, a.Planes.Length);
        var planeRef = a.Planes;
        var strideRef = a.Strides;
        arena.Return(in a);

        var b = arena.Rent(2);
        Assert.True(b.FromFixedPool);
        Assert.Same(planeRef, b.Planes);
        Assert.Same(strideRef, b.Strides);
    }

    [Fact]
    public void Parallel_rent_return_does_not_throw()
    {
        using var arena = new PassThroughDescriptorArena();
        const int workers = 8;
        const int iterations = 500;
        Parallel.For(0, workers * iterations, i =>
        {
            var h = arena.Rent(1);
            arena.Return(in h);
        });
    }

    [Fact]
    public void Dispose_then_Return_is_noop()
    {
        var arena = new PassThroughDescriptorArena();
        var h = arena.Rent(1);
        arena.Dispose();
        arena.Return(in h);
    }

    [Fact]
    public void Dispose_then_Rent_throws()
    {
        var arena = new PassThroughDescriptorArena();
        arena.Dispose();
        Assert.Throws<ObjectDisposedException>(() => arena.Rent(1));
    }
}
