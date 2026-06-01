using S.Media.Core.Video;
using S.Media.SkiaSharp;
using Xunit;

namespace S.Media.SkiaSharp.Tests;

public sealed class TextLayerSourceTests
{
    [Fact]
    public void DefaultTransparentBackground_TextProducesNonZeroPixels()
    {
        using var src = new TextLayerSource(
            width: 128, height: 48, frameRate: new Rational(30, 1),
            text: "HELLO", fontFamily: "sans-serif", fontSize: 24f,
            argbColor: 0xFFFFFFFF /* opaque white */);

        Assert.Equal(PixelFormat.Bgra32, src.Format.PixelFormat);
        Assert.Equal(128, src.Format.Width);
        Assert.Equal(48, src.Format.Height);

        Assert.True(src.TryReadNextFrame(out var frame));
        try
        {
            var plane = frame.Planes[0].Span;
            var nonZero = 0;
            for (var i = 0; i < plane.Length; i += 4)
            {
                // Background is transparent (alpha = 0). Text pixels are opaque white.
                if (plane[i + 3] > 0) nonZero++;
            }
            Assert.True(nonZero > 0, "expected at least some opaque text pixels");
        }
        finally { frame.Dispose(); }
    }

    [Fact]
    public void ChangingText_MarksDirty_NextFrameDiffers()
    {
        using var src = new TextLayerSource(64, 32, new Rational(30, 1),
            text: "A", fontFamily: "sans-serif", fontSize: 16f, argbColor: 0xFFFFFFFF);

        Assert.True(src.TryReadNextFrame(out var f1));
        var snapshot = f1.Planes[0].ToArray();
        f1.Dispose();

        src.Text = "WW"; // wider; should change the rasterised pixels noticeably.
        Assert.True(src.TryReadNextFrame(out var f2));
        var snapshot2 = f2.Planes[0].ToArray();
        f2.Dispose();

        var differing = 0;
        for (var i = 0; i < snapshot.Length; i++)
            if (snapshot[i] != snapshot2[i]) differing++;
        Assert.True(differing > 0, "expected the rasterised output to change after text update");
    }

    [Fact]
    public void HeldFrame_NotMutated_ByLaterTextChange()
    {
        // Regression: emitted frames must reference an immutable generation buffer. Changing the text
        // rasterises a NEW generation; a previously emitted frame still held by a consumer must not
        // change underneath it (the old code re-rasterised into the one shared buffer in place).
        using var src = new TextLayerSource(64, 32, new Rational(30, 1),
            text: "A", fontFamily: "sans-serif", fontSize: 16f, argbColor: 0xFFFFFFFF);

        Assert.True(src.TryReadNextFrame(out var held));
        try
        {
            var before = held.Planes[0].ToArray();

            src.Text = "WWWW"; // wider text → different rasterisation
            Assert.True(src.TryReadNextFrame(out var f2));
            f2.Dispose();

            var after = held.Planes[0].ToArray();
            Assert.Equal(before, after); // the held frame's pixels are stable across the re-rasterise
        }
        finally { held.Dispose(); }
    }

    [Fact]
    public void HeldFrame_RemainsReadable_AfterSourceDispose()
    {
        // Disposing the source must drop only the source's reference; a frame still in flight keeps the
        // generation buffer alive (the old code returned the shared buffer to the pool on Dispose).
        var src = new TextLayerSource(48, 24, new Rational(30, 1),
            text: "Z", fontFamily: "sans-serif", fontSize: 12f, argbColor: 0xFFFFFFFF);
        Assert.True(src.TryReadNextFrame(out var held));
        var before = held.Planes[0].ToArray();

        src.Dispose();

        var after = held.Planes[0].ToArray();
        Assert.Equal(before, after);
        held.Dispose(); // returns the buffer to the pool now that the last reference is gone
    }

    [Fact]
    public void PtsAdvancesAtFrameRate()
    {
        using var src = new TextLayerSource(32, 32, new Rational(30, 1),
            text: ".", fontFamily: "sans-serif", fontSize: 8f, argbColor: 0xFFFFFFFF);

        Assert.True(src.TryReadNextFrame(out var f1));
        var pts1 = f1.PresentationTime;
        f1.Dispose();
        Assert.True(src.TryReadNextFrame(out var f2));
        var pts2 = f2.PresentationTime;
        f2.Dispose();

        var delta = pts2 - pts1;
        // 30 FPS → ~33.33ms per frame.
        Assert.InRange(delta.TotalMilliseconds, 32.0, 35.0);
    }

    [Fact]
    public void Dispose_StopsEmitting()
    {
        var src = new TextLayerSource(32, 32, new Rational(30, 1),
            text: ".", fontFamily: "sans-serif", fontSize: 8f, argbColor: 0xFFFFFFFF);
        src.Dispose();
        Assert.True(src.IsExhausted);
        Assert.False(src.TryReadNextFrame(out _));
    }
}
