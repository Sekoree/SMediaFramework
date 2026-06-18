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
        PortAudioInput? toDispose = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Input, input))
                    return;
                toDispose = existing.Input;
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, input, format);
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
            try { e.Input.Dispose(); } catch { /* best effort */ }
        }
    }

    public void EvictExcept(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        var toDispose = new List<PortAudioInput>();
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
        foreach (var input in toDispose)
        {
            try { input.Dispose(); } catch { /* best effort */ }
        }
    }

    private void RemoveEntryLocked(Guid cueId, ICollection<PortAudioInput> toDispose)
    {
        if (!_entries.Remove(cueId, out var entry))
            return;
        toDispose.Add(entry.Input);
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
            try { e.Input.Dispose(); } catch { /* best effort */ }
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
