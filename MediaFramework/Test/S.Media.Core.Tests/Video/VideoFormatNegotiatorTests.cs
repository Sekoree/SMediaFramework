using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoFormatNegotiatorTests
{
    [Fact]
    public void Negotiate_PrefersNativeWhenSinkAccepts()
    {
        var src = new FakeSource([PixelFormat.I420, PixelFormat.Nv12]);
        var sink = new FakeSink([PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420]);

        // Walks sink preferences in order — Nv12 is the first overlap.
        Assert.Equal(PixelFormat.Nv12, VideoFormatNegotiator.Negotiate(src, sink));
    }

    [Fact]
    public void Negotiate_FallsBackToSinkFirstChoiceWhenNoOverlap()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var sink = new FakeSink([PixelFormat.Bgra32, PixelFormat.Rgba32]);

        Assert.Equal(PixelFormat.Bgra32, VideoFormatNegotiator.Negotiate(src, sink));
    }

    [Fact]
    public void Negotiate_SinkAcceptsAnything_ReturnsSourceFirstNative()
    {
        var src = new FakeSource([PixelFormat.Nv12, PixelFormat.I420]);
        var sink = new FakeSink([]);

        Assert.Equal(PixelFormat.Nv12, VideoFormatNegotiator.Negotiate(src, sink));
    }

    [Fact]
    public void Negotiate_BothEmpty_Throws()
    {
        var src = new FakeSource([]);
        var sink = new FakeSink([]);

        Assert.Throws<InvalidOperationException>(() => VideoFormatNegotiator.Negotiate(src, sink));
    }

    [Fact]
    public void Connect_SelectsAndConfigures()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var sink = new FakeSink([PixelFormat.I420]);

        var fmt = VideoFormatNegotiator.Connect(src, sink);

        Assert.Equal(PixelFormat.I420, fmt.PixelFormat);
        Assert.Equal(PixelFormat.I420, src.SelectedFormat);
        Assert.Equal(PixelFormat.I420, sink.ConfiguredFormat?.PixelFormat);
    }

    [Fact]
    public void Negotiate_WithFilter_SkipsFirstSinkPreference_InOverlap()
    {
        var src = new FakeSource([PixelFormat.I420, PixelFormat.Nv12]);
        var sink = new FakeSink([PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420]);

        var pf = VideoFormatNegotiator.Negotiate(src, sink, pf => pf != PixelFormat.Nv12);
        Assert.Equal(PixelFormat.I420, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_FallbackToFilteredSinkPreferenceWhenNoNativeOverlap()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var sink = new FakeSink([PixelFormat.Bgra32, PixelFormat.Rgba32]);

        var pf = VideoFormatNegotiator.Negotiate(src, sink, pf => pf == PixelFormat.Rgba32);
        Assert.Equal(PixelFormat.Rgba32, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_SinkAnything_PicksFilteredNativeSecond()
    {
        var src = new FakeSource([PixelFormat.Bgra32, PixelFormat.Nv12]);
        var sink = new FakeSink([]);

        var pf = VideoFormatNegotiator.Negotiate(src, sink, pf => pf == PixelFormat.Nv12);
        Assert.Equal(PixelFormat.Nv12, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_RejectsEverything_Throws()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var sink = new FakeSink([PixelFormat.Bgra32]);

        Assert.Throws<InvalidOperationException>(() =>
            VideoFormatNegotiator.Negotiate(src, sink, static _ => false));
    }

    [Fact]
    public void Negotiate_WithFilter_EmptySink_RejectsEveryNative_Throws()
    {
        var src = new FakeSource([PixelFormat.Bgra32, PixelFormat.I420]);
        var sink = new FakeSink([]);

        Assert.Throws<InvalidOperationException>(() =>
            VideoFormatNegotiator.Negotiate(src, sink, static _ => false));
    }

    [Fact]
    public void Connect_WiresD3D11GlBorrowWhenSourceAndSinkSupport()
    {
        var src = new FakeHardwareSource(PixelFormat.Nv12);
        var sink = new FakeBorrowSink(PixelFormat.Nv12);

        VideoFormatNegotiator.Connect(src, sink);

        Assert.Same(src, sink.LastBorrowSource);
    }

    [Fact]
    public void Connect_ClearsBorrowWhenSourceDoesNotExposeHardwareD3D11()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var sink = new FakeBorrowSink(PixelFormat.I420);

        VideoFormatNegotiator.Connect(src, sink);

        Assert.Null(sink.LastBorrowSource);
    }

    private sealed class FakeHardwareSource : FakeSource, IHardwareD3D11GlInteropSource
    {
        public FakeHardwareSource(params PixelFormat[] native) : base(native) { }

        public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr)
        {
            deviceComPtr = (nint)1;
            return true;
        }

        public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked)
        {
            adapterLuidPacked = 0;
            return false;
        }
    }

    private sealed class FakeBorrowSink : FakeSink, IVideoSinkD3D11GlBorrowSetup
    {
        public FakeBorrowSink(params PixelFormat[] accepted) : base(accepted) { }

        public IVideoSource? LastBorrowSource { get; private set; }

        public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
            LastBorrowSource = videoSource;
    }

    private class FakeSource(PixelFormat[] native) : IVideoSource
    {
        public VideoFormat Format { get; private set; } =
            new(1920, 1080, native.Length > 0 ? native[0] : PixelFormat.Unknown, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats => native;
        public bool IsExhausted => false;
        public PixelFormat? SelectedFormat { get; private set; }

        public void SelectOutputFormat(PixelFormat format)
        {
            SelectedFormat = format;
            Format = Format with { PixelFormat = format };
        }

        public bool TryReadNextFrame(out VideoFrame frame) { frame = null!; return false; }
    }

    private class FakeSink(PixelFormat[] accepted) : IVideoSink
    {
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => accepted;
        public VideoFormat? ConfiguredFormat { get; private set; }

        public void Configure(VideoFormat format)
        {
            ConfiguredFormat = format;
            Format = format;
        }

        public void Submit(VideoFrame frame) { }
    }
}
