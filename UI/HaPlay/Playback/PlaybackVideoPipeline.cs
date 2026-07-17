using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Decode.FFmpeg;

namespace HaPlay.Playback;

internal static class PlaybackVideoPipeline
{
    /// <summary>
    /// When false (default), live NDI/video is converted to <see cref="PixelFormat.Bgra32"/> before
    /// <see cref="S.Media.Session.MediaPlayer.TryOpenLive"/> so local outputs use the same path as idle
    /// preview. When true, native UYVY is passed through (SDL GL; NDI frames use limited-range BT.709 metadata).
    /// Persisted via <see cref="HaPlay.Models.AppSettings"/>
    /// and overridable from the player UI.
    /// </summary>
    public static bool PreferNativePixelFormatForLiveVideo { get; set; }

    /// <summary>Set from <c>--media-live-uyvy-passthrough</c> so startup does not overwrite CLI intent.</summary>
    internal static bool CliRequestedUyvyPassthrough { get; set; }

    /// <summary>The switch <see cref="ApplyCliStartupOptions"/> recognizes (documented in HaPlay.Desktop's
    /// startup help): skip live UYVY→BGRA conversion and hand native UYVY to the SDL GL path.</summary>
    internal const string UyvyPassthroughSwitch = "--media-live-uyvy-passthrough";

    /// <summary>
    /// Applies playback-related command-line switches. MUST run before <c>MainViewModel</c> is
    /// constructed (it consults <see cref="CliRequestedUyvyPassthrough"/> when applying the persisted
    /// preference) and independently of logging configuration - <c>--media-log off</c> previously
    /// short-circuited the parse so the switch logged success without doing anything (review P2-8).
    /// Returns true when the passthrough override was applied so the caller can log AFTER the
    /// logging pipeline exists.
    /// </summary>
    internal static bool ApplyCliStartupOptions(string[] args)
    {
        if (!args.Any(a => string.Equals(a, UyvyPassthroughSwitch, StringComparison.OrdinalIgnoreCase)))
            return false;

        CliRequestedUyvyPassthrough = true;
        PreferNativePixelFormatForLiveVideo = true;
        return true;
    }

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
    /// Builds the <see cref="IVideoSource"/> fed to <see cref="S.Media.Session.MediaPlayer.TryOpen"/> for file playback.
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
            var fade = FadeFromBlackVideoSource.Wrap(video, TimeSpan.FromMilliseconds(fadeInMs), disposeInner: false);
            ownedDisposables?.Add(fade);
            video = fade;
        }

        return video;
    }

    /// <summary>
    /// Builds the <see cref="IVideoSource"/> fed to <see cref="S.Media.Session.MediaPlayer.TryOpenLive"/>
    /// so live inputs respect the same output-preset raster as files.
    /// </summary>
    public static IVideoSource BuildLiveVideoSource(
        IVideoSource source,
        in HaPlayFilePlaybackOptions fileOpts,
        List<IDisposable>? ownedDisposables,
        bool disposeInnerOnPresetDispose) =>
        BuildProgramVideoSource(source, fileOpts, ownedDisposables, disposeInnerOnPresetDispose);

    /// <summary>
    /// Wraps live NDI (and similar) sources so local SDL/Avalonia outputs receive BGRA32, which matches
    /// the idle-preview path and avoids UYVY shader/range pitfalls on some Linux/Wayland stacks.
    /// </summary>
    public static IVideoSource? WrapLiveVideoForLocalDisplay(
        IVideoSource? source,
        bool disposeInnerOnWrapperDispose = false)
    {
        if (source is null || PreferNativePixelFormatForLiveVideo)
            return source;

        var native = source.NativePixelFormats;
        for (var i = 0; i < native.Count; i++)
        {
            if (native[i] == PixelFormat.Bgra32)
                return source;
        }

        return new PixelFormatConvertingVideoSource(source, PixelFormat.Bgra32, disposeInner: disposeInnerOnWrapperDispose);
    }
}
