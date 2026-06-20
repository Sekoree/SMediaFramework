using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class PlaybackSleepInhibitorTests
{
    [Fact]
    public void Acquire_ReferenceCountsSinglePlatformLease()
    {
        var backend = new FakeBackend();
        var inhibitor = new PlaybackSleepInhibitor(backend);

        var first = inhibitor.Acquire("first");
        var second = inhibitor.Acquire("second");

        Assert.Equal(2, inhibitor.ActiveLeaseCount);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(0, backend.DisposeCount);

        second.Dispose();
        Assert.Equal(1, inhibitor.ActiveLeaseCount);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(0, backend.DisposeCount);

        first.Dispose();
        Assert.Equal(0, inhibitor.ActiveLeaseCount);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public void Lease_Dispose_IsIdempotent()
    {
        var backend = new FakeBackend();
        var inhibitor = new PlaybackSleepInhibitor(backend);

        var lease = inhibitor.Acquire("playback");
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, inhibitor.ActiveLeaseCount);
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public void Acquire_BackendFailure_DoesNotThrowOrBreakRelease()
    {
        var inhibitor = new PlaybackSleepInhibitor(new ThrowingBackend());

        var lease = inhibitor.Acquire("playback");

        Assert.Equal(1, inhibitor.ActiveLeaseCount);
        lease.Dispose();
        Assert.Equal(0, inhibitor.ActiveLeaseCount);
    }

    private sealed class FakeBackend : IPlaybackSleepInhibitorBackend
    {
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }

        public IDisposable Inhibit(string reason)
        {
            _ = reason;
            StartCount++;
            return new Lease(this);
        }

        private sealed class Lease : IDisposable
        {
            private FakeBackend? _owner;

            public Lease(FakeBackend owner) => _owner = owner;

            public void Dispose()
            {
                if (_owner is { } owner)
                {
                    _owner = null;
                    owner.DisposeCount++;
                }
            }
        }
    }

    private sealed class ThrowingBackend : IPlaybackSleepInhibitorBackend
    {
        public IDisposable? Inhibit(string reason)
        {
            _ = reason;
            throw new InvalidOperationException("boom");
        }
    }
}
