using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class VideoPtsClockTests
{
    [Fact]
    public void BeginSession_ZeroPts_ElapsedTracksWallAndPts()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        Thread.Sleep(15);
        c.NotifyFramePts(TimeSpan.FromMilliseconds(40));
        Thread.Sleep(10);

        var e = c.ElapsedSinceStart;
        Assert.InRange(e.TotalMilliseconds, 30, 200);
        Assert.True(c.IsAdvancing);
    }

    [Fact]
    public void Pause_FreezesElapsed_ResumeContinuesFromAnchor()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        c.NotifyFramePts(TimeSpan.FromSeconds(1));
        Thread.Sleep(20);
        var beforeMs = c.ElapsedSinceStart.TotalMilliseconds;
        c.Pause();
        Thread.Sleep(40);
        // Wall vs PTS merge can move slightly on some schedulers; require "mostly frozen" not sample-accurate.
        Assert.True(Math.Abs(c.ElapsedSinceStart.TotalMilliseconds - beforeMs) < 30.0);
        Assert.False(c.IsAdvancing);

        c.Resume();
        Assert.True(c.IsAdvancing);
        var mid = c.ElapsedSinceStart;
        Thread.Sleep(15);
        Assert.True(c.ElapsedSinceStart > mid);
    }

    [Fact]
    public void Seek_RepositionsElapsed()
    {
        var c = new VideoPtsClock();
        c.BeginSession(TimeSpan.Zero);
        c.NotifyFramePts(TimeSpan.FromSeconds(2));
        c.Seek(TimeSpan.FromSeconds(10));
        var e = c.ElapsedSinceStart;
        Assert.InRange(e.TotalMilliseconds, 9970, 10_030);
    }
}
