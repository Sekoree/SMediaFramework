using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>Cross-fade between two clips via two compositor layers + opacity transitions.</summary>
public sealed class VideoCompositorCrossFadeTests
{
    private static readonly VideoFormat Bgra32_8x8 = new(8, 8, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void TwoLayers_OpacityTransition_CrossFadesRgb()
    {
        var red = MakeSolid(200, 0, 0, 255);
        var blue = MakeSolid(0, 0, 200, 255);

        using var clipA = StaticFrameSource.FromFrame(red);
        using var clipB = StaticFrameSource.FromFrame(blue);
        using var program = VideoCompositor.Create(Bgra32_8x8, VideoCompositorBackend.Cpu);

        var layerA = program.AddLayer(clipA, LayerConfig.Background);
        var layerB = program.AddLayer(clipB, new LayerConfig(LayerPosition.Center, 1f, 0f));

        layerA.AddTransition(TimeSpan.Zero, Transition.FadeTo(0f, TimeSpan.FromMilliseconds(100)));
        layerB.AddTransition(TimeSpan.Zero, Transition.FadeTo(1f, TimeSpan.FromMilliseconds(100)));

        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(50));

        Assert.True(program.TryReadNextFrame(out var mid));
        try
        {
            var span = mid.Planes[0].Span;
            // BGRA: both layers at ~50% — red and blue channels partially present.
            Assert.InRange(span[0], 40, 160);
            Assert.InRange(span[2], 40, 160);
        }
        finally
        {
            mid.Dispose();
        }

        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(150));
        Assert.True(program.TryReadNextFrame(out var end));
        try
        {
            var span = end.Planes[0].Span;
            Assert.InRange(span[0], 180, 220);
            Assert.Equal(0, span[2]);
        }
        finally
        {
            end.Dispose();
        }
    }

    [Fact]
    public void CutTransition_SwitchesToSecondLayerConfig()
    {
        var a = MakeSolid(10, 20, 30, 255);
        var b = MakeSolid(40, 50, 60, 255);

        using var srcA = StaticFrameSource.FromFrame(a);
        using var srcB = StaticFrameSource.FromFrame(b);
        using var program = VideoCompositor.Create(Bgra32_8x8, VideoCompositorBackend.Cpu);

        var handle = program.AddLayer(srcA, LayerConfig.Background);
        handle.AddTransition(TimeSpan.FromMilliseconds(40), Transition.Cut(new LayerConfig(LayerPosition.Center, 0.5f, 1f)));

        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(50));
        Assert.True(program.TryReadNextFrame(out var frame));
        try
        {
            // Layer A at half scale — still red-ish dominant if only one layer visible; second source not added.
            Assert.True(frame.Planes[0].Span[0] < 30);
        }
        finally
        {
            frame.Dispose();
        }

        program.AddLayer(srcB, LayerConfig.Background);
        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(50));
        Assert.True(program.TryReadNextFrame(out var both));
        try
        {
            var span = both.Planes[0].Span;
            Assert.True(span[0] > 30 || span[1] > 40);
        }
        finally
        {
            both.Dispose();
        }
    }

    [Fact]
    public void TimeDriven_SelectsFrameCoveringMasterTime_AndDecouplesFromReadRate()
    {
        // P3-4: layers advance to the master clock, not by one frame per downstream read. So (a) the
        // composited layer frame is the one whose interval contains the clock position (frame-accurate),
        // (b) reading repeatedly at a FIXED clock time neither pulls nor advances the layer (no
        // decode-speed coupling), and (c) the composite is stamped with the master time.
        var fmt = new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1));
        var src = new IndexedFrameSource(fmt);
        using var program = VideoCompositor.Create(fmt, VideoCompositorBackend.Cpu);
        program.AddLayer(src, LayerConfig.Background);

        // 70 ms → frame 2 (PTS 66.7 ms ≤ 70 < 100 ms) is the cover.
        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(70));
        Assert.True(program.TryReadNextFrame(out var f1));
        Assert.Equal(2, f1.Planes[0].Span[2]);                            // R channel encodes the frame index
        Assert.Equal(TimeSpan.FromMilliseconds(70), f1.PresentationTime); // composite stamped with master time
        f1.Dispose();

        // Reading 10 more times at the SAME clock position must not advance/pull the layer.
        var pulledAfterFirst = src.FramesPulled;
        for (var i = 0; i < 10; i++)
        {
            Assert.True(program.TryReadNextFrame(out var f));
            Assert.Equal(2, f.Planes[0].Span[2]);
            f.Dispose();
        }
        Assert.Equal(pulledAfterFirst, src.FramesPulled);

        // Advancing the clock advances the displayed frame: 140 ms → frame 4 (133.3 ms ≤ 140 < 166.7 ms).
        program.Clock = new FakePlaybackClock(TimeSpan.FromMilliseconds(140));
        Assert.True(program.TryReadNextFrame(out var f2));
        Assert.Equal(4, f2.Planes[0].Span[2]);
        f2.Dispose();
    }

    [Fact]
    public void NoClock_IsReadPaced_OneInnerFramePerRead()
    {
        // With no Clock the compositor is read-paced: a single-layer scaler/adapter (OutputPresetVideoSource)
        // advances its inner source exactly one frame per read — a 1:1 passthrough with no PTS-grid drift
        // and no wall-clock stall. Inner frame N shows on read N.
        var fmt = new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1));
        var src = new IndexedFrameSource(fmt);
        using var program = VideoCompositor.Create(fmt, VideoCompositorBackend.Cpu);
        program.AddLayer(src, LayerConfig.Background);

        for (var n = 0; n < 6; n++)
        {
            Assert.True(program.TryReadNextFrame(out var f));
            Assert.Equal(n, f.Planes[0].Span[2]);
            f.Dispose();
        }
        Assert.Equal(6, src.FramesPulled);
    }

    private sealed class IndexedFrameSource(VideoFormat fmt) : IVideoSource
    {
        private int _next;
        public VideoFormat Format { get; } = fmt;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public bool IsExhausted => false;
        public int FramesPulled => _next;
        public void SelectOutputFormat(PixelFormat format) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var idx = _next++;
            var buf = new byte[Format.Width * Format.Height * 4];
            for (var i = 0; i < Format.Width * Format.Height; i++)
            {
                buf[i * 4 + 2] = (byte)idx; // R = frame index, so the composite identifies which frame was chosen
                buf[i * 4 + 3] = 255;       // opaque
            }
            frame = new VideoFrame(TimeSpan.FromSeconds(idx / 30.0), Format, [buf], [Format.Width * 4], release: null);
            return true;
        }
    }

    private static VideoFrame MakeSolid(byte r, byte g, byte b, byte a)
    {
        var buf = new byte[8 * 8 * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b;
            buf[i + 1] = g;
            buf[i + 2] = r;
            buf[i + 3] = a;
        }

        return new VideoFrame(
            TimeSpan.Zero,
            Bgra32_8x8,
            [buf],
            [8 * 4],
            release: null);
    }

    private sealed class FakePlaybackClock(TimeSpan elapsed) : S.Media.Core.Clock.IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; } = elapsed;
        public bool IsAdvancing => true;
    }
}
