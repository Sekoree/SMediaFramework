using System.Text.Json.Serialization;

namespace S.Media.Source.Text;

/// <summary>
/// The serializable render parameters of a text cue plus its display duration. A flat value type (no host/UI
/// types) so it round-trips through a <see cref="TextSourceUri"/> and is portable across hosts. <see cref="HAlign"/>
/// and <see cref="VAlign"/> are integer alignment codes (0 = leading/top, 1 = center/middle, 2 = trailing/bottom).
/// </summary>
public sealed record TextSourceSpec
{
    public string Text { get; init; } = string.Empty;
    public string FontFamily { get; init; } = "Inter";
    public double FontSizePx { get; init; } = 96;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public uint ColorArgb { get; init; } = 0xFFFFFFFF;
    public uint BackgroundArgb { get; init; }
    public uint OutlineArgb { get; init; } = 0xFF000000;
    public double OutlineWidthPx { get; init; }

    /// <summary>Horizontal alignment: 0 = left, 1 = center, 2 = right.</summary>
    public int HAlign { get; init; }

    /// <summary>Vertical alignment: 0 = top, 1 = middle, 2 = bottom.</summary>
    public int VAlign { get; init; }

    public double WrapWidthFraction { get; init; } = 0.9;
    public double PaddingPx { get; init; } = 24;
    public int CanvasWidth { get; init; } = 1920;
    public int CanvasHeight { get; init; } = 1080;
    public long DurationMs { get; init; }
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TextSourceSpec))]
internal partial class TextSourceJsonContext : JsonSerializerContext;
