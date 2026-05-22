using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.SkiaSharp;

internal static class SkiaImageExtensionRegistration
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    public static void Register()
    {
        foreach (var ext in Extensions)
            MediaFrameworkExtensionRegistry.RegisterImageExtension(ext, static path => ImageFileSource.OpenFromFile(path));
    }
}

/// <summary>SkiaSharp image backend hook for <see cref="MediaFrameworkRuntime"/>.</summary>
public static class MediaFrameworkRuntimeSkiaSharpExtensions
{
    private static int _registered;

    /// <summary>Registers still-image <see cref="IVideoSource"/> factories on <see cref="MediaFrameworkPlugins"/>.</summary>
    public static MediaFrameworkRuntimeBuilder UseSkiaSharpImages(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return builder;

        MediaFrameworkPlugins.ImageFileSourceFactory = path => ImageFileSource.OpenFromFile(path);
        MediaFrameworkPlugins.ImageStreamSourceFactory = stream => ImageFileSource.OpenFromStream(stream);
        SkiaImageExtensionRegistration.Register();
        return builder;
    }
}
