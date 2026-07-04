using S.Media.Source.YouTube;

namespace HaPlay.Playback;

/// <summary>
/// One shared YouTube gateway/preparer for the whole app: the media registry's youtube provider and
/// the add/edit dialogs must consult the SAME cache, and coalescing only works on a shared preparer.
/// The default cache root (LocalApplicationData/mfplayer/youtube-cache) survives app restarts — a show
/// prepared yesterday plays offline today (reliable mode).
/// </summary>
internal static class YouTubeRuntime
{
    public static YoutubeExplodeGateway Gateway { get; } = new();

    public static YouTubePreparer Preparer { get; } = new(Gateway);

    public static YouTubeSourceModule Module { get; } = new(Preparer);
}
