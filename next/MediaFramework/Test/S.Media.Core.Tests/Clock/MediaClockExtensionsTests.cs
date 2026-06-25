using Xunit;

namespace S.Media.Core.Tests.Clock;

public sealed class MediaClockExtensionsTests
{
    [Fact]
    public void SetMasterChain_with_positive_crossfade_attaches_CompositePlaybackClock()
    {
        using var clock = new MediaClock();
        var low = new StubPlaybackClock(true, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(2));
        clock.SetMasterChain(
            TimeSpan.FromMilliseconds(40),
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));
        Assert.IsType<CompositePlaybackClock>(clock.Master);
    }

    [Fact]
    public void SetMasterChain_with_non_positive_crossfade_attaches_CompositePlaybackClock()
    {
        using var clock = new MediaClock();
        var a = new StubPlaybackClock(true, TimeSpan.FromSeconds(3));
        clock.SetMasterChain(TimeSpan.Zero, new PlaybackClockCandidate(a, 5));
        Assert.IsType<CompositePlaybackClock>(clock.Master);
    }

    [Fact]
    public void SetMasterChain_without_crossfade_overload_matches_explicit_zero_crossfade_type()
    {
        using var clockA = new MediaClock();
        using var clockB = new MediaClock();
        var low = new StubPlaybackClock(true, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(9));
        var c1 = new PlaybackClockCandidate(low, 1);
        var c2 = new PlaybackClockCandidate(high, 10);

        clockA.SetMasterChain(c1, c2);
        clockB.SetMasterChain(TimeSpan.Zero, c1, c2);

        Assert.IsType<CompositePlaybackClock>(clockA.Master);
        Assert.IsType<CompositePlaybackClock>(clockB.Master);
        Assert.Equal(clockA.Master!.ElapsedSinceStart, clockB.Master!.ElapsedSinceStart);
    }

    [Fact]
    public void SetMasterChain_with_crossfade_and_coAdvanceTau_attaches_CompositePlaybackClock()
    {
        using var clock = new MediaClock();
        var low = new StubPlaybackClock(true, TimeSpan.FromSeconds(1));
        var high = new StubPlaybackClock(true, TimeSpan.FromSeconds(9));
        clock.SetMasterChain(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(200),
            new PlaybackClockCandidate(low, 1),
            new PlaybackClockCandidate(high, 10));
        Assert.IsType<CompositePlaybackClock>(clock.Master);
    }

    [Fact]
    public void SetMasterChain_zero_crossfade_positive_coTau_attaches_blend_master()
    {
        using var clock = new MediaClock();
        var a = new StubPlaybackClock(true, TimeSpan.FromSeconds(3));
        clock.SetMasterChain(TimeSpan.Zero, TimeSpan.FromSeconds(2), new PlaybackClockCandidate(a, 5));
        Assert.IsType<CompositePlaybackClock>(clock.Master);
    }

    private sealed class StubPlaybackClock : IPlaybackClock
    {
        public StubPlaybackClock(bool advancing, TimeSpan elapsed)
        {
            IsAdvancing = advancing;
            ElapsedSinceStart = elapsed;
        }

        public TimeSpan ElapsedSinceStart { get; set; }
        public bool IsAdvancing { get; set; }
    }
}
