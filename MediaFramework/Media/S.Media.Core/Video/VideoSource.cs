using S.Media.Core.Diagnostics;

namespace S.Media.Core.Video;

/// <summary>Discoverable entry points for file-, stream-, and image-backed <see cref="IVideoSource"/> instances.</summary>
public static class VideoSource
{
    /// <summary>Opens the video track of a media file. Requires <c>.UseFFmpeg()</c>.</summary>
    public static IVideoSource OpenFile(string path, VideoSourceOpenOptions? options = null)
        => OpenFile(path, options, scopedFactory: null);

    /// <summary>
    /// Opens the video track of a media file. Resolution order is scoped factory, process-wide plugin factory.
    /// </summary>
    public static IVideoSource OpenFile(
        string path,
        VideoSourceOpenOptions? options,
        Func<string, VideoSourceOpenOptions?, IVideoSource>? scopedFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var factory = scopedFactory ?? MediaFrameworkPlugins.VideoSourceFileFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenFile: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(path, options);
    }

    /// <summary>Opens the video track of a media stream. Requires FFmpeg init.</summary>
    public static IVideoSource OpenStream(Stream stream, VideoSourceOpenOptions? options = null)
        => OpenStream(stream, options, scopedFactory: null);

    /// <summary>
    /// Opens the video track of a media stream. Resolution order is scoped factory, process-wide plugin factory.
    /// </summary>
    public static IVideoSource OpenStream(
        Stream stream,
        VideoSourceOpenOptions? options,
        Func<Stream, VideoSourceOpenOptions?, IVideoSource>? scopedFactory)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = scopedFactory ?? MediaFrameworkPlugins.VideoSourceStreamFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenStream: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(stream, options);
    }

    /// <summary>
    /// Opens a still image file by extension via <see cref="MediaFrameworkExtensionRegistry"/>,
    /// then falls back to <see cref="MediaFrameworkPlugins.ImageFileSourceFactory"/>.
    /// </summary>
    public static IVideoSource OpenImage(string path)
        => OpenImage(path, scopedFactory: null);

    /// <summary>
    /// Opens a still image file. Resolution order is scoped factory, extension registry, process-wide image factory.
    /// </summary>
    public static IVideoSource OpenImage(string path, Func<string, IVideoSource>? scopedFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (scopedFactory is not null)
            return scopedFactory(path);

        var ext = Path.GetExtension(path);
        if (ext.Length > 0)
        {
            var reg = MediaFrameworkExtensionRegistry.TryGetImageFactory(ext);
            if (reg is not null)
                return reg(path);
        }

        var factory = MediaFrameworkPlugins.ImageFileSourceFactory
            ?? throw new InvalidOperationException(
                $"VideoSource.OpenImage: no factory for '{ext}' — register via MediaFrameworkExtensionRegistry or call MediaFrameworkRuntime.Init().UseSkiaSharpImages().");
        return factory(path);
    }

    /// <summary>Opens a still image from a stream. Requires SkiaSharp image backend.</summary>
    public static IVideoSource OpenImage(Stream stream)
        => OpenImage(stream, scopedFactory: null);

    /// <summary>
    /// Opens a still image from a stream. Resolution order is scoped factory, process-wide image factory.
    /// </summary>
    public static IVideoSource OpenImage(Stream stream, Func<Stream, IVideoSource>? scopedFactory)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = scopedFactory ?? MediaFrameworkPlugins.ImageStreamSourceFactory
            ?? throw new InvalidOperationException(
                "VideoSource.OpenImage: no backend installed — call MediaFrameworkRuntime.Init().UseSkiaSharpImages() (requires S.Media.SkiaSharp).");
        return factory(stream);
    }
}
