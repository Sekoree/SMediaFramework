using System.Diagnostics;
using System.Threading;
using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class MediaClockTests
{
    private const int SettleMs = 80;

    [Fact]
    public void MediaClock_IsAssignableToIPlaybackTimeline()
    {
        using var clock = new MediaClock();
        IPlaybackTimeline timeline = clock;
        Assert.Equal(TimeSpan.Zero, timeline.CurrentPosition);
        Assert.False(timeline.IsRunning);
        Assert.Equal(1.0, timeline.PlaybackRate);
        timeline.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), timeline.CurrentPosition);
    }

    [Fact]
    public void SubscribePositionChanged_DisposeUnsubscribes()
    {
        using var clock = new MediaClock();
        var hits = 0;
        void Handler(object? _, TimeSpan __) => Interlocked.Increment(ref hits);

        using (clock.SubscribePositionChanged(Handler))
        {
            clock.Seek(TimeSpan.FromSeconds(2));
            Assert.True(hits >= 1);
        }

        var after = hits;
        clock.Seek(TimeSpan.FromSeconds(3));
        Assert.Equal(after, hits);
    }

    [Fact]
    public void NewClock_StartsAtZeroAndNotRunning()
    {
        using var clock = new MediaClock();

        Assert.Equal(TimeSpan.Zero, clock.CurrentPosition);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void Start_AdvancesPosition()
    {
        using var clock = new MediaClock();

        clock.Start();
        Assert.True(clock.IsRunning);
        Thread.Sleep(SettleMs);
        var pos = clock.CurrentPosition;

        Assert.True(pos > TimeSpan.FromMilliseconds(SettleMs / 2),
            $"expected position > {SettleMs / 2} ms, got {pos.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Pause_RetainsPositionAndStopsAdvancing()
    {
        using var clock = new MediaClock();

        clock.Start();
        Thread.Sleep(SettleMs);
        clock.Pause();
        var atPause = clock.CurrentPosition;
        Thread.Sleep(SettleMs);
        var afterWait = clock.CurrentPosition;

        Assert.False(clock.IsRunning);
        Assert.Equal(atPause, afterWait);
    }

    [Fact]
    public void Resume_AfterPause_ContinuesFromPaused()
    {
        using var clock = new MediaClock();

        clock.Start();
        Thread.Sleep(SettleMs);
        clock.Pause();
        var atPause = clock.CurrentPosition;
        clock.Start();
        Thread.Sleep(SettleMs);
        var afterResume = clock.CurrentPosition;

        Assert.True(afterResume > atPause,
            $"resume position {afterResume.TotalMilliseconds:F1} ms should exceed pause {atPause.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Seek_JumpsToPositionImmediately()
    {
        using var clock = new MediaClock();
        var target = TimeSpan.FromSeconds(42);

        clock.Seek(target);

        Assert.Equal(target, clock.CurrentPosition);
    }

    [Fact]
    public void Seek_FiresPositionChangedSynchronously()
    {
        using var clock = new MediaClock();
        TimeSpan? observed = null;
        clock.PositionChanged += (_, p) => observed = p;
        var target = TimeSpan.FromSeconds(10);

        clock.Seek(target);

        Assert.Equal(target, observed);
    }

    [Fact]
    public void Reset_ZerosPositionAndKeepsRunningState()
    {
        using var clock = new MediaClock();

        clock.Start();
        Thread.Sleep(SettleMs);
        clock.Reset();

        Assert.True(clock.IsRunning);
        Assert.True(clock.CurrentPosition < TimeSpan.FromMilliseconds(SettleMs / 2),
            $"after reset position {clock.CurrentPosition.TotalMilliseconds:F1} ms should be near zero");
    }

    [Fact]
    public void Seek_NegativePosition_Throws()
    {
        using var clock = new MediaClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Seek(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AudioTick_FiresAtApproximateRate()
    {
        var interval = TimeSpan.FromMilliseconds(20);
        using var clock = new MediaClock(audioTickInterval: interval, videoTickInterval: TimeSpan.FromMilliseconds(100));
        var ticks = 0;
        clock.AudioTick += (_, _) => Interlocked.Increment(ref ticks);

        clock.Start();
        Thread.Sleep(300);
        clock.Pause();

        // 300 ms / 20 ms = 15 expected; allow generous slack for thread scheduling.
        var observed = Volatile.Read(ref ticks);
        Assert.InRange(observed, 8, 25);
    }

    [Fact]
    public void VideoTick_FiresAtApproximateRate()
    {
        var interval = TimeSpan.FromMilliseconds(50);
        using var clock = new MediaClock(audioTickInterval: TimeSpan.FromMilliseconds(200), videoTickInterval: interval);
        var ticks = 0;
        clock.VideoTick += (_, _) => Interlocked.Increment(ref ticks);

        clock.Start();
        Thread.Sleep(300);
        clock.Pause();

        var observed = Volatile.Read(ref ticks);
        Assert.InRange(observed, 3, 10);
    }

    [Fact]
    public void Pause_StopsTickingPromptly()
    {
        using var clock = new MediaClock();
        var ticks = 0;
        clock.AudioTick += (_, _) => Interlocked.Increment(ref ticks);

        clock.Start();
        Thread.Sleep(80);
        clock.Pause();
        var atPause = Volatile.Read(ref ticks);
        Thread.Sleep(120);
        var afterWait = Volatile.Read(ref ticks);

        // A handful of stragglers from the in-flight loop iteration are tolerable.
        Assert.True(afterWait - atPause <= 2,
            $"got {afterWait - atPause} ticks after pause; loop did not stop");
    }

    [Fact]
    public void Dispose_StopsDriverThread()
    {
        var clock = new MediaClock();
        var ticks = 0;
        clock.AudioTick += (_, _) => Interlocked.Increment(ref ticks);

        clock.Start();
        Thread.Sleep(60);
        clock.Dispose();
        var atDispose = Volatile.Read(ref ticks);
        Thread.Sleep(120);

        Assert.Equal(atDispose, Volatile.Read(ref ticks));
        Assert.Throws<ObjectDisposedException>(() => clock.Start());
    }

    [Fact]
    public void DoubleStart_IsIdempotent()
    {
        using var clock = new MediaClock();

        clock.Start();
        clock.Start();
        Thread.Sleep(60);

        Assert.True(clock.IsRunning);
        Assert.True(clock.CurrentPosition > TimeSpan.Zero);
    }

    [Fact]
    public void PausedSeek_KeepsPaused()
    {
        using var clock = new MediaClock();
        var target = TimeSpan.FromSeconds(5);

        clock.Seek(target);
        Thread.Sleep(40);

        Assert.False(clock.IsRunning);
        Assert.Equal(target, clock.CurrentPosition);
    }

    [Fact]
    public void RunningSeek_KeepsRunning()
    {
        using var clock = new MediaClock();
        var target = TimeSpan.FromSeconds(5);

        clock.Start();
        Thread.Sleep(40);
        clock.Seek(target);
        Thread.Sleep(80);

        Assert.True(clock.IsRunning);
        Assert.True(clock.CurrentPosition > target,
            $"position {clock.CurrentPosition} should have advanced past seek target {target}");
    }

    [Fact]
    public void RepeatedSeek_whileRunning_doesNotThrow()
    {
        using var clock = new MediaClock();
        clock.Start();
        for (var i = 0; i < 3000; i++)
            clock.Seek(TimeSpan.FromTicks(1000L * i));

        Assert.True(clock.IsRunning);
        Assert.True(clock.CurrentPosition >= TimeSpan.Zero);
    }
}
