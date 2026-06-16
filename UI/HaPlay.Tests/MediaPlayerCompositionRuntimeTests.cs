using HaPlay.Playback;
using S.Media.Core.Video;
using S.Media.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class MediaPlayerCompositionRuntimeTests
{
    private static VideoFormat Canvas => new(320, 180, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public async Task Video_layer0_is_composited_and_fanned_to_all_deck_outputs()
    {
        var a = new RecordingVideoOutput();
        var b = new RecordingVideoOutput();
        using var runtime = new MediaPlayerCompositionRuntime(
            Canvas,
            new[]
            {
                new ClipCompositionOutputLease("out-a", "A", a),
                new ClipCompositionOutputLease("out-b", "B", b),
            },
            videoSourceFormat: Canvas);

        // The deck's video router would feed VideoSink; here we drive it directly.
        runtime.VideoSink.Configure(Canvas);
        runtime.VideoSink.Submit(SolidBgra(TimeSpan.Zero));
        runtime.EnsurePumpStarted();

        var delivered = await WaitUntilAsync(() => a.Count > 0 && b.Count > 0, TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(delivered, "composition did not fan a layer-0 frame to both deck outputs");
        }
        finally
        {
            a.DisposeCaptured();
            b.DisposeCaptured();
        }
    }

    [Fact]
    public void Hold_layer_is_present_only_when_a_logo_is_supplied_and_toggling_is_safe()
    {
        var a = new RecordingVideoOutput();
        using var withLogo = new MediaPlayerCompositionRuntime(
            Canvas,
            new[] { new ClipCompositionOutputLease("out-a", "A", a) },
            videoSourceFormat: Canvas,
            logoFrame: SolidBgra(TimeSpan.Zero));

        Assert.True(withLogo.HasHoldLayer);
        withLogo.SetHold(true);
        withLogo.SetHold(false);
        withLogo.SetVideoOpacity(0.5f);

        var b = new RecordingVideoOutput();
        using var noLogo = new MediaPlayerCompositionRuntime(
            Canvas,
            new[] { new ClipCompositionOutputLease("out-b", "B", b) },
            videoSourceFormat: Canvas);

        Assert.False(noLogo.HasHoldLayer);
        noLogo.SetHold(true);   // no logo layer → no-op, must not throw
    }

    private static VideoFrame SolidBgra(TimeSpan pts)
    {
        var fmt = Canvas;
        var stride = fmt.Width * 4;
        var bytes = new byte[stride * fmt.Height];
        for (var i = 3; i < bytes.Length; i += 4)
            bytes[i] = 255; // opaque
        return new VideoFrame(pts, fmt, bytes, stride);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private sealed class RecordingVideoOutput : IVideoOutput
    {
        private readonly object _gate = new();
        private readonly List<VideoFrame> _captured = new();

        public VideoFormat Format { get; private set; } = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [];

        public int Count
        {
            get { lock (_gate) return _captured.Count; }
        }

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            lock (_gate)
            {
                if (_captured.Count < 4)
                {
                    _captured.Add(frame);
                    return;
                }
            }
            frame.Dispose();
        }

        public void DisposeCaptured()
        {
            lock (_gate)
            {
                foreach (var f in _captured)
                    f.Dispose();
                _captured.Clear();
            }
        }
    }
}
