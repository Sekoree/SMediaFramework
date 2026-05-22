using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Tests.Video;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Playback;

public class AvPlaybackCoordinatorTests
{
    [Fact]
    public void Play_WhenVerifyPrebufferAfterPrefillFalse_ThrowsAndDoesNotStartVideo()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AvPlaybackCoordinator.Play(video, null,
                prefillBeforeHardware: () => { },
                startHardware: () => { },
                videoOnlyMaster: null,
                verifyPrebufferAfterPrefill: () => false));

        Assert.Contains("verifyPrebufferAfterPrefill", ex.Message, StringComparison.Ordinal);
        Assert.False(video.IsRunning);
    }

    [Fact]
    public void SeekCoordinated_PausesVideo()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var inner = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var src = new SeekableFakeVideoSource(inner);
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        video.Play();
        output.WaitForConfigured();

        AvPlaybackCoordinator.SeekCoordinated(video, null, TimeSpan.FromSeconds(1));

        Assert.False(video.IsRunning);
    }

    [Fact]
    public void Pause_InvokesOptionalFlushAfterBothSidesPaused()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        video.Play();
        output.WaitForConfigured();

        var flushCalls = 0;
        AvPlaybackCoordinator.Pause(video, null, default, () => flushCalls++);

        Assert.False(video.IsRunning);
        Assert.Equal(1, flushCalls);
    }

    [Fact]
    public void MediaPlaybackSession_DelegatesToCoordinator()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        var session = new MediaPlaybackSession(video, clock);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.Play(verifyPrebufferAfterPrefill: () => false));

        Assert.Contains("verifyPrebufferAfterPrefill", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaPlaybackSession_Pause_forwards_flush_delegate()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        var session = new MediaPlaybackSession(video, clock);

        var flushCalls = 0;
        session.Pause(default, () => flushCalls++);

        Assert.False(video.IsRunning);
        Assert.Equal(1, flushCalls);
    }

    [Fact]
    public void MediaPlaybackSession_ImplementsIAvPlaybackSession_TimelineIsClock()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        IAvPlaybackSession session = new MediaPlaybackSession(video, clock);

        Assert.Same(video, session.Video);
        Assert.Same(clock, session.Clock);
        Assert.Null(session.Audio);
        Assert.Same(clock, session.Timeline);
        Assert.Equal(1.0, session.Timeline.PlaybackRate);
    }

    [Fact]
    public void IPlaybackTimeline_AsPlayhead_mirrors_position_rate_running()
    {
        using var clock = new MediaClock();
        IPlaybackTimeline t = clock;
        var ph = t.AsPlayhead();
        Assert.Equal(clock.CurrentPosition, ph.CurrentPosition);
        Assert.Equal(clock.IsRunning, ph.IsRunning);
        Assert.Equal(clock.PlaybackRate, ph.PlaybackRate);
    }

    [Fact]
    public void Pause_WhenAudioNull_StopsMediaClockDriver()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        using var clock = new MediaClock();
        using var video = new VideoPlayer(src, output, clock);
        clock.Start();
        video.Play();
        output.WaitForConfigured();

        AvPlaybackCoordinator.Pause(video, null, default, () => { });

        Assert.False(clock.IsRunning);
        Assert.False(video.IsRunning);
    }

    [Fact]
    public void IAvPlaybackSession_SubscribePositionChanged_DisposeUnsubscribes()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock);
        IAvPlaybackSession session = new MediaPlaybackSession(video, clock);
        var hits = 0;
        void Handler(object? _, TimeSpan __) => System.Threading.Interlocked.Increment(ref hits);
        using (session.SubscribePositionChanged(Handler))
        {
            clock.AdvanceTo(TimeSpan.FromSeconds(1));
            Assert.True(hits >= 1);
        }

        var after = hits;
        clock.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.Equal(after, hits);
    }
}
