using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using S.Media.FFmpeg.Common;

namespace S.Media.Source.YouTube;

/// <summary>Progress phases of a prepare (download-then-remux) run, for the readiness UI.</summary>
public enum YouTubePreparePhase { Resolving, DownloadingVideo, DownloadingAudio, Remuxing, Ready }

public readonly record struct YouTubePrepareProgress(YouTubePreparePhase Phase, double Fraction);

/// <summary>The locally prepared, playable result of one youtube selection. <see cref="ResolvedSelection"/>
/// carries the CONCRETE stream descriptors a "best" request resolved to — persist THAT (and build the
/// canonical URI from it), because a best-selection URI cannot be mapped to a cache asset offline.</summary>
public sealed record YouTubePreparedMedia(
    string AssetPath, string? SubtitlePath, YouTubeMediaManifest Manifest, YouTubeStreamSelection ResolvedSelection);

/// <summary>
/// Downloads the selected separate video/audio streams and stream-copy remuxes them into ONE local MKV
/// in a content-addressed cache, so playback runs entirely from disk (reliable mode — the review's
/// Gate-5 default; progressive playback is a later, explicitly-opted path). Cache identity =
/// videoId + resolved stream descriptors; writes go to <c>.partial</c> paths and commit by atomic
/// rename; concurrent prepares of the same key coalesce onto one task.
/// </summary>
public sealed class YouTubePreparer
{
    private readonly IYouTubeGateway _gateway;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, Lazy<Task<YouTubePreparedMedia>>> _inFlight = new(StringComparer.Ordinal);
    private readonly Action<string?, string?, string, CancellationToken> _remux;

    /// <summary>Default cache root: <c>~/.cache/mfplayer/youtube</c> (or the platform equivalent).</summary>
    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mfplayer", "youtube-cache");

    public YouTubePreparer(
        IYouTubeGateway gateway,
        string? cacheRoot = null,
        Action<string?, string?, string, CancellationToken>? remux = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _cacheRoot = cacheRoot ?? DefaultCacheRoot;
        // The remux step is injectable so cache/coalescing behavior tests offline without FFmpeg natives.
        _remux = remux ?? new Action<string?, string?, string, CancellationToken>(
            // Explicit container: the atomic-commit temp path ends in ".partial", so libavformat
            // cannot infer the muxer from the extension.
            static (v, a, output, ct) => FFmpegStreamCopyRemuxer.Remux(v, a, output, ct, containerFormat: "matroska"));
    }

    public string CacheRoot => _cacheRoot;

    /// <summary>Deterministic cache key for a video + RESOLVED stream descriptors.</summary>
    internal static string CacheKey(string videoId, string? videoDescriptor, string? audioDescriptor)
    {
        var material = $"1|{videoId}|{videoDescriptor}|{audioDescriptor}"; // leading component = key format version
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
        return $"{videoId}-{hash}";
    }

    /// <summary>The final asset path a selection WOULD occupy — exists ⇒ prepared (used by the provider's
    /// no-network open path and by readiness queries). Requires descriptors already resolved (not "best").</summary>
    public string AssetPathFor(string videoId, string? videoDescriptor, string? audioDescriptor) =>
        Path.Combine(_cacheRoot, CacheKey(videoId, videoDescriptor, audioDescriptor) + ".mkv");

    public bool IsPrepared(string videoId, string? videoDescriptor, string? audioDescriptor) =>
        File.Exists(AssetPathFor(videoId, videoDescriptor, audioDescriptor));

    /// <summary>Content-addressed cache path for ONE downloaded stream, keyed by its OWN descriptor so it is
    /// reused across selection changes — swapping the audio stream keeps the already-downloaded video and
    /// vice-versa, instead of re-downloading both. <paramref name="ext"/> tags the role (<c>.v</c>/<c>.a</c>);
    /// the container is content-probed at remux time, so the extension need not encode it.</summary>
    private string StreamCachePath(string videoId, string descriptor, string ext)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"1|{videoId}|{descriptor}")))[..16];
        return Path.Combine(_cacheRoot, $"{videoId}-{hash}{ext}");
    }

    /// <summary>
    /// Prepares (or returns the already-cached asset for) one selection. "Best" selections are resolved
    /// against the live manifest first, so their cache identity is the RESOLVED stream pair.
    /// </summary>
    public Task<YouTubePreparedMedia> PrepareAsync(
        string videoId,
        YouTubeStreamSelection selection,
        IProgress<YouTubePrepareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoId);
        ArgumentNullException.ThrowIfNull(selection);

        // Coalesce by request identity (pre-resolution): two "best" prepares of the same video share one run.
        var coalesceKey = $"{videoId}|{selection.Video}|{selection.Audio}|{selection.SubtitleLanguage}|{selection.IncludeVideo}";
        var lazy = _inFlight.GetOrAdd(coalesceKey, _ => new Lazy<Task<YouTubePreparedMedia>>(
            () => PrepareCoreAsync(videoId, selection, progress, cancellationToken)));
        return AwaitAndRelease(lazy, coalesceKey);

        async Task<YouTubePreparedMedia> AwaitAndRelease(Lazy<Task<YouTubePreparedMedia>> entry, string key)
        {
            try
            {
                return await entry.Value.ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    private async Task<YouTubePreparedMedia> PrepareCoreAsync(
        string videoId,
        YouTubeStreamSelection selection,
        IProgress<YouTubePrepareProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(YouTubePreparePhase.Resolving, 0));
        var manifest = await _gateway.GetManifestAsync(videoId, cancellationToken).ConfigureAwait(false);

        // Resolve "best" → concrete descriptors (best = highest quality video, highest-bitrate default-language audio).
        var videoDescriptor = selection.IncludeVideo
            ? selection.Video ?? manifest.VideoStreams.FirstOrDefault()?.Descriptor
            : null;
        var audioDescriptor = selection.Audio
            ?? (manifest.AudioStreams.FirstOrDefault(a => a.IsDefaultLanguage) ?? manifest.AudioStreams.FirstOrDefault())?.Descriptor;
        if (videoDescriptor is null && audioDescriptor is null)
            throw new InvalidOperationException($"YouTube video '{videoId}' offers no downloadable streams.");
        if (selection.Video is not null && manifest.VideoStreams.All(s => s.Descriptor != selection.Video))
            throw new InvalidOperationException(
                $"Selected video stream '{selection.Video}' is no longer offered for '{videoId}' — reselect streams.");
        if (selection.Audio is not null && manifest.AudioStreams.All(s => s.Descriptor != selection.Audio))
            throw new InvalidOperationException(
                $"Selected audio stream '{selection.Audio}' is no longer offered for '{videoId}' — reselect streams.");

        Directory.CreateDirectory(_cacheRoot);
        var key = CacheKey(videoId, videoDescriptor, audioDescriptor);
        var assetPath = Path.Combine(_cacheRoot, key + ".mkv");
        var subtitlePath = selection.SubtitleLanguage is { Length: > 0 } lang
            ? Path.Combine(_cacheRoot, $"{key}.{lang}.ass")
            : null;

        if (!File.Exists(assetPath))
        {
            // Per-stream caches (keyed by each stream's own descriptor) so changing ONE stream selection
            // reuses the other — swapping audio keeps the cached video and vice-versa, instead of
            // re-downloading both. They persist for that reuse (like the .mkv assets); only the remux is temp.
            var videoCache = videoDescriptor is null ? null : StreamCachePath(videoId, videoDescriptor, ".v");
            var audioCache = audioDescriptor is null ? null : StreamCachePath(videoId, audioDescriptor, ".a");
            var assetTemp = assetPath + ".partial";
            try
            {
                if (videoCache is not null && !File.Exists(videoCache))
                {
                    progress?.Report(new(YouTubePreparePhase.DownloadingVideo, 0));
                    await DownloadToCacheAsync(
                        videoDescriptor!, videoCache,
                        Wrap(progress, YouTubePreparePhase.DownloadingVideo)).ConfigureAwait(false);
                }

                if (audioCache is not null && !File.Exists(audioCache))
                {
                    progress?.Report(new(YouTubePreparePhase.DownloadingAudio, 0));
                    await DownloadToCacheAsync(
                        audioDescriptor!, audioCache,
                        Wrap(progress, YouTubePreparePhase.DownloadingAudio)).ConfigureAwait(false);
                }

                progress?.Report(new(YouTubePreparePhase.Remuxing, 0));
                await Task.Run(
                    () => _remux(videoCache, audioCache, assetTemp, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                File.Move(assetTemp, assetPath, overwrite: true); // atomic commit — readers only ever see complete assets
            }
            finally
            {
                TryDelete(assetTemp);
                // videoCache / audioCache are intentionally KEPT for reuse across stream-selection changes.
            }
        }

        if (subtitlePath is not null && !File.Exists(subtitlePath))
        {
            var subTemp = subtitlePath + ".partial";
            try
            {
                if (await _gateway.TryDownloadCaptionsAssAsync(
                        videoId, selection.SubtitleLanguage!, subTemp, cancellationToken).ConfigureAwait(false))
                    File.Move(subTemp, subtitlePath, overwrite: true);
                else
                    subtitlePath = null; // requested language not offered — asset still plays
            }
            finally
            {
                TryDelete(subTemp);
            }
        }

        progress?.Report(new(YouTubePreparePhase.Ready, 1));
        var resolved = new YouTubeStreamSelection(videoDescriptor, audioDescriptor, selection.SubtitleLanguage)
        {
            IncludeVideo = selection.IncludeVideo,
        };
        return new YouTubePreparedMedia(assetPath, subtitlePath, manifest, resolved);

        // Download one stream into its content-addressed cache atomically: to a unique temp, then rename into
        // place. A concurrent prepare that reuses the same stream either finds the finished cache file (and
        // skips) or races to its own temp — it never observes a partial cache file.
        async Task DownloadToCacheAsync(string descriptor, string cachePath, IProgress<double>? p)
        {
            var temp = $"{cachePath}.{Guid.NewGuid():N}.partial";
            try
            {
                await _gateway.DownloadStreamAsync(videoId, descriptor, temp, p, cancellationToken).ConfigureAwait(false);
                File.Move(temp, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        static IProgress<double>? Wrap(IProgress<YouTubePrepareProgress>? inner, YouTubePreparePhase phase) =>
            inner is null ? null : new Progress<double>(f => inner.Report(new(phase, f)));

        static void TryDelete(string? path)
        {
            if (path is null)
                return;
            try { File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
