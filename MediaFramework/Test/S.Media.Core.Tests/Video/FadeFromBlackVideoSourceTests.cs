using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class FadeFromBlackVideoSourceTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void FirstFrame_FullyDark_AlphaPreserved()
    {
        var inner = new FixedPtsSource(Bgra32_4x4, MakeSolid(100, 100, 100, 255), TimeSpan.Zero);
        using var fade = new FadeFromBlackVideoSource(inner, TimeSpan.FromMilliseconds(100));

        Assert.True(fade.TryReadNextFrame(out var f));
        try
        {
            var span = f.Planes[0].Span;
            // At elapsed = 0 → ramp = 0 → RGB multiplied by 0; alpha preserved.
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(0, span[2]);
            Assert.Equal(255, span[3]);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public void AfterDuration_PassthroughOriginal()
    {
        // First frame anchors elapsed=0. Read past the duration (50ms) — the third frame's elapsed
        // exceeds the fade window so the wrapper passes the inner frame through unchanged.
        var seq = new SequenceSource(Bgra32_4x4,
            [(TimeSpan.Zero, MakeSolid(50, 75, 200, 255)),
             (TimeSpan.FromMilliseconds(50), MakeSolid(50, 75, 200, 255)),
             (TimeSpan.FromMilliseconds(100), MakeSolid(50, 75, 200, 255))]);
        using var fade = new FadeFromBlackVideoSource(seq, TimeSpan.FromMilliseconds(50));

        Assert.True(fade.TryReadNextFrame(out var f1)); f1.Dispose();
        Assert.True(fade.TryReadNextFrame(out var f2)); f2.Dispose();
        Assert.True(fade.TryReadNextFrame(out var f3));
        try
        {
            var span = f3.Planes[0].Span;
            Assert.Equal(50, span[0]);
            Assert.Equal(75, span[1]);
            Assert.Equal(200, span[2]);
            Assert.Equal(255, span[3]);
        }
        finally { f3.Dispose(); }
    }

    [Fact]
    public void MidFade_ScalesRgbProportionally()
    {
        // First frame at PTS = 0; second frame at PTS = 50ms with 100ms fade → ramp = 0.5.
        var seq = new SequenceSource(Bgra32_4x4,
            [(TimeSpan.Zero, MakeSolid(80, 100, 120, 255)),
             (TimeSpan.FromMilliseconds(50), MakeSolid(80, 100, 120, 255))]);
        using var fade = new FadeFromBlackVideoSource(seq, TimeSpan.FromMilliseconds(100));

        Assert.True(fade.TryReadNextFrame(out var f1)); f1.Dispose();
        Assert.True(fade.TryReadNextFrame(out var f2));
        try
        {
            var span = f2.Planes[0].Span;
            Assert.InRange(span[0], 38, 42); // ~80 * 0.5
            Assert.InRange(span[1], 48, 52); // ~100 * 0.5
            Assert.InRange(span[2], 58, 62); // ~120 * 0.5
            Assert.Equal(255, span[3]);
        }
        finally { f2.Dispose(); }
    }

    [Fact]
    public void Constructor_RejectsYuvSource()
    {
        var nv12Format = new VideoFormat(4, 4, PixelFormat.Nv12, new Rational(30, 1));
        var inner = new FixedPtsSource(nv12Format, new byte[16], TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => new FadeFromBlackVideoSource(inner, TimeSpan.FromMilliseconds(100)));
    }

    private static byte[] MakeSolid(byte b, byte g, byte r, byte a)
    {
        var buf = new byte[4 * 4 * 4];
        for (var i = 0; i < 4 * 4; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private sealed class FixedPtsSource(VideoFormat format, byte[] plane, TimeSpan pts) : IVideoSource
    {
        public VideoFormat Format => format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { format.PixelFormat };
        public bool IsExhausted => false;
        public void SelectOutputFormat(PixelFormat _) { }
        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(pts, format, plane, format.Width * 4, release: null);
            return true;
        }
    }

    private sealed class SequenceSource(VideoFormat format, IReadOnlyList<(TimeSpan, byte[])> frames) : IVideoSource
    {
        private int _i;
        public VideoFormat Format => format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { format.PixelFormat };
        public bool IsExhausted => _i >= frames.Count;
        public void SelectOutputFormat(PixelFormat _) { }
        public bool TryReadNextFrame(out VideoFrame frame)
        {
            if (_i >= frames.Count) { frame = null!; return false; }
            var (pts, plane) = frames[_i++];
            frame = new VideoFrame(pts, format, plane, format.Width * 4, release: null);
            return true;
        }
    }
}
