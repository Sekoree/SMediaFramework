namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// Preset rotation shared by the continuous renderer and the layer surface: sequential or
/// shuffled advance over a fixed preset list, with a blocklist of presets that failed to load
/// (projectM's preset-switch-failed callback) so rotation skips them instead of leaving the
/// output on the previous/idle preset for a whole slot ("empty section"). Pure logic - the
/// callers own the native load calls and thread affinity (all access on the render thread).
/// </summary>
internal sealed class PresetRotation(string[] presets, bool shuffle)
{
    private readonly Random _random = new();
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private int _index = -1;

    public int Count => presets.Length;

    public int FailedCount => _failed.Count;

    /// <summary>True when every preset in the pack failed to load - callers stop rotating
    /// (projectM stays on its built-in idle preset) instead of spinning through the pack.</summary>
    public bool AllFailed => presets.Length > 0 && _failed.Count >= presets.Length;

    /// <summary>Advances to the next loadable preset (sequential, or shuffled without immediate
    /// repeats). False when the pack is empty or fully blocklisted.</summary>
    public bool TryAdvance(out string preset)
    {
        preset = string.Empty;
        var remaining = presets.Length - _failed.Count;
        if (remaining <= 0)
            return false;

        if (shuffle && remaining > 1)
        {
            // Uniform pick over the ELIGIBLE set (non-failed, and not the preset currently on
            // screen when another choice exists). Random probing with a bounded retry loop is NOT
            // sufficient here - a handful of unlucky probes can all land on blocklisted entries
            // and exhaust the bound, so we count-and-walk deterministically instead. O(pack size),
            // and advances happen once per preset slot, not per frame.
            var entryIndex = _index;
            var excludeEntry = (uint)entryIndex < (uint)presets.Length
                && !_failed.Contains(presets[entryIndex]);
            var eligible = excludeEntry ? remaining - 1 : remaining;
            if (eligible <= 0)
            {
                eligible = remaining;
                excludeEntry = false;
            }

            var pick = _random.Next(eligible);
            for (var i = 0; i < presets.Length; i++)
            {
                if (_failed.Contains(presets[i]) || (excludeEntry && i == entryIndex))
                    continue;
                if (pick-- == 0)
                {
                    _index = i;
                    preset = presets[i];
                    return true;
                }
            }

            return false; // unreachable: eligible was counted from the same predicates
        }

        // Sequential: step past blocklisted entries; bounded by the pack size and guaranteed to
        // land by the remaining>0 guard above.
        for (var attempts = 0; attempts < presets.Length; attempts++)
        {
            _index = (_index + 1) % presets.Length;
            var candidate = presets[_index];
            if (_failed.Contains(candidate))
                continue;
            preset = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Blocklists a preset reported by projectM's switch-failed callback. Returns true
    /// the FIRST time this path is marked (callers log each offender once).</summary>
    public bool MarkFailed(string presetPath) => _failed.Add(presetPath);

}
