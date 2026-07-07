using HaPlay.Models;
using S.Media.Source.Text;

namespace HaPlay.Playback;

/// <summary>
/// SESSION-02 boundary: maps HaPlay's UI-side <see cref="TextPlaylistItem"/> onto the framework's portable
/// <see cref="TextSourceSpec"/> (the flat, host-agnostic render params the <c>S.Media.Source.Text</c> module
/// consumes). Alignment enums flatten to their integer codes (Left/Top = 0, Center/Middle = 1, Right/Bottom = 2).
/// </summary>
internal static class TextSourceSpecMapper
{
    public static TextSourceSpec ToSpec(this TextPlaylistItem t, long durationMs = 0)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new TextSourceSpec
        {
            Text = t.Text,
            FontFamily = t.FontFamily,
            FontSizePx = t.FontSizePx,
            Bold = t.Bold,
            Italic = t.Italic,
            ColorArgb = t.ColorArgb,
            BackgroundArgb = t.BackgroundArgb,
            OutlineArgb = t.OutlineArgb,
            OutlineWidthPx = t.OutlineWidthPx,
            HAlign = (int)t.HAlign,
            VAlign = (int)t.VAlign,
            WrapWidthFraction = t.WrapWidthFraction,
            PaddingPx = t.PaddingPx,
            CanvasWidth = t.CanvasWidth,
            CanvasHeight = t.CanvasHeight,
            DurationMs = durationMs,
        };
    }
}
