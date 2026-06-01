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
