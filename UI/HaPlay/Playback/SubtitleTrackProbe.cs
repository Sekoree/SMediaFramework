using S.Media.Decode.FFmpeg;

namespace HaPlay.Playback;

/// <summary>One embedded subtitle track discovered in a container, for the track picker.</summary>
public sealed record SubtitleTrackInfo(
    int StreamIndex,
    string DisplayLabel,
    string? Language,
    string? Title,
    string CodecName,
    bool IsForced,
    bool IsDefault)
{
    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvb_subtitle", "dvbsub", "dvd_subtitle", "xsub",
    };

    /// <summary>True for image-based subtitles (PGS/DVB/VobSub) — font/placement overrides don't apply.</summary>
    public bool IsBitmap => BitmapCodecs.Contains(CodecName);
}

/// <summary>
/// Lists the embedded subtitle tracks in a media container (MKV/MP4/…) via the framework's metadata probe
/// (<see cref="MediaContainerDecoder.ProbeStreams"/>, no decoding). Powers the media-player / cue subtitle
/// track picker. Returns empty on a missing/unreadable file rather than throwing.
/// </summary>
internal static class SubtitleTrackProbe
{
    public static IReadOnlyList<SubtitleTrackInfo> List(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];

        try
        {
            return MediaContainerDecoder.ProbeStreams(path)
                .Where(s => s.Kind == MediaStreamKind.Subtitle)
                .Select(s => new SubtitleTrackInfo(
                    s.Index, s.ToDisplayString(), s.Language, s.Title, s.CodecName, s.IsForced, s.IsDefault))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
