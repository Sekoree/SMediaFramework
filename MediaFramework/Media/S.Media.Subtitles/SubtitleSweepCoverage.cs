namespace S.Media.Subtitles;

/// <summary>
/// Tracks which timeline regions a streaming subtitle demux has swept, so a render position outside
/// them can trigger a seek-aware sweep instead of waiting for the sequential one. One sweep is active
/// at a time: it begins at a start position and its frontier only advances; beginning the next sweep
/// (after a seek) archives the current one into a merged interval set. A position counts as covered
/// when it lies inside an archived interval or within the active sweep plus a near-ahead grace - the
/// sweep demuxes far faster than real time, so a position slightly past the frontier arrives shortly
/// and is not worth a seek (the grace must exceed the caller's seek preroll or the region just seeked
/// to would re-request itself forever).
/// </summary>
/// <remarks>Thread-safe: renders query <see cref="IsCovered"/> while the demux pump advances state.</remarks>
public sealed class SubtitleSweepCoverage(long nearAheadMs)
{
    private readonly object _gate = new();
    private readonly List<(long Start, long End)> _archived = []; // sorted, non-overlapping, merged
    private long _sweepStart = -1;    // -1 = no active sweep
    private long _sweepFrontier = -1;

    /// <summary>Starts a sweep at <paramref name="startMs"/>, archiving the active one (if any).</summary>
    public void BeginSweep(long startMs)
    {
        lock (_gate)
        {
            ArchiveActiveLocked();
            _sweepStart = Math.Max(0, startMs);
            _sweepFrontier = _sweepStart;
        }
    }

    /// <summary>Advances the active sweep's frontier (regressions are ignored - a seek that landed on
    /// an earlier cluster only widens what actually gets decoded, not what was promised).</summary>
    public void AdvanceFrontier(long frontierMs)
    {
        lock (_gate)
        {
            if (_sweepStart >= 0 && frontierMs > _sweepFrontier)
                _sweepFrontier = frontierMs;
        }
    }

    /// <summary>Marks the active sweep as having reached the end of the stream: everything from its
    /// start onward is covered, and no sweep remains active.</summary>
    public void CompleteToEnd()
    {
        lock (_gate)
        {
            if (_sweepStart < 0)
                return;
            _sweepFrontier = long.MaxValue;
            ArchiveActiveLocked();
        }
    }

    /// <summary>True when <paramref name="ms"/> lies in an archived interval or within the active sweep
    /// (start … frontier + the near-ahead grace).</summary>
    public bool IsCovered(long ms)
    {
        if (ms < 0)
            ms = 0;
        lock (_gate)
        {
            if (_sweepStart >= 0 && ms >= _sweepStart
                && ms - _sweepFrontier <= nearAheadMs) // subtraction, not addition: frontier may be long.MaxValue
                return true;
            foreach (var (start, end) in _archived)
            {
                if (ms < start)
                    return false; // sorted - no later interval can contain it
                if (ms <= end)
                    return true;
            }
            return false;
        }
    }

    /// <summary>True once the archived intervals merge to a single [0, end-of-stream] span - nothing can
    /// ever be uncovered again, so the demux can shut down for good.</summary>
    public bool IsFullyCovered
    {
        get
        {
            lock (_gate)
                return _archived is [(<= 0, long.MaxValue)];
        }
    }

    private void ArchiveActiveLocked()
    {
        if (_sweepStart < 0)
            return;
        var start = _sweepStart;
        var end = _sweepFrontier;
        _sweepStart = -1;
        _sweepFrontier = -1;

        // Insert-merge: absorb every archived interval that touches [start, end].
        for (var i = _archived.Count - 1; i >= 0; i--)
        {
            var (s, e) = _archived[i];
            if (e < start - 1 || s > (end == long.MaxValue ? long.MaxValue : end + 1))
                continue;
            start = Math.Min(start, s);
            end = Math.Max(end, e);
            _archived.RemoveAt(i);
        }

        var at = _archived.FindIndex(x => x.Start > start);
        _archived.Insert(at < 0 ? _archived.Count : at, (start, end));
    }
}
