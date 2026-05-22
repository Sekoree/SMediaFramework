using HaPlay.Models;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace HaPlay.Playback;

internal static class PlaybackVideoPipeline
{
    /// <summary>
    /// Builds the fixed-raster program source for file or live paths.
    /// </summary>
    public static IVideoSource BuildProgramVideoSource(
        IVideoSource source,
        in HaPlayFilePlaybackOptions fileOpts,
        List<IDisposable>? ownedDisposables,
        bool disposeInnerOnPresetDispose)
    {
        ArgumentNullException.ThrowIfNull(source);
        IVideoSource video = source;
        if (OutputPresetVideoSource.WrapForPreset(
                source,
                fileOpts.OutputPreset,
                out var presetOwned,
                fileOpts.CustomOutputWidth,
                fileOpts.CustomOutputHeight,
                disposeInnerOnPresetDispose) is { } scaled)
        {
            video = scaled;
            ownedDisposables?.Add(presetOwned!);
        }

        return video;
    }

    /// <summary>
    /// Builds the <see cref="IVideoSource"/> fed to <see cref="S.Media.Playback.MediaPlayer.TryOpen"/> for file playback.
    /// </summary>
    public static IVideoSource BuildFileVideoSource(
        MediaContainerDecoder decoder,
        in HaPlayFilePlaybackOptions fileOpts,
        List<IDisposable>? ownedDisposables)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var video = BuildProgramVideoSource(
            decoder.Video,
            fileOpts,
            ownedDisposables,
            disposeInnerOnPresetDispose: false);

        var fadeInMs = fileOpts.EffectiveVideoFadeInMs;
        if (fadeInMs > 0)
        {
            var fade = new FadeFromBlackVideoSource(video, TimeSpan.FromMilliseconds(fadeInMs), disposeInner: false);
            ownedDisposables?.Add(fade);
            video = fade;
        }

        return video;
    }

    /// <summary>
    /// Builds the <see cref="IVideoSource"/> fed to <see cref="S.Media.Playback.MediaPlayer.TryOpenLive"/>
    /// so live inputs respect the same output-preset raster as files.
    /// </summary>
    public static IVideoSource BuildLiveVideoSource(
        IVideoSource source,
        in HaPlayFilePlaybackOptions fileOpts,
        List<IDisposable>? ownedDisposables,
        bool disposeInnerOnPresetDispose) =>
        BuildProgramVideoSource(source, fileOpts, ownedDisposables, disposeInnerOnPresetDispose);
}
