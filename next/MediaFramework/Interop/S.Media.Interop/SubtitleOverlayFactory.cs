using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Subtitles;

namespace S.Media.Interop;

/// <summary>
/// The unified subtitle factory: builds an <see cref="IVideoOverlaySource"/> from any subtitle source — a sidecar
/// file (SRT/VTT/MicroDVD/SAMI/SubViewer/ASS/…) or a media container carrying a subtitle stream — all rendered
/// through libass. Sidecar ASS/SSA goes straight to libass; every other text format and in-container stream is
/// decoded to ASS events by FFmpeg first.
/// </summary>
/// <remarks>
/// Host glue: it pairs the FFmpeg decoder with the libass renderer so the session and the subtitle library stay
/// decoupled (neither references the other). Wire <see cref="FromFile"/> into <c>ShowSession</c> as its subtitle
/// factory delegate so a clip's <c>SubtitlePath</c> — of any format — auto-attaches as a layer.
/// </remarks>
public static class SubtitleOverlayFactory
{
    /// <summary>
    /// Creates an overlay source for <paramref name="path"/> at the composition canvas size, or <c>null</c> when
    /// the file is missing, carries no decodable text subtitle, or is a bitmap subtitle.
    /// </summary>
    public static IVideoOverlaySource? FromFile(string path, int width, int height)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        // Sidecar ASS/SSA renders directly — no FFmpeg round-trip.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ass" or ".ssa")
            return SubtitleSourceFactory.FromFile(path, width, height);

        // Everything else — other sidecar text formats, or a container's subtitle stream — via FFmpeg → libass.
        DecodedSubtitleTrack track;
        try
        {
            track = FFmpegSubtitleDecoder.Decode(path);
        }
        catch
        {
            return null; // unopenable, or no subtitle stream
        }

        if (track.Events.Count == 0)
            return TryBitmap(path); // no text events — maybe a bitmap subtitle (PGS/DVB/VobSub)

        var events = track.Events.Select(e => new AssEventChunk(e.Body, e.StartMs, e.DurationMs)).ToList();
        var fonts = track.Fonts.Select(f => new AssFontAttachment(f.Name, f.Data)).ToList();
        return new AssSubtitleLayerSource(width, height, track.Header, events, fonts);
    }

    // Bitmap subtitles (PGS/DVB/VobSub) are images, composited directly without libass.
    private static IVideoOverlaySource? TryBitmap(string path)
    {
        try
        {
            var bitmap = FFmpegBitmapSubtitleDecoder.Decode(path);
            return bitmap.Cues.Count > 0 ? new BitmapSubtitleLayerSource(bitmap) : null;
        }
        catch
        {
            return null;
        }
    }
}
