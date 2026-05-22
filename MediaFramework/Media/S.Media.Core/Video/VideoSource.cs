using S.Media.Core.Diagnostics;

namespace S.Media.Core.Video;

/// <summary>Discoverable entry points for file-, stream-, and image-backed <see cref="IVideoSource"/> instances.</summary>
public static class VideoSource
{
    /// <summary>Opens the video track of a media file. Requires <c>.UseFFmpeg()</c>.</summary>
    public static IVideoSource OpenFile(string path, VideoSourceOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var factory = MediaFrameworkPlugins.VideoSourceFileFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenFile: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(path, options);
    }

    /// <summary>Opens the video track of a media stream. Requires FFmpeg init.</summary>
    public static IVideoSource OpenStream(Stream stream, VideoSourceOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = MediaFrameworkPlugins.VideoSourceStreamFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenStream: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(stream, options);
    }

    /// <summary>Opens a still image file. Requires <c>.UseSkiaSharpImages()</c>.</summary>
    public static IVideoSource OpenImage(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var factory = MediaFrameworkPlugins.ImageFileSourceFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenImage: no backend installed — call MediaFrameworkRuntime.Init().UseSkiaSharpImages() (requires S.Media.SkiaSharp).");
        return factory(path);
    }

    /// <summary>Opens a still image from a stream. Requires SkiaSharp image backend.</summary>
    public static IVideoSource OpenImage(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = MediaFrameworkPlugins.ImageStreamSourceFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenImage: no backend installed — call MediaFrameworkRuntime.Init().UseSkiaSharpImages() (requires S.Media.SkiaSharp).");
        return factory(stream);
    }
}
