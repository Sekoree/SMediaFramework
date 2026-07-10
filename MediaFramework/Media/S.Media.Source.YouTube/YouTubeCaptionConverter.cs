using System.Globalization;
using System.Text;
using System.Text.Json;

namespace S.Media.Source.YouTube;

/// <summary>
/// Converts YouTube's rich <c>json3</c> timedtext format to an ASS/SSA document so the full styling and
/// positioning survive into libass. YoutubeExplode only exposes captions as flat SRT (plain text + timing),
/// which drops colours, bold/italic/underline, edge/outline and - most visibly - the per-cue window
/// positioning YouTube subtitles use. json3 carries all of it: a <c>pens</c> table (colour, alpha, b/i/u,
/// edge), a <c>wpWinPositions</c> table (anchor point + horizontal/vertical percentage), and events whose
/// segments reference those tables. This maps them to ASS override tags (<c>\pos</c>/<c>\an</c>, <c>\c</c>,
/// <c>\1a</c>, <c>\b</c>/<c>\i</c>/<c>\u</c>, <c>\bord</c>/<c>\3c</c>).
/// </summary>
public static class YouTubeCaptionConverter
{
    /// <summary>Converts a json3 timedtext document to a full ASS document. Positions are laid out in a
    /// <paramref name="playResX"/>×<paramref name="playResY"/> reference space; libass scales that to the
    /// actual render size, so the percentages land correctly whatever the composition canvas is.</summary>
    public static string Json3ToAss(string json3, int playResX = 1280, int playResY = 720)
    {
        using var doc = JsonDocument.Parse(json3);
        var root = doc.RootElement;
        var pens = ArrayOrEmpty(root, "pens");
        var positions = ArrayOrEmpty(root, "wpWinPositions");

        var sb = new StringBuilder();
        AppendHeader(sb, playResX, playResY);

        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in events.EnumerateArray())
            {
                if (!ev.TryGetProperty("segs", out var segs) || segs.ValueKind != JsonValueKind.Array)
                    continue; // format/style-only events carry no text
                var startMs = GetLong(ev, "tStartMs");
                var durMs = GetLong(ev, "dDurationMs");
                if (durMs <= 0)
                    continue;

                var body = BuildEventBody(ev, segs, pens, positions, playResX, playResY);
                if (body.Length == 0)
                    continue;

                sb.Append("Dialogue: 0,").Append(Stamp(startMs)).Append(',').Append(Stamp(startMs + durMs))
                  .Append(",Default,,0,0,0,,").Append(body).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Fallback for when the rich json3 format can't be fetched: wraps flat (text, timing) captions
    /// in the same ASS scaffold (plain white, bottom-centre, no positioning) so the sidecar is always a valid
    /// ASS document and the direct-libass render path still applies.</summary>
    public static string PlainCaptionsToAss(
        IEnumerable<(string Text, long StartMs, long DurationMs)> captions,
        int playResX = 1280, int playResY = 720)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, playResX, playResY);
        foreach (var (text, startMs, durMs) in captions)
        {
            if (durMs <= 0)
                continue;
            var body = EscapeAssText(text);
            if (body.Length == 0)
                continue;
            sb.Append("Dialogue: 0,").Append(Stamp(startMs)).Append(',').Append(Stamp(startMs + durMs))
              .Append(",Default,,0,0,0,,").Append(body).Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, int playResX, int playResY)
    {
        var fontSize = Math.Max(24, playResY / 18);
        sb.Append("[Script Info]\n")
          .Append("ScriptType: v4.00+\n")
          .Append("WrapStyle: 2\n")
          .Append("ScaledBorderAndShadow: yes\n")
          .Append("PlayResX: ").Append(playResX).Append('\n')
          .Append("PlayResY: ").Append(playResY).Append("\n\n")
          .Append("[V4+ Styles]\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
          .Append("Style: Default,Arial,").Append(fontSize)
          .Append(",&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,2,20,20,20,1\n\n")
          .Append("[Events]\n")
          .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
    }

    private static string BuildEventBody(
        JsonElement ev, JsonElement segs, IReadOnlyList<JsonElement> pens, IReadOnlyList<JsonElement> positions,
        int w, int h)
    {
        var sb = new StringBuilder();

        // Window position → one \an + \pos for the whole line (persists across the segment style overrides).
        var wpId = GetInt(ev, "wpWinPosId", -1);
        if (wpId >= 0 && wpId < positions.Count && positions[wpId].ValueKind == JsonValueKind.Object)
        {
            var pos = positions[wpId];
            if (pos.TryGetProperty("ahHorPos", out _) || pos.TryGetProperty("avVerPos", out _)
                || pos.TryGetProperty("apPoint", out _))
            {
                var an = AnchorPointToAlignment(GetInt(pos, "apPoint", 7));
                var x = (int)Math.Round(GetInt(pos, "ahHorPos", 50) / 100.0 * w);
                var y = (int)Math.Round(GetInt(pos, "avVerPos", 100) / 100.0 * h);
                sb.Append("{\\an").Append(an).Append("\\pos(").Append(x).Append(',').Append(y).Append(")}");
            }
        }

        var eventPen = GetInt(ev, "pPenId", -1);
        var hasText = false;
        foreach (var seg in segs.EnumerateArray())
        {
            var utf8 = seg.TryGetProperty("utf8", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            if (utf8.Length == 0)
                continue;
            var text = EscapeAssText(utf8);
            if (text.Length == 0)
                continue;

            var penId = seg.TryGetProperty("pPenId", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32()
                : eventPen;
            // Every segment carries a COMPLETE style override (not just its deltas) so styling never bleeds
            // from an earlier segment - and \pos/\an above are untouched by these style-only tags.
            sb.Append('{').Append(PenOverride(pens, penId)).Append('}').Append(text);
            hasText = true;
        }

        return hasText ? sb.ToString() : string.Empty;
    }

    // A fully-specified style override for one pen: bold/italic/underline, primary colour + alpha, and outline.
    // Absent attributes fall back to readable defaults (white, opaque, thin black outline) so a plain segment
    // after a coloured one reverts cleanly.
    private static string PenOverride(IReadOnlyList<JsonElement> pens, int penId)
    {
        var pen = penId >= 0 && penId < pens.Count && pens[penId].ValueKind == JsonValueKind.Object
            ? pens[penId]
            : default;

        var b = GetInt(pen, "bAttr", 0) != 0 ? 1 : 0;
        var i = GetInt(pen, "iAttr", 0) != 0 ? 1 : 0;
        var un = GetInt(pen, "uAttr", 0) != 0 ? 1 : 0;

        var sb = new StringBuilder();
        sb.Append("\\b").Append(b).Append("\\i").Append(i).Append("\\u").Append(un);

        // Foreground colour (0xRRGGBB) → ASS &HBBGGRR&; always emitted (default white) to prevent bleed.
        var fg = pen.ValueKind == JsonValueKind.Object && pen.TryGetProperty("fcForeColor", out var fc)
                 && fc.ValueKind == JsonValueKind.Number
            ? fc.GetInt32()
            : 0xFFFFFF;
        sb.Append("\\c").Append(AssColor(fg));

        // Foreground alpha: json3 255 = opaque, ASS \1a 00 = opaque → invert. Default opaque.
        var fa = GetInt(pen, "foForeAlpha", 255);
        sb.Append("\\1a").Append(AssAlpha(fa));

        // Edge (outline). json3 etEdgeType 1..4 present ⇒ draw an outline in ecEdgeColor; else a thin black one.
        if (pen.ValueKind == JsonValueKind.Object && pen.TryGetProperty("etEdgeType", out var et)
            && et.ValueKind == JsonValueKind.Number && et.GetInt32() != 0)
        {
            var ec = GetInt(pen, "ecEdgeColor", 0x000000);
            sb.Append("\\bord2\\3c").Append(AssColor(ec));
        }
        else
        {
            sb.Append("\\bord2\\3c&H000000&");
        }

        return sb.ToString();
    }

    // YouTube anchor point (0..8, reading TL→BR) → ASS numpad alignment (\an 1..9, BL→TR).
    private static int AnchorPointToAlignment(int ap) => ap switch
    {
        0 => 7, 1 => 8, 2 => 9,   // top    L C R
        3 => 4, 4 => 5, 5 => 6,   // middle L C R
        6 => 1, 7 => 2, 8 => 3,   // bottom L C R
        _ => 2,                   // default bottom-centre
    };

    private static string AssColor(int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return $"&H{b:X2}{g:X2}{r:X2}&";
    }

    private static string AssAlpha(int youtubeAlpha) =>
        $"&H{Math.Clamp(255 - youtubeAlpha, 0, 255):X2}&";

    private static string EscapeAssText(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\n': sb.Append("\\N"); break;
                case '\r': break;
                case '{': sb.Append('('); break; // braces delimit ASS override blocks - neutralise stray ones
                case '}': sb.Append(')'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string Stamp(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        // ASS centiseconds: H:MM:SS.cc
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    private static IReadOnlyList<JsonElement> ArrayOrEmpty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().ToArray()
            : [];

    private static long GetLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static int GetInt(JsonElement e, string name, int fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : fallback;
}
