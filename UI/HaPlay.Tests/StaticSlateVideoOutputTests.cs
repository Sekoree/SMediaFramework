using HaPlay.Playback;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class StaticSlateVideoOutputTests
{
    [Fact]
    public void Submit_UsesIndependentFrame_AndKeepsBorrowedOutputAlive()
    {
        var format = new VideoFormat(2, 1, PixelFormat.Bgra32, new Rational(30, 1));
        var templateReleased = 0;
        var template = new VideoFrame(
            TimeSpan.Zero,
            format,
            [new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }],
            [8],
            release: new CallbackDisposable(() => Interlocked.Increment(ref templateReleased)));
        var output = new HoldingVideoOutput();
        var slate = new StaticSlateVideoOutput(output);

        slate.Configure(format);
        slate.SetTemplate(template);
        slate.Submit();

        Assert.NotNull(output.Submitted);
        var submitted = output.Submitted!;
        Assert.NotSame(template, submitted);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, submitted.Planes[0].ToArray());
        Assert.Equal(0, templateReleased);

        slate.Dispose();

        Assert.Equal(1, templateReleased);
        Assert.Equal(0, output.DisposeCalls); // the output is borrowed and has a separate owner
        Assert.Equal((byte)1, submitted.Planes[0].Span[0]); // submitted copy remains owned by the output
        submitted.Dispose();
    }

    private sealed class HoldingVideoOutput : IVideoOutput, IDisposable
    {
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [PixelFormat.Bgra32];
        public VideoFrame? Submitted { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame) => Submitted = frame;

        public void Dispose() => DisposeCalls++;
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}
