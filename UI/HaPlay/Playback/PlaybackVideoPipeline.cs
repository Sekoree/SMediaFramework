using HaPlay.Models;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace HaPlay.Playback;

internal static class PlaybackVideoPipeline
{
    /// <summary>
    /// Builds the <see cref="IVideoSource"/> fed to <see cref="S.Media.Playback.MediaPlayer.TryOpen"/> for file playback.
    /// </summary>
    public static IVideoSource BuildFileVideoSource(
        MediaContainerDecoder decoder,
        in HaPlayFilePlaybackOptions fileOpts,
        List<IDisposable>? ownedDisposables)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        IVideoSource video = decoder.Video;
        if (OutputPresetVideoSource.WrapForPreset(decoder.Video, fileOpts.OutputPreset, out var presetOwned) is { } scaled)
        {
            video = scaled;
            ownedDisposables?.Add(presetOwned!);
        }

        var fadeInMs = fileOpts.EffectiveVideoFadeInMs;
        if (fadeInMs > 0)
        {
            var fade = new FadeFromBlackVideoSource(video, TimeSpan.FromMilliseconds(fadeInMs), disposeInner: false);
            ownedDisposables?.Add(fade);
            video = fade;
        }

        return video;
    }
}
