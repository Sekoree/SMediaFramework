using System.Diagnostics.CodeAnalysis;
using YoutubeExplode.Videos;

namespace S.Media.Source.YouTube;

/// <summary>
/// The persisted stream selection for one youtube source. Stream identity is a descriptor string
/// (<c>label|codec|container</c> for video, <c>codec|container|language</c> for audio) rather than an
/// itag - YoutubeExplode does not expose itags publicly, and the descriptor stays meaningful to the
/// operator in saved shows. <c>null</c> means "best by policy" at prepare time.
/// </summary>
public sealed record YouTubeStreamSelection(
    string? Video = null,
    string? Audio = null,
    string? SubtitleLanguage = null)
{
    public static readonly YouTubeStreamSelection Best = new();

    /// <summary>True when this selection carries a video leg (video may be disabled for audio-only cues).</summary>
    public bool IncludeVideo { get; init; } = true;

    /// <summary>True to also download the video's thumbnail and embed it into the prepared asset as an
    /// EXTRA still-image video stream (selectable in stream pickers - e.g. an audio-only cue can show
    /// the cover). Part of the cache identity: the same A/V pair with and without the thumbnail are
    /// different assets.</summary>
    public bool IncludeThumbnail { get; init; }
}

/// <summary>
/// Canonical URI codec for YouTube sources: <c>youtube://&lt;videoId&gt;?v=…&amp;a=…&amp;sub=…&amp;novideo=1&amp;thumb=1</c>.
/// Watch/short URLs (`youtube.com/watch?v=`, `youtu.be/…`, `/shorts/…`) normalize to the same form via
/// <see cref="VideoId.TryParse"/>, so link variations never change cache identity (review Gate-5 design).
/// </summary>
public static class YouTubeSourceUri
{
    public const string Scheme = "youtube";

    /// <summary>Builds the canonical URI for a video id + selection.</summary>
    public static string Build(string videoId, YouTubeStreamSelection selection)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoId);
        ArgumentNullException.ThrowIfNull(selection);
        var query = new List<string>(3);
        if (selection.Video is { Length: > 0 } v)
            query.Add("v=" + Uri.EscapeDataString(v));
        if (selection.Audio is { Length: > 0 } a)
            query.Add("a=" + Uri.EscapeDataString(a));
        if (selection.SubtitleLanguage is { Length: > 0 } s)
            query.Add("sub=" + Uri.EscapeDataString(s));
        if (!selection.IncludeVideo)
            query.Add("novideo=1");
        if (selection.IncludeThumbnail)
            query.Add("thumb=1");
        return query.Count == 0
            ? $"{Scheme}://{videoId}"
            : $"{Scheme}://{videoId}?{string.Join('&', query)}";
    }

    /// <summary>
    /// Parses a canonical <c>youtube://</c> URI or any recognizable YouTube watch/share URL or bare
    /// 11-character video id. Watch URLs yield the default (best) selection.
    /// </summary>
    public static bool TryParse(
        string? uri,
        [NotNullWhen(true)] out string? videoId,
        [NotNullWhen(true)] out YouTubeStreamSelection? selection)
    {
        videoId = null;
        selection = null;
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (uri.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            var rest = uri[(uri.IndexOf(':') + 1)..].TrimStart('/');
            string? query = null;
            var q = rest.IndexOf('?');
            if (q >= 0)
            {
                query = rest[(q + 1)..];
                rest = rest[..q];
            }

            if (VideoId.TryParse(rest) is not { } parsed)
                return false;
            videoId = parsed.Value;

            string? video = null, audio = null, sub = null;
            var includeVideo = true;
            var includeThumbnail = false;
            if (query is not null)
            {
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = pair[..eq];
                    var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    switch (key)
                    {
                        case "v": video = value; break;
                        case "a": audio = value; break;
                        case "sub": sub = value; break;
                        case "novideo": includeVideo = value is not ("1" or "true"); break;
                        case "thumb": includeThumbnail = value is "1" or "true"; break;
                    }
                }
            }

            selection = new YouTubeStreamSelection(video, audio, sub)
            {
                IncludeVideo = includeVideo,
                IncludeThumbnail = includeThumbnail,
            };
            return true;
        }

        // Watch/share URLs and bare ids - but NOT arbitrary strings that merely look id-shaped unless
        // they came through an explicit YouTube entry point. Only accept URL forms here; a bare string
        // is ambiguous with local file names, so the UI passes canonical URIs for those.
        if (uri.Contains("youtube.", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            if (VideoId.TryParse(uri) is not { } fromUrl)
                return false;
            videoId = fromUrl.Value;
            selection = YouTubeStreamSelection.Best;
            return true;
        }

        return false;
    }
}
