using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class YadifDeinterlacerTests
{
    [Fact]
    public void ProgressiveInput_PassesThrough()
    {
        FFmpegRuntime.EnsureInitialized();
        var fmt = new VideoFormat(64, 64, PixelFormat.I420, new Rational(25, 1));
        using var deint = new YadifDeinterlacer(fmt);

        var y = new byte[64 * 64];
        var u = new byte[32 * 32];
        var v = new byte[32 * 32];
        var planes = new ReadOnlyMemory<byte>[] { y, u, v };
        var strides = new[] { 64, 32, 32 };
        var frame = new VideoFrame(TimeSpan.Zero, fmt, planes, strides, release: null);

        Span<VideoFrame?> outs = new VideoFrame?[2];
        var n = deint.Process(frame, outs);
        Assert.Equal(1, n);
        Assert.Same(frame, outs[0]);
        frame.Dispose();
    }

    [Fact]
    public void Interlaced_I420_Emits_AtLeastOneProgressiveOutput()
    {
        FFmpegRuntime.EnsureInitialized();
        var fmt = new VideoFormat(64, 64, PixelFormat.I420, new Rational(25, 1));
        using var deint = new YadifDeinterlacer(fmt);

        var y = new byte[64 * 64];
        for (var i = 0; i < y.Length; i++) y[i] = (byte)(((i / 64) & 1) == 0 ? 200 : 50);
        var u = new byte[32 * 32];
        Array.Fill(u, (byte)128);
        var v = new byte[32 * 32];
        Array.Fill(v, (byte)128);
        var planes = new ReadOnlyMemory<byte>[] { y, u, v };
        var strides = new[] { 64, 32, 32 };
        var frame = new VideoFrame(TimeSpan.Zero, fmt, planes, strides, release: null,
            metadata: new VideoFrameMetadata(FieldOrder: VideoFieldOrder.TopFieldFirst));

        // Yadif mode=0 needs to see at least two interlaced frames before emitting (it analyses motion
        // against the previous frame). Push two and confirm we eventually get a progressive output.
        Span<VideoFrame?> outs = new VideoFrame?[2];
        var totalEmitted = 0;
        for (var i = 0; i < 3 && totalEmitted == 0; i++)
        {
            var f2 = new VideoFrame(TimeSpan.FromMilliseconds(40 * i), fmt, planes, strides, release: null,
                metadata: new VideoFrameMetadata(FieldOrder: VideoFieldOrder.TopFieldFirst));
            var n = deint.Process(f2, outs);
            totalEmitted += n;
            for (var j = 0; j < n; j++) outs[j]?.Dispose();
            f2.Dispose();
        }
        frame.Dispose();

        Assert.True(totalEmitted > 0, "yadif should have emitted at least one progressive frame within 3 pushes");
    }

    [Fact]
    public void Configure_RejectsUnsupportedFormat()
    {
        FFmpegRuntime.EnsureInitialized();
        var fmt = new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(25, 1));
        Assert.Throws<ArgumentException>(() => new YadifDeinterlacer(fmt));
    }

    [Fact]
    public void Configure_AcceptsYuv422P_And_EmitsProgressive()
    {
        FFmpegRuntime.EnsureInitialized();
        // 4:2:2 layout: chroma is half-width, full-height.
        var fmt = new VideoFormat(64, 64, PixelFormat.Yuv422P, new Rational(25, 1));
        using var deint = new YadifDeinterlacer(fmt);
        Assert.Equal(PixelFormat.Yuv422P, deint.OutputFormat.PixelFormat);

        var y = new byte[64 * 64];
        for (var i = 0; i < y.Length; i++) y[i] = (byte)(((i / 64) & 1) == 0 ? 200 : 50);
        var u = new byte[32 * 64];
        Array.Fill(u, (byte)128);
        var v = new byte[32 * 64];
        Array.Fill(v, (byte)128);
        var planes = new ReadOnlyMemory<byte>[] { y, u, v };
        var strides = new[] { 64, 32, 32 };

        Span<VideoFrame?> outs = new VideoFrame?[2];
        var totalEmitted = 0;
        for (var i = 0; i < 3 && totalEmitted == 0; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(40 * i), fmt, planes, strides, release: null,
                metadata: new VideoFrameMetadata(FieldOrder: VideoFieldOrder.TopFieldFirst));
            var n = deint.Process(f, outs);
            totalEmitted += n;
            for (var j = 0; j < n; j++)
            {
                // Confirm the deinterlacer preserved 4:2:2 round-trip (still 3 planes).
                Assert.Equal(PixelFormat.Yuv422P, outs[j]!.Format.PixelFormat);
                Assert.Equal(3, outs[j]!.Planes.Length);
                outs[j]?.Dispose();
            }
            f.Dispose();
        }
        Assert.True(totalEmitted > 0, "yadif should have emitted a 4:2:2 progressive frame within 3 pushes");
    }

    [Fact]
    public void Configure_AcceptsYuv444P_And_EmitsProgressive()
    {
        FFmpegRuntime.EnsureInitialized();
        // 4:4:4 layout: chroma is full-width, full-height.
        var fmt = new VideoFormat(64, 64, PixelFormat.Yuv444P, new Rational(25, 1));
        using var deint = new YadifDeinterlacer(fmt);
        Assert.Equal(PixelFormat.Yuv444P, deint.OutputFormat.PixelFormat);

        var y = new byte[64 * 64];
        for (var i = 0; i < y.Length; i++) y[i] = (byte)(((i / 64) & 1) == 0 ? 200 : 50);
        var u = new byte[64 * 64];
        Array.Fill(u, (byte)128);
        var v = new byte[64 * 64];
        Array.Fill(v, (byte)128);
        var planes = new ReadOnlyMemory<byte>[] { y, u, v };
        var strides = new[] { 64, 64, 64 };

        Span<VideoFrame?> outs = new VideoFrame?[2];
        var totalEmitted = 0;
        for (var i = 0; i < 3 && totalEmitted == 0; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(40 * i), fmt, planes, strides, release: null,
                metadata: new VideoFrameMetadata(FieldOrder: VideoFieldOrder.TopFieldFirst));
            var n = deint.Process(f, outs);
            totalEmitted += n;
            for (var j = 0; j < n; j++)
            {
                Assert.Equal(PixelFormat.Yuv444P, outs[j]!.Format.PixelFormat);
                Assert.Equal(3, outs[j]!.Planes.Length);
                outs[j]?.Dispose();
            }
            f.Dispose();
        }
        Assert.True(totalEmitted > 0, "yadif should have emitted a 4:4:4 progressive frame within 3 pushes");
    }
}
