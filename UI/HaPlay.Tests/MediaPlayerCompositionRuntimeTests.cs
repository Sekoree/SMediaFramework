using System.Threading;
using HaPlay.Playback;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Session;
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
            videoSourceFormat: Canvas,
            compositorFactory: CpuCompositor);

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
    public async Task Video_layer0_letterboxes_wide_sources_instead_of_cropping_sides()
    {
        var output = new RecordingVideoOutput();
        var canvas = new VideoFormat(100, 100, PixelFormat.Bgra32, new Rational(30, 1));
        var source = new VideoFormat(200, 100, PixelFormat.Bgra32, new Rational(30, 1));
        using var runtime = new MediaPlayerCompositionRuntime(
            canvas,
            new[] { new ClipCompositionOutputLease("out-a", "A", output) },
            videoSourceFormat: source,
            compositorFactory: CpuCompositor);

        runtime.VideoSink.Configure(source);
        runtime.VideoSink.Submit(WideSideMarkerFrame(source));
        runtime.EnsurePumpStarted();

        var delivered = await WaitUntilAsync(() => output.Count > 0, TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(delivered, "composition did not deliver a frame");
            var bytes = output.FirstFrameBytes();
            AssertPixel(bytes, canvas.Width, x: 0, y: 50, b: 0, g: 0, r: 255, a: 255);
            AssertPixel(bytes, canvas.Width, x: 99, y: 50, b: 255, g: 0, r: 0, a: 255);
            AssertPixel(bytes, canvas.Width, x: 50, y: 0, b: 0, g: 0, r: 0, a: 0);
        }
        finally
        {
            output.DisposeCaptured();
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
            logoFrame: SolidBgra(TimeSpan.Zero),
            compositorFactory: CpuCompositor);

        Assert.True(withLogo.HasHoldLayer);
        withLogo.SetHold(true);
        withLogo.SetHold(false);
        withLogo.SetVideoOpacity(0.5f);

        var b = new RecordingVideoOutput();
        using var noLogo = new MediaPlayerCompositionRuntime(
            Canvas,
            new[] { new ClipCompositionOutputLease("out-b", "B", b) },
            videoSourceFormat: Canvas,
            compositorFactory: CpuCompositor);

        Assert.False(noLogo.HasHoldLayer);
        noLogo.SetHold(true);   // no logo layer → no-op, must not throw
    }

    [Fact]
    public void RemoveOutput_RetiresOnlyRequestedDeckOutput()
    {
        var releasedA = 0;
        var releasedB = 0;
        using var runtime = new MediaPlayerCompositionRuntime(
            Canvas,
            new[]
            {
                new ClipCompositionOutputLease(
                    "out-a",
                    "A",
                    new RecordingVideoOutput(),
                    Release: () => Interlocked.Increment(ref releasedA)),
                new ClipCompositionOutputLease(
                    "out-b",
                    "B",
                    new RecordingVideoOutput(),
                    Release: () => Interlocked.Increment(ref releasedB)),
            },
            videoSourceFormat: Canvas,
            compositorFactory: CpuCompositor);

        Assert.Equal(2, runtime.OutputCount);

        Assert.True(runtime.RemoveOutput("out-a"));

        Assert.Equal(1, runtime.OutputCount);
        Assert.Equal(1, Volatile.Read(ref releasedA));
        Assert.Equal(0, Volatile.Read(ref releasedB));
        Assert.False(runtime.RemoveOutput("out-a"));
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

    private static ClipCompositionCompositor CpuCompositor(VideoFormat fmt) =>
        new(new CpuVideoCompositor(fmt), RequiresBgraLayerConversion: true, BackendName: "CPU");

    private static VideoFrame WideSideMarkerFrame(VideoFormat fmt)
    {
        var stride = fmt.Width * 4;
        var bytes = new byte[stride * fmt.Height];
        for (var y = 0; y < fmt.Height; y++)
        {
            for (var x = 0; x < fmt.Width; x++)
            {
                var idx = y * stride + x * 4;
                if (x < fmt.Width / 4)
                {
                    bytes[idx + 2] = 255; // red left side
                }
                else if (x >= fmt.Width * 3 / 4)
                {
                    bytes[idx + 0] = 255; // blue right side
                }
                else
                {
                    bytes[idx + 1] = 255; // green center
                }
                bytes[idx + 3] = 255;
            }
        }

        return new VideoFrame(TimeSpan.Zero, fmt, bytes, stride);
    }

    private static void AssertPixel(byte[] bytes, int width, int x, int y, byte b, byte g, byte r, byte a)
    {
        var idx = (y * width + x) * 4;
        Assert.Equal(b, bytes[idx + 0]);
        Assert.Equal(g, bytes[idx + 1]);
        Assert.Equal(r, bytes[idx + 2]);
        Assert.Equal(a, bytes[idx + 3]);
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

        public byte[] FirstFrameBytes()
        {
            lock (_gate)
            {
                var frame = _captured.FirstOrDefault();
                Assert.NotNull(frame);
                return frame.Planes[0].ToArray();
            }
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
