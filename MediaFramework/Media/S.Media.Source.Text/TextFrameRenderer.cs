using System.Text;
using S.Media.Core;
using S.Media.Core.Video;
using SkiaSharp;

namespace S.Media.Source.Text;

/// <summary>
/// Renders a <see cref="TextSourceSpec"/> to a BGRA <see cref="VideoFrame"/> with SkiaSharp (CPU raster — works
/// off the UI thread and headless). Supports a background box, word wrap, H/V alignment, bold/italic, and an
/// outline. Falls back to the default typeface when the requested family is missing. Alignment codes: HAlign
/// 0=left/1=center/2=right, VAlign 0=top/1=middle/2=bottom.
/// </summary>
public static class TextFrameRenderer
{
    public static VideoFrame? Render(TextSourceSpec spec, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(spec);
        try
        {
            var width = Math.Clamp(spec.CanvasWidth, 16, 7680);
            var height = Math.Clamp(spec.CanvasHeight, 16, 4320);
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);

                var bg = ToColor(spec.BackgroundArgb);
                if (bg.Alpha > 0)
                {
                    using var bgPaint = new SKPaint { Color = bg, IsAntialias = false, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(0, 0, width, height, bgPaint);
                }

                using var font = BuildFont(spec);

                var padding = (float)Math.Max(0, spec.PaddingPx);
                var maxTextWidth = spec.WrapWidthFraction > 0
                    ? (float)(width * Math.Clamp(spec.WrapWidthFraction, 0.05, 1.0)) - padding * 2
                    : width * 100f; // effectively no wrap (still honours explicit newlines)
                var lines = WrapLines(spec.Text ?? string.Empty, font, Math.Max(1f, maxTextWidth));

                var lineHeight = font.Spacing;
                var metrics = font.Metrics;
                var blockHeight = lines.Count * lineHeight;

                var firstBaseline = spec.VAlign switch
                {
                    0 => padding - metrics.Ascent,                              // top
                    2 => height - padding - blockHeight - metrics.Ascent,       // bottom
                    _ => (height - blockHeight) / 2f - metrics.Ascent,          // middle
                };

                using var fill = new SKPaint { Color = ToColor(spec.ColorArgb), IsAntialias = true, Style = SKPaintStyle.Fill };
                using var stroke = new SKPaint
                {
                    Color = ToColor(spec.OutlineArgb),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)Math.Max(0, spec.OutlineWidthPx),
                    StrokeJoin = SKStrokeJoin.Round,
                };

                var align = spec.HAlign switch
                {
                    0 => SKTextAlign.Left,
                    2 => SKTextAlign.Right,
                    _ => SKTextAlign.Center,
                };
                var x = spec.HAlign switch
                {
                    0 => padding,
                    2 => width - padding,
                    _ => width / 2f,
                };

                var baseline = firstBaseline;
                foreach (var line in lines)
                {
                    if (spec.OutlineWidthPx > 0 && stroke.Color.Alpha > 0)
                        canvas.DrawText(line, x, baseline, align, font, stroke);
                    canvas.DrawText(line, x, baseline, align, font, fill);
                    baseline += lineHeight;
                }

                canvas.Flush();
            }

            // Render at the full canvas size (fixed). A variable-size frame scaled unpredictably under the
            // placement's Cover fit and broke live edits (which need a stable frame size to swap onto the running
            // pipeline). The text is sized by FontSizePx and positioned by alignment within this fixed frame.
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

    /// <summary>The tight bounding box of the rendered text as fractions (0..1, x/y/w/h) of the cue's canvas — so a
    /// placement editor can outline where the actual text sits inside the placed (full-canvas) frame. Mirrors
    /// <see cref="Render"/>'s layout. Null on failure or empty text.</summary>
    public static (double X, double Y, double W, double H)? MeasureNormalizedBounds(TextSourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        try
        {
            var width = Math.Clamp(spec.CanvasWidth, 16, 7680);
            var height = Math.Clamp(spec.CanvasHeight, 16, 4320);
            if (MeasureBlockPixel(spec, width, height) is not { } b)
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

    private static SKFont BuildFont(TextSourceSpec spec)
    {
        var weight = spec.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = spec.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var typeface = SKTypeface.FromFamilyName(spec.FontFamily, weight, SKFontStyleWidth.Normal, slant) ?? SKTypeface.Default;
        return new SKFont(typeface, (float)Math.Clamp(spec.FontSizePx, 4, 2000)) { Edging = SKFontEdging.Antialias };
    }

    private static (float Left, float Top, float Right, float Bottom)? MeasureBlockPixel(TextSourceSpec spec, int width, int height)
    {
        using var font = BuildFont(spec);
        var padding = (float)Math.Max(0, spec.PaddingPx);
        var maxTextWidth = spec.WrapWidthFraction > 0
            ? (float)(width * Math.Clamp(spec.WrapWidthFraction, 0.05, 1.0)) - padding * 2
            : width * 100f;
        var lines = WrapLines(spec.Text ?? string.Empty, font, Math.Max(1f, maxTextWidth));

        var lineHeight = font.Spacing;
        var metrics = font.Metrics;
        var blockHeight = lines.Count * lineHeight;
        var firstBaseline = spec.VAlign switch
        {
            0 => padding - metrics.Ascent,
            2 => height - padding - blockHeight - metrics.Ascent,
            _ => (height - blockHeight) / 2f - metrics.Ascent,
        };

        var maxLineWidth = 0f;
        foreach (var line in lines)
            maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(line));
        if (maxLineWidth <= 0f)
            return null; // no visible text

        var top = firstBaseline + metrics.Ascent;                                  // ascent is negative
        var bottom = firstBaseline + (lines.Count - 1) * lineHeight + metrics.Descent;
        var left = spec.HAlign switch
        {
            0 => padding,
            2 => width - padding - maxLineWidth,
            _ => width / 2f - maxLineWidth / 2f,
        };
        return (left, top, left + maxLineWidth, bottom);
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
