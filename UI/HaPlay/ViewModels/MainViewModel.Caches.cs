using HaPlay.Playback;
using HaPlay.Resources;
using S.Media.Source.MMD;

namespace HaPlay.ViewModels;

public partial class MainViewModel
{
    /// <summary>On-disk media caches surfaced on the Project workspace (size / open folder / clear).
    /// Sizes refresh whenever the Project workspace is selected, so the numbers are current by the
    /// time the operator can see them.</summary>
    public IReadOnlyList<MediaCacheViewModel> MediaCaches { get; } =
    [
        new(Strings.CacheYouTubeLabel, YouTubeRuntime.Preparer.CacheRoot, Strings.CacheYouTubeHint),
        new(Strings.CacheMMDBakeLabel, MMDPhysicsBakeCache.CacheDirectory, Strings.CacheMMDBakeHint),
    ];

    private void RefreshMediaCacheSizes()
    {
        foreach (var cache in MediaCaches)
            _ = cache.RefreshCommand.ExecuteAsync(null);
    }
}
