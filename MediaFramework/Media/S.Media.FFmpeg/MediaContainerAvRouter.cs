using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Small factory for the usual <see cref="MediaContainerDecoder"/> + <see cref="MediaPlaybackSession"/> + <see cref="AvRouter"/>
/// wiring (strategy C stepping stone — one place to construct the pair with matching session).
/// For a named holder of decoder + player + router references, see <see cref="MediaContainerPlaybackGraph"/>.
/// </summary>
public static class MediaContainerAvRouter
{
    /// <summary>
    /// Pairs <paramref name="decoder"/> with a new <see cref="MediaPlaybackSession"/> and <see cref="AvRouter"/>.
    /// </summary>
    public static AvRouter Create(MediaContainerDecoder decoder, VideoPlayer video, IMediaClock clock, AudioPlayer? audio = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(clock);
        return new AvRouter(decoder, new MediaPlaybackSession(video, clock, audio));
    }
}
