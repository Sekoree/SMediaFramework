using S.Media.Core.Diagnostics;
using S.Media.Decode.FFmpeg;
using Microsoft.Extensions.Logging;

namespace HaPlay.Playback;

/// <summary>
/// Pre-opens FFmpeg decoders for adjacent playlist items so track switching
/// doesn't block on container probing. Keeps at most <see cref="MaxEntries"/>
/// decoders open. Thread-safe.
/// </summary>
internal sealed class PlaylistDecoderCache : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.PlaylistDecoderCache");
    private const int MaxEntries = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    // Cache key includes the explicit audio track so a decoder pre-opened on the default track is
    // never reused after the operator picks a different language on the item (and vice versa).
    private static string BuildKey(string path, int? audioTrackIndex) =>
        $"{path}\n#atrack:{(audioTrackIndex is { } t ? t.ToString() : "auto")}";

    public MediaContainerDecoder? TryTake(string path, int? audioTrackIndex = null)
    {
        lock (_gate)
        {
            if (_entries.Remove(BuildKey(path, audioTrackIndex), out var entry))
            {
                Trace.LogDebug("PlaylistDecoderCache: hit for {Path}", path);
                return entry.Decoder;
            }
        }
        return null;
    }

    public void PreOpen(string path, int? audioTrackIndex = null, CancellationToken ct = default)
    {
        var key = BuildKey(path, audioTrackIndex);
        lock (_gate)
        {
            if (_disposed) return;
            if (_entries.ContainsKey(key)) return;
        }

        MediaContainerDecoder? decoder;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Match the live open path's deep read-ahead + large file read buffer so a cached track plays as
            // smoothly as a freshly-opened one (anyNDI=false: the speculative pre-open can't know the future
            // route, and HW decode is the safe default — the NDI path re-opens without it when needed).
            var options = HaPlayPlaybackSession.BuildFileOpenOptions(anyNDI: false) with
            {
                AudioStreamIndex = audioTrackIndex,
            };
            decoder = MediaContainerDecoder.Open(path, options.ToVideoDecoderOpenOptions());
            Trace.LogDebug("PlaylistDecoderCache: pre-opened {Path}", path);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Trace.LogTrace("PlaylistDecoderCache: failed to pre-open {Path}: {Err}", path, ex.Message);
            return;
        }

        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                decoder.Dispose();
                return;
            }

            if (_entries.ContainsKey(key))
            {
                decoder.Dispose();
                return;
            }

            _entries[key] = new CacheEntry(decoder, DateTime.UtcNow);

            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.MinBy(kv => kv.Value.OpenedUtc);
                if (_entries.Remove(oldest.Key, out var evicted))
                {
                    Trace.LogDebug("PlaylistDecoderCache: evicted {Path}", oldest.Key);
                    HaPlayCleanup.TryDispose(evicted.Decoder, "PlaylistDecoderCache: dispose evicted decoder");
                }
            }
        }
    }

    public void PreOpenAsync(IEnumerable<(string Path, int? AudioTrackIndex)> items, CancellationToken ct = default)
    {
        foreach (var (path, track) in items)
        {
            var p = path;
            var t = track;
            _ = Task.Run(() => PreOpen(p, t, ct), ct);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                HaPlayCleanup.TryDispose(entry.Decoder, "PlaylistDecoderCache.InvalidateAll: dispose decoder");
            }
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                HaPlayCleanup.TryDispose(entry.Decoder, "PlaylistDecoderCache.Dispose: dispose decoder");
            }
            _entries.Clear();
        }
    }

    private sealed record CacheEntry(MediaContainerDecoder Decoder, DateTime OpenedUtc);
}
