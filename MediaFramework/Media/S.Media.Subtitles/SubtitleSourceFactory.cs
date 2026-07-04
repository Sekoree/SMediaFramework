using S.Media.Core.Video;

namespace S.Media.Subtitles;

/// <summary>
/// Builds an <see cref="IVideoOverlaySource"/> from a sidecar <strong>ASS/SSA</strong> file, rendered full-fidelity
/// through libass (<see cref="AssSubtitleLayerSource"/>). Returns <c>null</c> for a missing file or a non-ASS
/// extension — every other format (SRT/VTT/MicroDVD/SAMI/SubViewer/…) and in-container subtitle streams are decoded
/// to ASS events by FFmpeg first; that path lives in the host (which references the FFmpeg decoder), not here.
/// </summary>
/// <remarks>
/// Host-side glue: hosts wire <see cref="FromFile"/> into <c>ShowSession</c> (as its subtitle factory) so a clip's
/// <c>SubtitlePath</c> auto-attaches as a layer, while the session/runtime stay renderer-agnostic (they only know
/// Core's <see cref="IVideoOverlaySource"/>). A sidecar <c>.ass</c> renders directly here without an FFmpeg
/// round-trip; route other formats through the FFmpeg-decode → libass path.
/// </remarks>
public static class SubtitleSourceFactory
{
    /// <summary>Creates an overlay source for a sidecar ASS/SSA <paramref name="path"/> at <paramref name="width"/>×
    /// <paramref name="height"/> (the composition canvas), or <c>null</c> if missing or not ASS/SSA.</summary>
    public static IVideoOverlaySource? FromFile(string path, int width, int height, SubtitleStyleOverride? style = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            return null;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => new AssSubtitleLayerSource(File.ReadAllBytes(path), width, height, style: style),
            _ => null,
        };
    }
}
