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
    public void WhenBothAdvancing_UsesHigherPriorityElapsed_NotBlended()
    {
        var low = new StubPlaybackClock(true, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(99));
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(99), c.ElapsedSinceStart);
    }

    [Fact]
    public void RepeatedElapsedReads_whileAdvancing_remain_consistent()
    {
        var a = new StubPlaybackClock(true, TimeSpan.FromSeconds(3.5));
        var c = new CompositePlaybackClock(new PlaybackClockCandidate(a, 1));
        for (var i = 0; i < 5000; i++)
        {
            Assert.True(c.IsAdvancing);
            Assert.Equal(TimeSpan.FromSeconds(3.5), c.ElapsedSinceStart);
        }
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

    [Fact]
    public void RepeatedElapsedReads_idleToSingleAdvancing_switchesFromZeroToActiveElapsed()
    {
        var a = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var b = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(7) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 10),
            new PlaybackClockCandidate(b, 1));

        for (var i = 0; i < 3000; i++)
        {
            if ((i & 1) == 0)
            {
                a.IsAdvancing = false;
                b.IsAdvancing = false;
                Assert.False(c.IsAdvancing);
                Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
            }
            else
            {
                b.IsAdvancing = true;
                b.ElapsedSinceStart = TimeSpan.FromSeconds(7 + i * 0.0001);
                Assert.True(c.IsAdvancing);
                Assert.Equal(b.ElapsedSinceStart, c.ElapsedSinceStart);
            }
        }
    }

    [Fact]
    public void WhenThreeAdvancing_UsesHighestPriorityElapsed_thenFallsThroughWhenHigherStops()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var mid = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(mid, 5),
            new PlaybackClockCandidate(high, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(3), c.ElapsedSinceStart);

        high.IsAdvancing = false;
        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(2), c.ElapsedSinceStart);

        mid.IsAdvancing = false;
        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(1), c.ElapsedSinceStart);

        low.IsAdvancing = false;
        Assert.False(c.IsAdvancing);
        Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
    }

    [Fact]
    public void WhenSamePriority_bothAdvancing_firstRegisteredWinsTies()
    {
        var first = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(7) };
        var second = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(8) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(first, 10),
            new PlaybackClockCandidate(second, 10));

        Assert.True(c.IsAdvancing);
        Assert.Equal(TimeSpan.FromSeconds(7), c.ElapsedSinceStart);
    }

    [Fact]
    public void EqualPriority_fourCandidates_seeded_subsetAdvancing_firstRegisteredWinsAmongAdvancing()
    {
        var a = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var b = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var c = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var d = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.Zero };
        var comp = new CompositePlaybackClock(
            new PlaybackClockCandidate(a, 5),
            new PlaybackClockCandidate(b, 5),
            new PlaybackClockCandidate(c, 5),
            new PlaybackClockCandidate(d, 5));

        var rnd = new Random(314_159);
        for (var i = 0; i < 6000; i++)
        {
            a.IsAdvancing = rnd.Next(2) == 0;
            b.IsAdvancing = rnd.Next(2) == 0;
            c.IsAdvancing = rnd.Next(2) == 0;
            d.IsAdvancing = rnd.Next(2) == 0;
            a.ElapsedSinceStart = TimeSpan.FromTicks(100 + rnd.Next(50));
            b.ElapsedSinceStart = TimeSpan.FromTicks(200 + rnd.Next(50));
            c.ElapsedSinceStart = TimeSpan.FromTicks(300 + rnd.Next(50));
            d.ElapsedSinceStart = TimeSpan.FromTicks(400 + rnd.Next(50));

            var any = a.IsAdvancing || b.IsAdvancing || c.IsAdvancing || d.IsAdvancing;
            Assert.Equal(any, comp.IsAdvancing);
            if (!any)
            {
                Assert.Equal(TimeSpan.Zero, comp.ElapsedSinceStart);
                continue;
            }

            MutableStubPlaybackClock winner;
            if (a.IsAdvancing) winner = a;
            else if (b.IsAdvancing) winner = b;
            else if (c.IsAdvancing) winner = c;
            else winner = d;

            Assert.Equal(winner.ElapsedSinceStart, comp.ElapsedSinceStart);
        }
    }

    [Fact]
    public void RepeatedElapsedReads_threeWayRotatingAdvancing_followsStrictPriority()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var mid = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(2) };
        var high = new MutableStubPlaybackClock { IsAdvancing = false, ElapsedSinceStart = TimeSpan.FromSeconds(3) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(mid, 5),
            new PlaybackClockCandidate(high, 10));

        for (var i = 0; i < 3000; i++)
        {
            var phase = i % 4;
            low.IsAdvancing = phase == 1;
            mid.IsAdvancing = phase == 2;
            high.IsAdvancing = phase == 3;
            low.ElapsedSinceStart = TimeSpan.FromSeconds(10 + i * 0.0001);
            mid.ElapsedSinceStart = TimeSpan.FromSeconds(20 + i * 0.0001);
            high.ElapsedSinceStart = TimeSpan.FromSeconds(30 + i * 0.0001);

            if (phase == 0)
            {
                Assert.False(c.IsAdvancing);
                Assert.Equal(TimeSpan.Zero, c.ElapsedSinceStart);
            }
            else if (phase == 1)
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(low.ElapsedSinceStart, c.ElapsedSinceStart);
            }
            else if (phase == 2)
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(mid.ElapsedSinceStart, c.ElapsedSinceStart);
            }
            else
            {
                Assert.True(c.IsAdvancing);
                Assert.Equal(high.ElapsedSinceStart, c.ElapsedSinceStart);
            }
        }
    }

    [Fact]
    public void RepeatedElapsedReads_underAlternatingAdvancingCandidates_followsHigherPriority()
    {
        var low = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(1) };
        var high = new MutableStubPlaybackClock { IsAdvancing = true, ElapsedSinceStart = TimeSpan.FromSeconds(10) };
        var c = new CompositePlaybackClock(
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 100));

        for (var i = 0; i < 4000; i++)
        {
            if ((i & 1) == 0)
            {
                low.IsAdvancing = false;
                high.IsAdvancing = true;
                high.ElapsedSinceStart = TimeSpan.FromSeconds(10 + i * 0.0001);
            }
            else
            {
                high.IsAdvancing = false;
                low.IsAdvancing = true;
                low.ElapsedSinceStart = TimeSpan.FromSeconds(1 + i * 0.0001);
            }

            Assert.True(c.IsAdvancing);
            var e = c.ElapsedSinceStart;
            if ((i & 1) == 0)
                Assert.Equal(high.ElapsedSinceStart, e);
            else
                Assert.Equal(low.ElapsedSinceStart, e);
        }
    }

    private sealed class MutableStubPlaybackClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; set; }
        public bool IsAdvancing { get; set; }
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
