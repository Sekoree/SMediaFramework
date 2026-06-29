using S.Media.Core.Audio;
using S.Media.NDI;

namespace HaPlay.Playback;

/// <summary>Pre-connected <see cref="NDISource"/> instances for upcoming NDI media cues (§6.11).</summary>
internal sealed class NdiInputPreConnectCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    public static string BuildCacheKey(NDIInputPlaylistItem item) =>
        $"ndi:{item.SourceName}|lb:{item.LowBandwidth}|ao:{item.AudioOnly}|vo:{item.VideoOnly}";

    public bool HasMatchingEntry(Guid cueId, string cacheKey)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(cueId, out var e)
                   && string.Equals(e.CacheKey, cacheKey, StringComparison.Ordinal);
        }
    }

    public bool TryTake(Guid cueId, string cacheKey, out NDISource? receiver, out AudioFormat format)
    {
        receiver = null;
        format = default;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(cueId, out var entry))
                return false;
            if (!string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal))
                return false;

            _entries.Remove(cueId);
            receiver = entry.Receiver;
            format = entry.Format;
            return true;
        }
    }

    public void Store(Guid cueId, string cacheKey, NDISource receiver, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        NDISource? toDispose = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Receiver, receiver))
                    return;
                toDispose = existing.Receiver;
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, receiver, format);
        }
        try { toDispose?.Dispose(); } catch { /* best effort */ }
    }

    public void InvalidateAll()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }
        foreach (var e in entries)
        {
            try { e.Receiver.Dispose(); } catch { /* best effort */ }
        }
    }

    public void EvictExcept(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        var toDispose = new List<NDISource>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keep = keepCueIds.ToHashSet();
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                RemoveEntryLocked(id, toDispose);

            while (_entries.Count > maxEntries)
            {
                var oldest = _entries.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                RemoveEntryLocked(oldest, toDispose);
            }
        }
        foreach (var receiver in toDispose)
        {
            try { receiver.Dispose(); } catch { /* best effort */ }
        }
    }

    private void RemoveEntryLocked(Guid cueId, ICollection<NDISource> toDispose)
    {
        if (!_entries.Remove(cueId, out var entry))
            return;
        toDispose.Add(entry.Receiver);
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }
        foreach (var e in entries)
        {
            try { e.Receiver.Dispose(); } catch { /* best effort */ }
        }
    }

    private sealed record Entry(string CacheKey, NDISource Receiver, AudioFormat Format, DateTime CreatedUtc)
    {
        public Entry(string cacheKey, NDISource receiver, AudioFormat format)
            : this(cacheKey, receiver, format, DateTime.UtcNow)
        {
        }
    }
}
