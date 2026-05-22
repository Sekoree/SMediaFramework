using S.Media.Core.Video;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Extension-to-factory registry for optional backends (still images, etc.).
/// </summary>
public static class MediaFrameworkExtensionRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Func<string, IVideoSource>> ImageFactories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a still-image factory for an extension (e.g. <c>.png</c>).</summary>
    public static void RegisterImageExtension(string extension, Func<string, IVideoSource> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(factory);
        var ext = NormalizeExtension(extension);
        lock (Gate)
            ImageFactories[ext] = factory;
    }

    /// <summary>Returns a registered image factory or <c>null</c>.</summary>
    public static Func<string, IVideoSource>? TryGetImageFactory(string extension)
    {
        var ext = NormalizeExtension(extension);
        lock (Gate)
            return ImageFactories.TryGetValue(ext, out var f) ? f : null;
    }

    /// <summary>Snapshot of registered image extensions.</summary>
    public static IReadOnlyList<string> RegisteredImageExtensions
    {
        get
        {
            lock (Gate)
                return ImageFactories.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var ext = extension.Trim();
        if (ext.Length == 0)
            throw new ArgumentException("Extension cannot be empty.", nameof(extension));
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        return ext.ToLowerInvariant();
    }
}
