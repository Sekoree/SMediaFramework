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
        var sink = new FakeVideoSink([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, sink, clock);

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
        var sink = new FakeVideoSink([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, sink, clock);
        video.Play();
        sink.WaitForConfigured();

        AvPlaybackCoordinator.SeekCoordinated(video, null, TimeSpan.FromSeconds(1));

        Assert.False(video.IsRunning);
    }

    [Fact]
    public void MediaPlaybackSession_DelegatesToCoordinator()
    {
        var fmt = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var stride = 16 * 4;
        var frameBytes = new byte[stride * 16];
        var src = new FakeVideoSource(fmt, (TimeSpan.Zero, frameBytes, stride));
        var sink = new FakeVideoSink([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var video = new VideoPlayer(src, sink, clock);
        var session = new MediaPlaybackSession(video, clock);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.Play(verifyPrebufferAfterPrefill: () => false));

        Assert.Contains("verifyPrebufferAfterPrefill", ex.Message, StringComparison.Ordinal);
    }
}
