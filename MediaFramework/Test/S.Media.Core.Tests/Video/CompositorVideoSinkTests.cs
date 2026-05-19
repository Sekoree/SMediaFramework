using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class CompositorVideoSinkTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void TwoSlots_RoundTripsThroughCompositor()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var sink = new CompositorVideoSink(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slotA = sink.AddSlot("A");
        var slotB = sink.AddSlot("B");

        Assert.Equal(2, sink.Slots.Count);

        slotA.Sink.Configure(Bgra32_4x4);
        slotB.Sink.Configure(Bgra32_4x4);
        slotA.Sink.Submit(MakeFrame(0, 0, 255, 255)); // red
        slotB.Opacity = 0.5f;
        slotB.Sink.Submit(MakeFrame(255, 0, 0, 255)); // blue at half opacity

        Assert.True(sink.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            // SourceOver (slotB is default): blue * 0.5 over red.
            // src.rgb*opacity = (127,0,0). (1 - 0.5)=0.5. dst*0.5 = (0,0,127.5).
            // sum: (127, 0, 127).
            Assert.InRange(span[0], 126, 130);
            Assert.Equal(0, span[1]);
            Assert.InRange(span[2], 126, 130);
            Assert.True(composite.PresentationTime >= TimeSpan.Zero);
        }
        finally { composite.Dispose(); }
        Assert.Equal(1, sink.CompositesEmitted);
    }

    [Fact]
    public void LatestWins_DropsOldFrameAndCountsOverflow()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var sink = new CompositorVideoSink(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = sink.AddSlot();
        slot.Sink.Configure(Bgra32_4x4);

        // Submit twice without reading — second submit replaces first.
        slot.Sink.Submit(MakeFrame(255, 0, 0, 255)); // blue
        slot.Sink.Submit(MakeFrame(0, 0, 255, 255)); // red (should be the one composited)
        Assert.Equal(1, slot.OverflowFrames);

        Assert.True(sink.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(255, span[2]);
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void EmptySlot_ContributesTransparency()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var sink = new CompositorVideoSink(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        sink.AddSlot();
        sink.AddSlot();
        // No submits — every slot is empty.
        Assert.True(sink.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            for (var i = 0; i < span.Length; i++)
                Assert.Equal(0, span[i]);
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void PtsAdvancesPerRead()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var sink = new CompositorVideoSink(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        sink.AddSlot();

        Assert.True(sink.TryReadNextFrame(out var f1));
        var p1 = f1.PresentationTime;
        f1.Dispose();
        Assert.True(sink.TryReadNextFrame(out var f2));
        var p2 = f2.PresentationTime;
        f2.Dispose();
        Assert.True(p2 > p1, $"second PTS {p2} should exceed first {p1}");
    }

    [Fact]
    public void Slot_RejectsUnacceptedPixelFormat()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var sink = new CompositorVideoSink(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = sink.AddSlot();
        var nv12Format = new VideoFormat(4, 4, PixelFormat.Nv12, new Rational(30, 1));
        Assert.Throws<ArgumentException>(() => slot.Sink.Configure(nv12Format));
    }

    [Fact]
    public void Dispose_DisposesHeldFramesAndCompositor()
    {
        var compositor = new TrackingCompositor();
        var sink = new CompositorVideoSink(compositor.OutputFormat, compositor, disposeCompositorOnDispose: true);
        var slot = sink.AddSlot();
        slot.Sink.Configure(compositor.OutputFormat);
        var disposed = false;
        var frame = new VideoFrame(TimeSpan.Zero, compositor.OutputFormat,
            new byte[4 * 4 * 4], 4 * 4, release: () => disposed = true);
        slot.Sink.Submit(frame);

        sink.Dispose();
        Assert.True(disposed, "held frame should have been disposed");
        Assert.True(compositor.IsDisposed);
    }

    private static VideoFrame MakeFrame(byte b, byte g, byte r, byte a)
    {
        var buf = new byte[4 * 4 * 4];
        for (var i = 0; i < 4 * 4; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return new VideoFrame(TimeSpan.Zero, Bgra32_4x4, buf, 4 * 4, release: null);
    }

    private sealed class TrackingCompositor : IVideoCompositor
    {
        public bool IsDisposed { get; private set; }
        public VideoFormat OutputFormat { get; } = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public void Configure(VideoFormat output) { }
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) =>
            new(pts, OutputFormat, new byte[4 * 4 * 4], 4 * 4, release: null);
        public void Dispose() => IsDisposed = true;
    }
}
