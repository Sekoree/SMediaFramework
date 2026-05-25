using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class CutVideoSourceTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void BeforeCut_EmitsFromA()
    {
        var a = new ColoredSource(Bgra32_4x4, color: 11);
        var b = new ColoredSource(Bgra32_4x4, color: 22);
        using var cut = new CutVideoSource(a, b, TimeSpan.FromMilliseconds(100));

        Assert.True(cut.TryReadNextFrame(out var f));
        try
        {
            Assert.Equal(11, f.Planes[0].Span[0]);
            Assert.True(f.PresentationTime < TimeSpan.FromMilliseconds(100));
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public void AtCut_DropsAFrameSwitchesToB_RewritesPtsToCutBoundary()
    {
        // A emits frames with PTS 0, 33ms, 66ms, 99ms, 132ms — at 132ms we hit the cut at 100ms.
        var a = new ColoredSource(Bgra32_4x4, color: 11);
        var b = new ColoredSource(Bgra32_4x4, color: 22);
        using var cut = new CutVideoSource(a, b, TimeSpan.FromMilliseconds(100));

        // Skip past A frames until we hit the cut.
        VideoFrame f;
        do
        {
            Assert.True(cut.TryReadNextFrame(out f));
            if (f.Planes[0].Span[0] == 22) break;
            f.Dispose();
        } while (true);

        try
        {
            Assert.Equal(22, f.Planes[0].Span[0]);
            // First B frame's PTS is rewritten to cutAt.
            Assert.Equal(TimeSpan.FromMilliseconds(100), f.PresentationTime);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public void Constructor_RejectsMismatchedFormats()
    {
        var a = new ColoredSource(Bgra32_4x4, color: 11);
        var differentFmt = new VideoFormat(8, 8, PixelFormat.Bgra32, new Rational(30, 1));
        var b = new ColoredSource(differentFmt, color: 22);
        Assert.Throws<ArgumentException>(() => new CutVideoSource(a, b, TimeSpan.FromMilliseconds(50)));
    }

    private sealed class ColoredSource(VideoFormat format, byte color) : IVideoSource, IDisposable
    {
        private int _i;
        private readonly TimeSpan _step = TimeSpan.FromMilliseconds(33);
        public VideoFormat Format => format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { format.PixelFormat };
        public bool IsExhausted => false;
        public void SelectOutputFormat(PixelFormat _) { }
        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var plane = new byte[format.Width * format.Height * 4];
            for (var i = 0; i < plane.Length; i++) plane[i] = color;
            var pts = TimeSpan.FromTicks(_step.Ticks * _i++);
            frame = new VideoFrame(pts, format, plane, format.Width * 4, release: null);
            return true;
        }
        public void Dispose() { }
    }
}
