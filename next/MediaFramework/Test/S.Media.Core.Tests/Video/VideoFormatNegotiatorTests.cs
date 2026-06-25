using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoFormatNegotiatorTests
{
    [Fact]
    public void Negotiate_PrefersNativeWhenOutputAccepts()
    {
        var src = new FakeSource([PixelFormat.I420, PixelFormat.Nv12]);
        var output = new FakeOutput([PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420]);

        // Walks output preferences in order — Nv12 is the first overlap.
        Assert.Equal(PixelFormat.Nv12, VideoFormatNegotiator.Negotiate(src, output));
    }

    [Fact]
    public void Negotiate_FallsBackToOutputFirstChoiceWhenNoOverlap()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var output = new FakeOutput([PixelFormat.Bgra32, PixelFormat.Rgba32]);

        Assert.Equal(PixelFormat.Bgra32, VideoFormatNegotiator.Negotiate(src, output));
    }

    [Fact]
    public void Negotiate_OutputAcceptsAnything_ReturnsSourceFirstNative()
    {
        var src = new FakeSource([PixelFormat.Nv12, PixelFormat.I420]);
        var output = new FakeOutput([]);

        Assert.Equal(PixelFormat.Nv12, VideoFormatNegotiator.Negotiate(src, output));
    }

    [Fact]
    public void Negotiate_BothEmpty_Throws()
    {
        var src = new FakeSource([]);
        var output = new FakeOutput([]);

        Assert.Throws<InvalidOperationException>(() => VideoFormatNegotiator.Negotiate(src, output));
    }

    [Fact]
    public void Connect_SelectsAndConfigures()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var output = new FakeOutput([PixelFormat.I420]);

        var fmt = VideoFormatNegotiator.Connect(src, output);

        Assert.Equal(PixelFormat.I420, fmt.PixelFormat);
        Assert.Equal(PixelFormat.I420, src.SelectedFormat);
        Assert.Equal(PixelFormat.I420, output.ConfiguredFormat?.PixelFormat);
    }

    [Fact]
    public void Negotiate_WithFilter_SkipsFirstOutputPreference_InOverlap()
    {
        var src = new FakeSource([PixelFormat.I420, PixelFormat.Nv12]);
        var output = new FakeOutput([PixelFormat.Bgra32, PixelFormat.Nv12, PixelFormat.I420]);

        var pf = VideoFormatNegotiator.Negotiate(src, output, pf => pf != PixelFormat.Nv12);
        Assert.Equal(PixelFormat.I420, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_FallbackToFilteredOutputPreferenceWhenNoNativeOverlap()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var output = new FakeOutput([PixelFormat.Bgra32, PixelFormat.Rgba32]);

        var pf = VideoFormatNegotiator.Negotiate(src, output, pf => pf == PixelFormat.Rgba32);
        Assert.Equal(PixelFormat.Rgba32, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_OutputAnything_PicksFilteredNativeSecond()
    {
        var src = new FakeSource([PixelFormat.Bgra32, PixelFormat.Nv12]);
        var output = new FakeOutput([]);

        var pf = VideoFormatNegotiator.Negotiate(src, output, pf => pf == PixelFormat.Nv12);
        Assert.Equal(PixelFormat.Nv12, pf);
    }

    [Fact]
    public void Negotiate_WithFilter_RejectsEverything_Throws()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var output = new FakeOutput([PixelFormat.Bgra32]);

        Assert.Throws<InvalidOperationException>(() =>
            VideoFormatNegotiator.Negotiate(src, output, static _ => false));
    }

    [Fact]
    public void Negotiate_WithFilter_EmptyOutput_RejectsEveryNative_Throws()
    {
        var src = new FakeSource([PixelFormat.Bgra32, PixelFormat.I420]);
        var output = new FakeOutput([]);

        Assert.Throws<InvalidOperationException>(() =>
            VideoFormatNegotiator.Negotiate(src, output, static _ => false));
    }

    [Fact]
    public void Connect_WiresD3D11GlBorrowWhenSourceAndOutputSupport()
    {
        var src = new FakeHardwareSource(PixelFormat.Nv12);
        var output = new FakeBorrowOutput(PixelFormat.Nv12);

        VideoFormatNegotiator.Connect(src, output);

        Assert.Same(src, output.LastBorrowSource);
    }

    [Fact]
    public void Connect_ClearsBorrowWhenSourceDoesNotExposeHardwareD3D11()
    {
        var src = new FakeSource([PixelFormat.I420]);
        var output = new FakeBorrowOutput(PixelFormat.I420);

        VideoFormatNegotiator.Connect(src, output);

        Assert.Null(output.LastBorrowSource);
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

    private sealed class FakeBorrowOutput : FakeOutput, IVideoOutputD3D11GlBorrowSetup
    {
        public FakeBorrowOutput(params PixelFormat[] accepted) : base(accepted) { }

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

    private class FakeOutput(PixelFormat[] accepted) : IVideoOutput
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
