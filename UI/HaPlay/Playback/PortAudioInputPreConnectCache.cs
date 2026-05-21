using HaPlay.Models;
using S.Media.Core.Audio;
using S.Media.PortAudio;

namespace HaPlay.Playback;

/// <summary>Pre-started <see cref="PortAudioInput"/> instances for upcoming PortAudio media cues (§6.11).</summary>
internal sealed class PortAudioInputPreConnectCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    public static string BuildCacheKey(PortAudioInputPlaylistItem item) =>
        $"pa:{item.DeviceName}|{item.SampleRate}|{item.Channels}|gi:{item.GlobalDeviceIndex}";

    public bool HasMatchingEntry(Guid cueId, string cacheKey)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(cueId, out var e)
                   && string.Equals(e.CacheKey, cacheKey, StringComparison.Ordinal);
        }
    }

    public bool TryTake(Guid cueId, string cacheKey, out PortAudioInput? input, out AudioFormat format)
    {
        input = null;
        format = default;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(cueId, out var entry))
                return false;
            if (!string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal))
                return false;

            _entries.Remove(cueId);
            input = entry.Input;
            format = entry.Format;
            return true;
        }
    }

    public void Store(Guid cueId, string cacheKey, PortAudioInput input, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Input, input))
                    return;
                try { existing.Input.Dispose(); } catch { /* best effort */ }
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, input, format);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                try { e.Input.Dispose(); } catch { /* best effort */ }
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
        try { entry.Input.Dispose(); } catch { /* best effort */ }
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

    private sealed record Entry(string CacheKey, PortAudioInput Input, AudioFormat Format, DateTime CreatedUtc)
    {
        public Entry(string cacheKey, PortAudioInput input, AudioFormat format)
            : this(cacheKey, input, format, DateTime.UtcNow)
        {
        }
    }
}
