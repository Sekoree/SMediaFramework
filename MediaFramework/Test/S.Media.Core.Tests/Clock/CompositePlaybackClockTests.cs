using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class CompositePlaybackClockTests
{
    [Fact]
    public void PicksHigherPriorityAdvancingClock()
    {
        var low = new StubPlaybackClock(false, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(5));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(5), c.ElapsedSinceStart);
    }

    [Fact]
    public void WhenNoneAdvancing_ElapsedIsZero()
    {
        var a = new StubPlaybackClock(false, TimeSpan.FromSeconds(3));
        var b = new StubPlaybackClock(false, TimeSpan.FromSeconds(4));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 5),
            new PlaybackClockCandidate(b, 1));

        Assert.False(c.IsAdvancing);
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
    }

    private sealed class StubPlaybackClock : IPlaybackClock
    {
        private readonly bool _adv;
        private readonly TimeSpan _elapsed;

        public StubPlaybackClock(bool adv, TimeSpan elapsed)
        {
            _adv = adv;
            _elapsed = elapsed;
        }

        public TimeSpan ElapsedSinceStart => _elapsed;
        public bool IsAdvancing => _adv;
    }
}
