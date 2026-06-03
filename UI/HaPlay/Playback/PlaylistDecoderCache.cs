using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
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

    public MediaContainerDecoder? TryTake(string path)
    {
        lock (_gate)
        {
            if (_entries.Remove(path, out var entry))
            {
                Trace.LogDebug("PlaylistDecoderCache: hit for {Path}", path);
                return entry.Decoder;
            }
        }
        return null;
    }

    public void PreOpen(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_entries.ContainsKey(path)) return;
        }

        MediaContainerDecoder? decoder;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Match the live open path's deep read-ahead + large file read buffer so a cached track plays as
            // smoothly as a freshly-opened one (anyNDI=false: the speculative pre-open can't know the future
            // route, and HW decode is the safe default — the NDI path re-opens without it when needed).
            decoder = MediaContainerDecoder.Open(path,
                HaPlayPlaybackSession.BuildFileOpenOptions(anyNDI: false).ToVideoDecoderOpenOptions());
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

            if (_entries.ContainsKey(path))
            {
                decoder.Dispose();
                return;
            }

            _entries[path] = new CacheEntry(decoder, DateTime.UtcNow);

            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.MinBy(kv => kv.Value.OpenedUtc);
                if (_entries.Remove(oldest.Key, out var evicted))
                {
                    Trace.LogDebug("PlaylistDecoderCache: evicted {Path}", oldest.Key);
                    try { evicted.Decoder.Dispose(); } catch { }
                }
            }
        }
    }

    public void PreOpenAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var path in paths)
        {
            var p = path;
            _ = Task.Run(() => PreOpen(p, ct), ct);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                try { entry.Decoder.Dispose(); } catch { }
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
                try { entry.Decoder.Dispose(); } catch { }
            }
            _entries.Clear();
        }
    }

    private sealed record CacheEntry(MediaContainerDecoder Decoder, DateTime OpenedUtc);
}
