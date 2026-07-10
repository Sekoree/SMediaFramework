namespace S.Control;

/// <summary>
/// Layer-level slices of a <see cref="ControlSystemConfig"/> (save/load rework 2026-06-10):
/// "export one layer / import a layer into another show". A slice is itself a plain
/// <see cref="ControlSystemConfig"/> carrying only the layers and their scripts - same file
/// format as a full control config, so any consumer that can read configs can read slices.
/// </summary>
public static class ControlConfigSlices
{
    /// <summary>
    /// Extracts <paramref name="layerIds"/> with every script that belongs to them (listed in
    /// <see cref="ControlLayerConfig.ScriptIds"/> or layer-scoped via
    /// <see cref="ControlScriptConfig.LayerId"/>). Everything else (devices, listeners, monitor
    /// options) is left at defaults - a slice describes the layer, not the rig.
    /// </summary>
    public static ControlSystemConfig ExtractLayers(ControlSystemConfig config, IReadOnlyCollection<Guid> layerIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(layerIds);

        var layers = config.Layers.Where(l => layerIds.Contains(l.Id)).ToList();
        var referenced = layers.SelectMany(l => l.ScriptIds).ToHashSet();
        var scripts = config.Scripts
            .Where(s => referenced.Contains(s.Id) || (s.LayerId is { } lid && layerIds.Contains(lid)))
            .ToList();
        return new ControlSystemConfig { Layers = layers, Scripts = scripts };
    }

    /// <summary>
    /// Merges a layer slice into <paramref name="target"/>. Layers replace by <em>name</em>
    /// (case-insensitive): a same-named target layer and its scripts are removed first, so
    /// re-importing an updated slice is idempotent; new names append. Incoming script ids that
    /// collide with unrelated target scripts are regenerated (and remapped in their layer's
    /// <see cref="ControlLayerConfig.ScriptIds"/> / <see cref="ControlScriptConfig.LayerId"/>).
    /// </summary>
    public static ControlSystemConfig MergeLayers(ControlSystemConfig target, ControlSystemConfig slice)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(slice);

        var layers = target.Layers.ToList();
        var scripts = target.Scripts.ToList();

        foreach (var incomingLayer in slice.Layers)
        {
            // Drop the same-named target layer and the scripts it owned.
            var existing = layers.FirstOrDefault(l =>
                string.Equals(l.Name, incomingLayer.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                layers.Remove(existing);
                var owned = existing.ScriptIds.ToHashSet();
                scripts.RemoveAll(s => owned.Contains(s.Id) || s.LayerId == existing.Id);
            }

            // Bring the incoming layer's scripts over, regenerating ids that collide with the
            // remaining (unrelated) target scripts.
            var incomingScripts = slice.Scripts
                .Where(s => incomingLayer.ScriptIds.Contains(s.Id) || s.LayerId == incomingLayer.Id)
                .ToList();
            var usedIds = scripts.Select(s => s.Id).ToHashSet();
            var layerToAdd = incomingLayer;
            foreach (var incoming in incomingScripts)
            {
                var script = incoming;
                if (!usedIds.Add(script.Id))
                {
                    var fresh = Guid.NewGuid();
                    layerToAdd = layerToAdd with
                    {
                        ScriptIds = layerToAdd.ScriptIds.Select(id => id == script.Id ? fresh : id).ToList(),
                    };
                    script = script with { Id = fresh };
                    usedIds.Add(fresh);
                }

                if (script.LayerId is not null)
                    script = script with { LayerId = layerToAdd.Id };
                scripts.Add(script);
            }

            layers.Add(layerToAdd);
        }

        return target with { Layers = layers, Scripts = scripts };
    }
}
