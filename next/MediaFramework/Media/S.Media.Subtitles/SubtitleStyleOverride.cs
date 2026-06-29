namespace S.Media.Subtitles;

/// <summary>
/// Optional per-render style overrides for libass-rendered subtitles (sidecar ASS, or any format FFmpeg decodes
/// to ASS events). Applied globally to the renderer — they nudge the document's own styling rather than rewriting
/// it. Ignored by bitmap (PGS/DVB) subtitles, which carry their own pixels. A <c>null</c> field keeps the
/// document's value.
/// </summary>
public sealed record SubtitleStyleOverride(
    /// <summary>Fallback/override font family (libass resolves it via fontconfig / DirectWrite / CoreText).</summary>
    string? FontFamily = null,
    /// <summary>Global font-size multiplier (1.0 = document default). Clamped to a sane range when applied.</summary>
    double? FontScale = null)
{
    /// <summary>True when at least one override is set (so callers can skip building a renderer override).</summary>
    public bool HasAny => !string.IsNullOrWhiteSpace(FontFamily) || FontScale is > 0;
}
