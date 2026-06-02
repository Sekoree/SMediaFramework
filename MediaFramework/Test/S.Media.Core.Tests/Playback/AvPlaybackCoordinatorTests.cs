using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Tests.Video;
using S.Media.Core.Video;
using S.Media.FFmpeg;
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
            AvPlaybackCoordinator.Play(video, null, null,
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

        AvPlaybackCoordinator.SeekCoordinated(video, null, null, null, TimeSpan.FromSeconds(1));

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
        AvPlaybackCoordinator.Pause(video, null, null, default, () => flushCalls++);

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
        Assert.Null(session.AudioRouter);
        Assert.Same(clock, session.Clock);
        Assert.Equal(1.0, session.Clock.PlaybackRate);
    }

    [Fact]
    public void IPlayhead_AsPlayhead_mirrors_position_rate_running()
    {
        using var clock = new MediaClock();
        IPlayhead t = clock;
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

        AvPlaybackCoordinator.Pause(video, null, null, default, () => { });

        Assert.False(clock.IsRunning);
        Assert.False(video.IsRunning);
    }

    [Fact]
    public void MediaContainerSession_Pause_DefaultInvokesFlush()
    {
        using var fixture = new ContainerSessionFixture();

        fixture.Session.Pause();

        Assert.Equal(1, fixture.FlushCalls);
        Assert.Equal(1, fixture.Inner.PauseCalls);
    }

    [Fact]
    public void MediaContainerSession_PauseSkippingSharedMuxFlush_SkipsFlush()
    {
        using var fixture = new ContainerSessionFixture();

        fixture.Session.PauseSkippingSharedMuxFlush();

        Assert.Equal(0, fixture.FlushCalls);
        Assert.Equal(1, fixture.Inner.PauseCalls);
    }

    [Fact]
    public void MediaContainerSession_PausePolicy_ControlsFlush()
    {
        using var fixture = new ContainerSessionFixture();

        fixture.Session.Pause(default, PauseFlushPolicy.SkipFlush);
        fixture.Session.Pause(default, PauseFlushPolicy.FlushCodecPipelines);

        Assert.Equal(1, fixture.FlushCalls);
        Assert.Equal(2, fixture.Inner.PauseCalls);
    }

    [Fact]
    public void MediaContainerSession_SeekCoordinatedPolicy_ControlsFlush()
    {
        using var fixture = new ContainerSessionFixture(seekableVideo: true);

        fixture.Session.SeekCoordinated(TimeSpan.FromMilliseconds(250), default, PauseFlushPolicy.SkipFlush);
        fixture.Session.SeekCoordinated(TimeSpan.FromMilliseconds(500), default, PauseFlushPolicy.FlushCodecPipelines);

        Assert.Equal(1, fixture.FlushCalls);
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

    private sealed class ContainerSessionFixture : IDisposable
    {
        private static readonly VideoFormat Fmt = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        private static readonly byte[] FrameBytes = new byte[16 * 16 * 4];
        private readonly VideoPlayer _video;

        public ContainerSessionFixture(bool seekableVideo = false)
        {
            var source = new FakeVideoSource(Fmt, (TimeSpan.Zero, FrameBytes, 16 * 4));
            IVideoSource videoSource = seekableVideo ? new SeekableFakeVideoSource(source) : source;
            var output = new FakeVideoOutput([PixelFormat.Bgra32]);
            var clock = new FakeMediaClock();
            _video = new VideoPlayer(videoSource, output, clock);
            Inner = new RecordingAvPlaybackSession(_video, clock);
            Session = new MediaContainerSession(Inner, () => FlushCalls++);
        }

        public MediaContainerSession Session { get; }
        public RecordingAvPlaybackSession Inner { get; }
        public int FlushCalls { get; private set; }

        public void Dispose() => _video.Dispose();
    }

    private sealed class RecordingAvPlaybackSession(VideoPlayer video, IMediaClock clock) : IAvPlaybackSession
    {
        public int PauseCalls { get; private set; }
        public VideoPlayer Video { get; } = video;
        public IMediaClock Clock { get; } = clock;
        public AudioRouter? AudioRouter => null;
        public MediaClock? AudioClock => null;
        public string? AudioSourceId => null;

        public void Play(
            Action? prefillBeforeHardware = null,
            Action? startHardware = null,
            IPlaybackClock? videoOnlyMaster = null,
            Func<bool>? verifyPrebufferAfterPrefill = null)
        {
            Video.Play();
        }

        public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null)
        {
            PauseCalls++;
            flushSharedMuxAfterPause?.Invoke();
        }

        public void Seek(TimeSpan position) => Video.Seek(position);

        public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
            Action? flushSharedMuxAfterPause = null)
        {
            Pause(cancellationToken, flushSharedMuxAfterPause);
            Seek(position);
        }
    }
}
