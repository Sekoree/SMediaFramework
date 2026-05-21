using HaPlay.Models;
using S.Media.Core.Audio;
using S.Media.NDI.Audio;

namespace HaPlay.Playback;

/// <summary>Pre-connected <see cref="NDIAudioReceiver"/> instances for upcoming NDI media cues (§6.11).</summary>
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

    public bool TryTake(Guid cueId, string cacheKey, out NDIAudioReceiver? receiver, out AudioFormat format)
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

    public void Store(Guid cueId, string cacheKey, NDIAudioReceiver receiver, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Receiver, receiver))
                    return;
                try { existing.Receiver.Dispose(); } catch { /* best effort */ }
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, receiver, format);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                try { e.Receiver.Dispose(); } catch { /* best effort */ }
            }
            _entries.Clear();
        }
    }

    public void EvictExcept(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keep = keepCueIds.ToHashSet();
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                RemoveEntryLocked(id);

            while (_entries.Count > maxEntries)
            {
                var oldest = _entries.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                RemoveEntryLocked(oldest);
            }
        }
    }

    private void RemoveEntryLocked(Guid cueId)
    {
        if (!_entries.Remove(cueId, out var entry))
            return;
        try { entry.Receiver.Dispose(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            InvalidateAll();
        }
    }

    private sealed record Entry(string CacheKey, NDIAudioReceiver Receiver, AudioFormat Format, DateTime CreatedUtc)
    {
        public Entry(string cacheKey, NDIAudioReceiver receiver, AudioFormat format)
            : this(cacheKey, receiver, format, DateTime.UtcNow)
        {
        }
    }
}
