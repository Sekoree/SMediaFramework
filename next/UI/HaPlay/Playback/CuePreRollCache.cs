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
        HaPlayPlaybackSession? toDispose = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(cueId, out var existing))
            {
                if (ReferenceEquals(existing.Session, session))
                    return;
                toDispose = existing.Session;
                _entries.Remove(cueId);
            }

            _entries[cueId] = new Entry(cacheKey, session, item);
        }
        try { toDispose?.Dispose(); }
        catch { /* best effort */ }
        RaiseEntriesChanged();
    }

    public void InvalidateAll()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }
        foreach (var entry in entries)
        {
            try { entry.Session.Dispose(); }
            catch { /* best effort */ }
        }
        if (entries.Length > 0) RaiseEntriesChanged();
    }

    public void EvictExcept(IReadOnlyCollection<Guid> keepCueIds, int maxEntries)
    {
        int countBefore, countAfter;
        var toDispose = new List<HaPlayPlaybackSession>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            countBefore = _entries.Count;
            var keep = keepCueIds is HashSet<Guid> hs ? hs : keepCueIds.ToHashSet();
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                RemoveEntryLocked(id, toDispose);

            while (_entries.Count > maxEntries)
            {
                var oldest = _entries.OrderBy(kv => kv.Value.CreatedUtc).First().Key;
                RemoveEntryLocked(oldest, toDispose);
            }
            countAfter = _entries.Count;
        }
        foreach (var session in toDispose)
        {
            try { session.Dispose(); }
            catch { /* best effort */ }
        }
        if (countAfter != countBefore) RaiseEntriesChanged();
    }

    private void RemoveEntryLocked(Guid cueId, ICollection<HaPlayPlaybackSession> toDispose)
    {
        if (!_entries.Remove(cueId, out var entry))
            return;
        toDispose.Add(entry.Session);
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
        foreach (var entry in entries)
        {
            try { entry.Session.Dispose(); }
            catch { /* best effort */ }
        }
        if (entries.Length > 0) RaiseEntriesChanged();
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

    /// <summary>Items that can be warmed before GO: files pre-open decoders, live inputs pre-connect devices.</summary>
    public static bool SupportsPreRoll(this PlaylistItem? item) =>
        item is FilePlaylistItem
        || item is NDIInputPlaylistItem { VideoOnly: false }
        || item is PortAudioInputPlaylistItem;
}
