using HaPlay.Models;
using HaPlay.ViewModels;

namespace HaPlay.Playback;

/// <summary>
/// Bounded cache of opened <see cref="HaPlayPlaybackSession"/> instances for upcoming file media cues (§5.7).
/// </summary>
internal sealed class CuePreRollCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    /// <summary>Raised on any membership change so the UI can refresh warming badges (Phase 5.7.2).
    /// Snapshot of the currently-warm cue ids is supplied — callers must not mutate it.</summary>
    public event Action<IReadOnlyCollection<Guid>>? EntriesChanged;

    public IReadOnlyCollection<Guid> SnapshotWarmCueIds()
    {
        lock (_gate)
        {
            return _entries.Keys.ToArray();
        }
    }

    private void RaiseEntriesChanged()
    {
        Guid[] snapshot;
        lock (_gate) snapshot = _entries.Keys.ToArray();
        EntriesChanged?.Invoke(snapshot);
    }

    public bool HasMatchingEntry(Guid cueId, string cacheKey)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(cueId, out var entry)
                   && string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal);
        }
    }

    public bool TryTake(Guid cueId, string cacheKey, out HaPlayPlaybackSession? session, out PlaylistItem? item)
    {
        session = null;
        item = null;
        bool taken;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(cueId, out var entry))
                return false;
            if (!string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal))
                return false;

            _entries.Remove(cueId);
            session = entry.Session;
            item = entry.Item;
            taken = true;
        }
        if (taken) RaiseEntriesChanged();
        return taken;
    }

    public void Store(Guid cueId, string cacheKey, HaPlayPlaybackSession session, PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Session, session))
                    return;
                try { existing.Session.Dispose(); }
                catch { /* best effort */ }
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, session, item);
        }
        RaiseEntriesChanged();
    }

    public void InvalidateAll()
    {
        bool hadEntries;
        lock (_gate)
        {
            hadEntries = _entries.Count > 0;
            foreach (var entry in _entries.Values)
            {
                try { entry.Session.Dispose(); }
                catch { /* best effort */ }
            }
            _entries.Clear();
        }
        if (hadEntries) RaiseEntriesChanged();
    }

    public void EvictExcept(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        int countBefore, countAfter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            countBefore = _entries.Count;
            var keep = keepCueIds is HashSet<Guid> hs ? hs : keepCueIds.ToHashSet();
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                RemoveEntryLocked(id);

            while (_entries.Count > maxEntries)
            {
                var oldest = _entries.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                RemoveEntryLocked(oldest);
            }
            countAfter = _entries.Count;
        }
        if (countAfter != countBefore) RaiseEntriesChanged();
    }

    private void RemoveEntryLocked(Guid cueId)
    {
        if (!_entries.Remove(cueId, out var entry))
            return;
        try { entry.Session.Dispose(); }
        catch { /* best effort */ }
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

    public static string BuildCacheKey(
        PlaylistItem item,
        IReadOnlyList<OutputLineViewModel> outputs,
        HaPlayFilePlaybackOptions filePlayback)
    {
        var outputKey = string.Join("|", outputs.Select(l => l.Definition.Id.ToString("N")).OrderBy(x => x));
        return $"{item.CacheKey()}|{outputKey}|{filePlayback.OutputPreset}|{filePlayback.TransitionMode}|{filePlayback.TransitionDurationMs}|fi{filePlayback.CueFadeInMs}|fo{filePlayback.CueFadeOutMs}";
    }

    private sealed record Entry(string CacheKey, HaPlayPlaybackSession Session, PlaylistItem Item, DateTime CreatedUtc)
    {
        public Entry(string cacheKey, HaPlayPlaybackSession session, PlaylistItem item)
            : this(cacheKey, session, item, DateTime.UtcNow)
        {
        }
    }
}

internal static class PlaylistItemPreRollExtensions
{
    public static string CacheKey(this PlaylistItem item) =>
        item switch
        {
            FilePlaylistItem f => $"file:{f.Path}",
            NDIInputPlaylistItem ndi => NdiInputPreConnectCache.BuildCacheKey(ndi),
            PortAudioInputPlaylistItem pa => PortAudioInputPreConnectCache.BuildCacheKey(pa),
            _ => item.GetType().Name,
        };

    /// <summary>File media suitable for pre-roll (live inputs are excluded — they hold devices/NDI).</summary>
    public static bool SupportsPreRoll(this PlaylistItem? item) =>
        item is FilePlaylistItem
        || item is NDIInputPlaylistItem { VideoOnly: false }
        || item is PortAudioInputPlaylistItem;
}
