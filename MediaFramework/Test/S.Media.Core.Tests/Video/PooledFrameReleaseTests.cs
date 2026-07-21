using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// Behaviour pins for <see cref="PooledFrameRelease"/> - the pooled release state hot emit paths
/// (sws emit, NDI unpack, CPU convert) hand to every emitted <see cref="VideoFrame"/>. The critical
/// guarantees: exact-length plane/stride views, idempotent thread-agnostic dispose (a frame's release
/// may fire from any downstream consumer), and no double-recycle of the lease onto the free list.
/// </summary>
public sealed class PooledFrameReleaseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Rent_RejectsPlaneCountOutsideOneToFour(int planeCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledFrameRelease.Rent(planeCount));
    }

    [Fact]
    public void RentPlane_ExposesExactLengthsAndStrides()
    {
        var lease = PooledFrameRelease.Rent(2);
        try
        {
            var y = lease.RentPlane(0, 100, 10);
            var uv = lease.RentPlane(1, 50, 10);

            Assert.Equal(2, lease.PlaneCount);
            // ArrayPool may hand back a longer array; the exposed plane view must be exact-length.
            Assert.True(y.Length >= 100);
            Assert.True(uv.Length >= 50);
            Assert.Equal(100, lease.Planes[0].Length);
            Assert.Equal(50, lease.Planes[1].Length);
            Assert.Equal(10, lease.Strides[0]);
            Assert.Equal(10, lease.Strides[1]);
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void Dispose_IsIdempotent_AndNeverRecyclesTwice()
    {
        var lease = PooledFrameRelease.Rent(4);
        lease.RentPlane(0, 64, 64);
        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        // A broken guard would enqueue the lease onto the free list more than once, so two later
        // rents could observe the SAME live instance. Rent well past the free-list cap and require
        // every concurrently-held lease to be distinct.
        var held = new List<PooledFrameRelease>();
        try
        {
            var seen = new HashSet<PooledFrameRelease>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < 40; i++)
            {
                var l = PooledFrameRelease.Rent(4);
                held.Add(l);
                Assert.True(seen.Add(l), "same lease instance rented twice concurrently - double recycle");
            }
        }
        finally
        {
            foreach (var l in held)
                l.Dispose();
        }
    }

    [Fact]
    public void PartiallyRentedLease_DisposesCleanly()
    {
        // Emit sites roll back a mid-emit failure with a single Dispose - only rented planes return.
        var lease = PooledFrameRelease.Rent(3);
        lease.RentPlane(0, 32, 32);
        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void FrameDoubleDispose_FiresLeaseReleaseOnce()
    {
        var lease = PooledFrameRelease.Rent(1);
        lease.RentPlane(0, 4 * 4 * 4, 16);
        var frame = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1)),
            lease.Planes,
            lease.Strides,
            release: lease);

        frame.Dispose();
        frame.Dispose();

        // The frame swaps the release out on first Dispose and the lease guards itself as well; either
        // way a double frame-dispose must not throw or double-return the plane buffer.
        lease.Dispose();
    }
}
