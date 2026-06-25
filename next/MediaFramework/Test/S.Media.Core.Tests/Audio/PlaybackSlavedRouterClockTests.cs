using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class PlaybackSlavedRouterClockTests
{
    [Fact]
    public void WaitForNextChunk_AdvancesWithIngestElapsed()
    {
        var master = new FakeIngestClock(TimeSpan.FromMilliseconds(20));
        var clock = new PlaybackSlavedRouterClock(master, 48_000, 480);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clock.WaitForNextChunk(cts.Token));
        Assert.True(master.ElapsedSinceStart >= TimeSpan.FromMilliseconds(10));
    }

    private sealed class FakeIngestClock(TimeSpan elapsed) : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; private set; } = elapsed;
        public bool IsAdvancing => true;

        public void Advance(TimeSpan delta) => ElapsedSinceStart += delta;
    }
}
