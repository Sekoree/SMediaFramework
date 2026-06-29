using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class TextFrameRendererTests
{
    [Fact]
    public void Render_OpaqueBackground_ProducesBgraFrameOfRequestedSize()
    {
        var text = new TextPlaylistItem
        {
            Text = "Hello world",
            CanvasWidth = 320,
            CanvasHeight = 180,
            FontSizePx = 48,
            ColorArgb = 0xFFFFFFFF,
            BackgroundArgb = 0xFF112233, // opaque
        };

        var frame = TextFrameRenderer.Render(text, new Rational(30, 1));

        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(320, frame!.Format.Width);
            Assert.Equal(180, frame.Format.Height);
            Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);
            // Opaque background means the top-left pixel is fully opaque (alpha at byte 3).
            Assert.Equal(255, frame.Planes[0].Span[3]);
        }
    }

    [Fact]
    public void Render_TransparentBackground_LeavesEmptyAreasTransparent()
    {
        var text = new TextPlaylistItem
        {
            Text = ".",
            CanvasWidth = 64,
            CanvasHeight = 64,
            FontSizePx = 12,
            HAlign = TextAlignH.Left,
            VAlign = TextAlignV.Top,
            BackgroundArgb = 0x00000000, // transparent
        };

        var frame = TextFrameRenderer.Render(text, new Rational(30, 1));

        Assert.NotNull(frame);
        using (frame)
        {
            // The bottom-right pixel is far from a small top-left glyph, so it stays transparent.
            var span = frame!.Planes[0].Span;
            var lastPixelAlpha = span[(64 * 64 - 1) * 4 + 3];
            Assert.Equal(0, lastPixelAlpha);
        }
    }
}
