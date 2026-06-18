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
    public void Play_WhenVerifyPrebufferAfterPrefillFalse_ThrowsAfterVideoOnlyStart()
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
        Assert.True(video.IsRunning);
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

    [Fact]
    public void IsVideoBufferReadyForSync_RequiresQueuedPresentableFrames_NotLifetimePending()
    {
        var frames = Enumerable.Range(0, 24)
            .Select(i => (TimeSpan.FromMilliseconds(i * 42), new byte[16 * 16 * 4], 16 * 4))
            .ToArray();
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var src = new FakeVideoSource(fmt, frames);
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock, queueCapacity: 16);

        video.Play();
        output.WaitForConfigured();
        WaitFor(() => video.QueuedFrameCount >= 2, TimeSpan.FromSeconds(2));

        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));
        clock.RaiseVideoTick();
        WaitFor(() => video.DisplayedCount >= 1, TimeSpan.FromSeconds(1));
        video.Pause();

        Assert.Equal(0, video.QueuedFrameCount);
        Assert.True(video.PendingBufferedCount >= 12);

        Assert.False(AvPlaybackCoordinator.IsVideoBufferReadyForSync(video, clock.CurrentPosition));
    }

    [Fact]
    public void IsVideoBufferReadyForSync_AcceptsFirstFrameOnePeriodAfterTarget()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(24, 1));
        var stride = 16 * 4;
        var bytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt,
            (TimeSpan.FromMilliseconds(42), bytes, stride),
            (TimeSpan.FromMilliseconds(84), bytes, stride));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock, queueCapacity: 4);

        video.Play();
        output.WaitForConfigured();
        WaitFor(() => video.QueuedFrameCount >= 1, TimeSpan.FromSeconds(1));

        Assert.True(AvPlaybackCoordinator.IsVideoBufferReadyForSync(video, TimeSpan.Zero));
    }

    [Fact]
    public void IsVideoBufferReadyForSync_AcceptsSaturatedBufferWhenFirstFrameBeyondLead()
    {
        // Mirrors mambo.mp4: 25 fps (lead = 1.5/25 = 60 ms), video starts two frame periods after the
        // clock origin so the earliest queued frame (80 ms) sits beyond the lead window. The buffer
        // saturates and the decode thread blocks; without the saturated-buffer escape the gate would
        // spin its full 8 s timeout waiting for a sub-60 ms frame that can never arrive.
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(25, 1));
        var stride = 16 * 4;
        var bytes = new byte[stride * 16];
        const int cap = 4;
        var frames = Enumerable.Range(2, cap + 4)
            .Select(i => (TimeSpan.FromMilliseconds(i * 40), bytes, stride))
            .ToArray();
        var src = new FakeVideoSource(fmt, frames);
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, output, clock, queueCapacity: cap);

        video.Play();
        output.WaitForConfigured();
        WaitFor(() => video.IsJitterBufferSaturated, TimeSpan.FromSeconds(2));

        // No frame within 60 ms of target 0 (earliest is 80 ms), but the buffer is full.
        Assert.False(video.HasFrameWithinLeadOf(TimeSpan.Zero, video.SyncStartupLead));
        Assert.True(AvPlaybackCoordinator.IsVideoBufferReadyForSync(video, TimeSpan.Zero));
        video.Pause();
    }

    [Fact]
    public void Play_OnResume_RealignsAudioSourceWhenAheadOfClock()
    {
        var vFmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(24, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var inner = new FakeVideoSource(vFmt, (TimeSpan.FromSeconds(10), frameBytes, stride));
        var videoSrc = new SeekableFakeVideoSource(inner) { Position = TimeSpan.FromSeconds(10) };
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var videoClock = new FakeMediaClock();
        videoClock.Seek(TimeSpan.FromSeconds(10));
        using var video = new VideoPlayer(videoSrc, output, videoClock);

        var audioFmt = new AudioFormat(44100, 2);
        var audioSrc = new SeekableResumeAudioSource(audioFmt) { Position = TimeSpan.FromSeconds(10.7) };
        using var router = new AudioRouter(44100);
        var srcId = router.AddSource(audioSrc, "src");
        var outId = router.AddOutput(new SilentAudioOutput(audioFmt));
        router.Connect(srcId, outId);

        using var audioClock = new MediaClock();
        audioClock.Seek(TimeSpan.FromSeconds(10));

        AvPlaybackCoordinator.Play(video, router, audioClock, audioSourceId: srcId,
            verifyPrebufferAfterPrefill: () => true);

        Assert.Equal(TimeSpan.FromSeconds(10), audioSrc.LastSeekTo);
        router.Stop();
        video.Pause();
    }

    [Fact]
    public void Play_WithAudio_PresentsVideoFrameBeforeStartingAudioClock()
    {
        var vFmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(24, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(vFmt,
            (TimeSpan.FromSeconds(10), frameBytes, stride),
            (TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(42), frameBytes, stride));
        using var clock = new MediaClock();
        clock.Seek(TimeSpan.FromSeconds(10));
        var output = new ClockObservingVideoOutput([PixelFormat.Bgra32], clock);
        using var video = new VideoPlayer(src, output, clock, queueCapacity: 4);

        var audioFmt = new AudioFormat(44100, 2);
        var audioSrc = new SeekableResumeAudioSource(audioFmt) { Position = TimeSpan.FromSeconds(10) };
        using var router = new AudioRouter(44100, chunkSamples: 64);
        var srcId = router.AddSource(audioSrc, "src");
        var outId = router.AddOutput(new SilentAudioOutput(audioFmt));
        router.Connect(srcId, outId);

        try
        {
            AvPlaybackCoordinator.Play(video, router, clock, audioSourceId: srcId);

            WaitFor(() => output.SubmittedCount >= 1, TimeSpan.FromSeconds(1));

            Assert.False(output.FirstSubmitClockWasRunning);
            Assert.Equal(TimeSpan.FromSeconds(10), output.FirstSubmittedPresentationTime);
            Assert.True(output.AbandonQueuedFramesCalls >= 1);
            Assert.True(output.WaitForIdleCalls >= 1);
        }
        finally
        {
            router.Stop();
            video.Pause();
            clock.Pause();
        }
    }

    private sealed class SeekableResumeAudioSource(AudioFormat fmt) : IAudioSource, ISeekableSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromMinutes(10);
        public TimeSpan Position { get; set; }
        public TimeSpan? LastSeekTo { get; private set; }

        public int ReadInto(Span<float> dst)
        {
            dst.Clear();
            return dst.Length;
        }

        public void Seek(TimeSpan position)
        {
            LastSeekTo = position;
            Position = position;
        }
    }

    private sealed class SilentAudioOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockObservingVideoOutput(PixelFormat[] accepted, IMediaClock clock) : IVideoOutput, IVideoOutputQueueControl
    {
        private readonly List<TimeSpan> _submitted = new();
        private int _firstSubmitRecorded;
        private int _waitForIdleCalls;
        private int _abandonQueuedFramesCalls;

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = accepted;
        public VideoFormat Format { get; private set; }
        public bool FirstSubmitClockWasRunning { get; private set; }
        public int WaitForIdleCalls => Volatile.Read(ref _waitForIdleCalls);
        public int AbandonQueuedFramesCalls => Volatile.Read(ref _abandonQueuedFramesCalls);

        public int SubmittedCount
        {
            get { lock (_submitted) return _submitted.Count; }
        }

        public TimeSpan FirstSubmittedPresentationTime
        {
            get { lock (_submitted) return _submitted[0]; }
        }

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            if (Interlocked.Exchange(ref _firstSubmitRecorded, 1) == 0)
                FirstSubmitClockWasRunning = clock.IsRunning;

            lock (_submitted)
                _submitted.Add(frame.PresentationTime);

            frame.Dispose();
        }

        public void AbandonQueuedFrames() => Interlocked.Increment(ref _abandonQueuedFramesCalls);

        public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _waitForIdleCalls);
            return true;
        }
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("condition not met within timeout");
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
