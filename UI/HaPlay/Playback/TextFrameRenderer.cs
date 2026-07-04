using System.Text;
using HaPlay.Models;
using S.Media.Core.Video;
using SkiaSharp;

namespace HaPlay.Playback;

/// <summary>
/// Renders a <see cref="TextPlaylistItem"/> to a BGRA <see cref="VideoFrame"/> with SkiaSharp (CPU
/// raster — works off the UI thread and headless). Supports a background box, word wrap, H/V alignment,
/// bold/italic, and an outline. Falls back to the default typeface when the requested family is missing.
/// </summary>
internal static class TextFrameRenderer
{
    public static VideoFrame? Render(TextPlaylistItem text, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var width = Math.Clamp(text.CanvasWidth, 16, 7680);
            var height = Math.Clamp(text.CanvasHeight, 16, 4320);
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);

                var bg = ToColor(text.BackgroundArgb);
                if (bg.Alpha > 0)
                {
                    using var bgPaint = new SKPaint { Color = bg, IsAntialias = false, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(0, 0, width, height, bgPaint);
                }

                var weight = text.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                var slant = text.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                using var typeface =
                    SKTypeface.FromFamilyName(text.FontFamily, weight, SKFontStyleWidth.Normal, slant)
                    ?? SKTypeface.Default;
                using var font = new SKFont(typeface, (float)Math.Clamp(text.FontSizePx, 4, 2000))
                {
                    Edging = SKFontEdging.Antialias,
                };

                var padding = (float)Math.Max(0, text.PaddingPx);
                var maxTextWidth = text.WrapWidthFraction > 0
                    ? (float)(width * Math.Clamp(text.WrapWidthFraction, 0.05, 1.0)) - padding * 2
                    : width * 100f; // effectively no wrap (still honours explicit newlines)
                var lines = WrapLines(text.Text ?? string.Empty, font, Math.Max(1f, maxTextWidth));

                var lineHeight = font.Spacing;
                var metrics = font.Metrics;
                var blockHeight = lines.Count * lineHeight;

                var firstBaseline = text.VAlign switch
                {
                    TextAlignV.Top => padding - metrics.Ascent,
                    TextAlignV.Bottom => height - padding - blockHeight - metrics.Ascent,
                    _ => (height - blockHeight) / 2f - metrics.Ascent,
                };

                using var fill = new SKPaint { Color = ToColor(text.ColorArgb), IsAntialias = true, Style = SKPaintStyle.Fill };
                using var stroke = new SKPaint
                {
                    Color = ToColor(text.OutlineArgb),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)Math.Max(0, text.OutlineWidthPx),
                    StrokeJoin = SKStrokeJoin.Round,
                };

                var align = text.HAlign switch
                {
                    TextAlignH.Left => SKTextAlign.Left,
                    TextAlignH.Right => SKTextAlign.Right,
                    _ => SKTextAlign.Center,
                };
                var x = text.HAlign switch
                {
                    TextAlignH.Left => padding,
                    TextAlignH.Right => width - padding,
                    _ => width / 2f,
                };

                var baseline = firstBaseline;
                foreach (var line in lines)
                {
                    if (text.OutlineWidthPx > 0 && stroke.Color.Alpha > 0)
                        canvas.DrawText(line, x, baseline, align, font, stroke);
                    canvas.DrawText(line, x, baseline, align, font, fill);
                    baseline += lineHeight;
                }

                canvas.Flush();
            }

            // Render at the full CanvasWidth×CanvasHeight (fixed size). A previous attempt cropped the frame tight
            // to the text, but a variable-size frame (a) scales unpredictably under the placement's Cover fit (a
            // small tight frame blown up to fill the composition — text huge / out of bounds, and the size jumped
            // with every edit), and (b) broke the in-place live edit, which needs a stable frame size to swap onto
            // the running pipeline. The text is sized by FontSizePx and positioned by H/V alignment within this
            // fixed frame; the placement editor's outline shows where it sits.
            var stride = bmp.RowBytes;
            var bgra = bmp.Bytes; // an owned, tightly-packed BGRA copy (survives the bitmap's disposal)
            var fmt = new VideoFormat(width, height, PixelFormat.Bgra32, frameRate);
            return new VideoFrame(TimeSpan.Zero, fmt, bgra, stride, release: null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The tight pixel bounding box of the laid-out text block within a <paramref name="width"/>×
    /// <paramref name="height"/> canvas — mirrors <see cref="Render"/>'s layout (wrap, alignment, ascent/descent).
    /// Null for empty text. Used to crop the rendered frame to the text (so a text cue's frame is the text, not a
    /// mostly-empty canvas).</summary>
    private static (float Left, float Top, float Right, float Bottom)? MeasureBlockPixel(TextPlaylistItem text, int width, int height)
    {
        var weight = text.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = text.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        using var typeface =
            SKTypeface.FromFamilyName(text.FontFamily, weight, SKFontStyleWidth.Normal, slant) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)Math.Clamp(text.FontSizePx, 4, 2000));

        var padding = (float)Math.Max(0, text.PaddingPx);
        var maxTextWidth = text.WrapWidthFraction > 0
            ? (float)(width * Math.Clamp(text.WrapWidthFraction, 0.05, 1.0)) - padding * 2
            : width * 100f;
        var lines = WrapLines(text.Text ?? string.Empty, font, Math.Max(1f, maxTextWidth));

        var lineHeight = font.Spacing;
        var metrics = font.Metrics;
        var blockHeight = lines.Count * lineHeight;
        var firstBaseline = text.VAlign switch
        {
            TextAlignV.Top => padding - metrics.Ascent,
            TextAlignV.Bottom => height - padding - blockHeight - metrics.Ascent,
            _ => (height - blockHeight) / 2f - metrics.Ascent,
        };

        var maxLineWidth = 0f;
        foreach (var line in lines)
            maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(line));
        if (maxLineWidth <= 0f)
            return null; // no visible text

        var top = firstBaseline + metrics.Ascent;                                  // ascent is negative
        var bottom = firstBaseline + (lines.Count - 1) * lineHeight + metrics.Descent;
        var left = text.HAlign switch
        {
            TextAlignH.Left => padding,
            TextAlignH.Right => width - padding - maxLineWidth,
            _ => width / 2f - maxLineWidth / 2f,
        };
        return (left, top, left + maxLineWidth, bottom);
    }

    /// <summary>The tight bounding box of the rendered text as fractions (0..1, x/y/w/h) of the cue's canvas — so
    /// the placement editor can outline where the actual text sits inside the placed (full-canvas) frame. Mirrors
    /// <see cref="Render"/>'s layout. Null on failure or empty text.</summary>
    public static (double X, double Y, double W, double H)? MeasureNormalizedBounds(TextPlaylistItem text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var width = Math.Clamp(text.CanvasWidth, 16, 7680);
            var height = Math.Clamp(text.CanvasHeight, 16, 4320);
            if (MeasureBlockPixel(text, width, height) is not { } b)
                return null;
            var nx = Math.Clamp(b.Left, 0, width) / width;
            var ny = Math.Clamp(b.Top, 0, height) / height;
            var nw = (Math.Clamp(b.Right, 0, width) - Math.Clamp(b.Left, 0, width)) / width;
            var nh = (Math.Clamp(b.Bottom, 0, height) - Math.Clamp(b.Top, 0, height)) / height;
            return (nx, ny, Math.Max(0, nw), Math.Max(0, nh));
        }
        catch
        {
            return null;
        }
    }

    private static List<string> WrapLines(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = rawLine.Split(' ');
            var current = new StringBuilder();
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length == 0 || font.MeasureText(candidate) <= maxWidth)
                {
                    if (current.Length > 0) current.Append(' ');
                    current.Append(word);
                }
                else
                {
                    result.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
            }

            result.Add(current.ToString());
        }

        if (result.Count == 0)
            result.Add(string.Empty);
        return result;
    }

    private static SKColor ToColor(uint argb) =>
        new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
}
