using S.Media.Core.Buses;
using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// Owns the per-composition visualizer slots for a <see cref="ShowSession"/>: attach/replace,
/// per-placement hot updates, fade snapshots, reload retention and persistent reattachment
/// (extracted from ShowSession per review P2-6 - one runtime responsibility, one owner).
/// </summary>
/// <remarks>
/// <para><strong>Dispatcher-confined.</strong> Every method must run on the owning session's
/// dispatcher; the session's public API provides the serialization (InvokeAsync) and the fade
/// pacing policy - this type only holds the slot state and its lifecycle rules.</para>
/// <para>One slot owns a LIST of surface layers because a visualizer cue can place the same source
/// into several sections of one canvas (#26 multi-placement); they attach, fade and detach
/// together, sharing one audio tap and one metadata registration.</para>
/// </remarks>
internal sealed class ShowSessionVisualizerService
{
    internal sealed record Layer(
        ClipCompositionRuntime.SurfaceLayerSlot Slot,
        VideoPlacementSpec Placement);

    internal sealed record Slot(
        IReadOnlyList<Layer> Layers,
        Guid TapId,
        IAudioVisualSource Source,
        bool DisposeSource,
        bool PreserveAcrossDocumentReload);

    /// <summary>Fade snapshot: slot identity makes the final detach safe when a new visualizer is
    /// fired onto the same composition while the old one is fading.</summary>
    internal sealed record FadeCapture(string CompositionId, Slot Captured, IReadOnlyList<float> StartOpacities);

    internal sealed record Reattachment(string CompositionId, Slot Captured, ClipCompositionRuntime Replacement);

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Func<IAudioVisualSource, Func<string, bool>?, Guid> _registerTap;
    private readonly Action<Guid> _detachTapFromActiveClips;
    private readonly Action<Guid> _releaseTapRegistration;
    private readonly BusMetadataHub _metadataHub;

    /// <param name="registerTap">Creates + attaches the visualizer's audio tap (session-owned tap
    /// list) and returns its id.</param>
    /// <param name="detachTapFromActiveClips">Removes the tap's routes from currently-playing clips.</param>
    /// <param name="releaseTapRegistration">Removes the tap from the session's registration list and
    /// disposes its cached rate adapters.</param>
    public ShowSessionVisualizerService(
        Func<IAudioVisualSource, Func<string, bool>?, Guid> registerTap,
        Action<Guid> detachTapFromActiveClips,
        Action<Guid> releaseTapRegistration,
        BusMetadataHub metadataHub)
    {
        _registerTap = registerTap;
        _detachTapFromActiveClips = detachTapFromActiveClips;
        _releaseTapRegistration = releaseTapRegistration;
        _metadataHub = metadataHub;
    }

    public bool Has(string compositionId) => _slots.ContainsKey(compositionId);

    /// <summary>Removes and fully tears down the composition's visualizer (no-op when absent).</summary>
    public void Remove(string compositionId)
    {
        if (_slots.Remove(compositionId, out var removed))
            DisposeSlot(removed);
    }

    /// <summary>
    /// Attaches <paramref name="source"/> as one surface layer per placement, replacing any existing
    /// visualizer on the composition. The replacement is STAGED first: a renderer/surface creation
    /// failure must not tear down the currently-live visualizer.
    /// </summary>
    public void Attach(
        string compositionId,
        ClipCompositionRuntime composition,
        Compositor.ILayerSurfaceVideoSource surfaceSource,
        IAudioVisualSource source,
        IReadOnlyList<VideoPlacementSpec> placements,
        bool disposeSourceOnRemove,
        Func<string, bool>? audioFeedFilter,
        bool preserveAcrossDocumentReload)
    {
        var stagedLayers = new List<Layer>(placements.Count);
        try
        {
            foreach (var spec in placements)
                stagedLayers.Add(new Layer(
                    composition.AddSurfaceLayer(surfaceSource.CreateLayerSurface(), spec), spec));
        }
        catch
        {
            foreach (var staged in stagedLayers)
                staged.Slot.Dispose();
            throw;
        }
        composition.EnsurePumpStarted();

        if (_slots.Remove(compositionId, out var existing))
            DisposeSlot(ReferenceEquals(existing.Source, source)
                ? existing with { DisposeSource = false }
                : existing);

        var tapId = _registerTap(source, audioFeedFilter);
        if (source is IBusMetadataSink sink)
            _metadataHub.Attach(sink);

        _slots[compositionId] = new Slot(
            stagedLayers, tapId, source, disposeSourceOnRemove, preserveAcrossDocumentReload);
    }

    /// <summary>Hot-updates one surface layer's placement; false when the composition has no
    /// visualizer or the index is out of range (see ShowSession.UpdateCompositionVisualizerPlacementAsync).</summary>
    public bool UpdatePlacement(string compositionId, VideoPlacementSpec placement, int placementIndex)
    {
        if (!_slots.TryGetValue(compositionId, out var slot)
            || placementIndex < 0 || placementIndex >= slot.Layers.Count)
            return false;
        var layer = slot.Layers[placementIndex];
        layer.Slot.UpdatePlacement(placement);
        var layers = slot.Layers.ToArray();
        layers[placementIndex] = layer with { Placement = placement };
        _slots[compositionId] = slot with { Layers = layers };
        return true;
    }

    /// <summary>Snapshots the slots (all, or one composition) for a fade: identities + start opacities.</summary>
    public IReadOnlyList<FadeCapture> CaptureForFade(string? compositionId) =>
        _slots
            .Where(pair => compositionId is null
                           || string.Equals(pair.Key, compositionId, StringComparison.Ordinal))
            .Select(pair => new FadeCapture(
                pair.Key, pair.Value,
                pair.Value.Layers.Select(l => l.Slot.Opacity).ToArray()))
            .ToArray();

    /// <summary>Applies one fade level to every captured slot that is still the live one. Returns
    /// false when nothing applied (every captured slot was replaced mid-fade).</summary>
    public bool ApplyFadeLevel(IReadOnlyList<FadeCapture> fades, float level)
    {
        var applied = false;
        foreach (var fade in fades)
        {
            if (!_slots.TryGetValue(fade.CompositionId, out var current)
                || !ReferenceEquals(current, fade.Captured))
                continue;
            for (var i = 0; i < fade.Captured.Layers.Count; i++)
                fade.Captured.Layers[i].Slot.Opacity = fade.StartOpacities[i] * level;
            applied = true;
        }

        return applied;
    }

    /// <summary>Detaches the faded slots - only those whose identity still matches, so a replacement
    /// fired during the fade is never torn down.</summary>
    public void FinalizeFade(IReadOnlyList<FadeCapture> fades)
    {
        foreach (var fade in fades)
        {
            if (!_slots.TryGetValue(fade.CompositionId, out var current)
                || !ReferenceEquals(current, fade.Captured))
                continue;
            _slots.Remove(fade.CompositionId);
            DisposeSlot(fade.Captured);
        }
    }

    /// <summary>Session-teardown clear: taps unregister and sources dispose; the surface layers
    /// themselves die with their owning compositions (the caller disposes those next).</summary>
    public void Clear()
    {
        foreach (var slot in _slots.Values)
            DisposeAuxiliaries(slot);

        _slots.Clear();
    }

    /// <summary>Reload-time cleanup that SPARES preserved compositions. Slots on a preserved
    /// composition are left intact; a persistent slot on a rebuilt composition keeps its durable
    /// parts (source/tap/filter) and is returned for reattachment after the composition map commits;
    /// every other slot gets the historical full-reload teardown.</summary>
    public List<Reattachment> RetainForPreservedCompositionsOnly(
        HashSet<string> preservedIds,
        IReadOnlyDictionary<string, ClipCompositionRuntime> replacementCompositions)
    {
        var reattachments = new List<Reattachment>();
        foreach (var (id, slot) in _slots.Where(kv => !preservedIds.Contains(kv.Key)).ToList())
        {
            if (slot.PreserveAcrossDocumentReload
                && replacementCompositions.TryGetValue(id, out var replacement)
                && replacement.SupportsSurfaceLayers
                && slot.Source is Compositor.ILayerSurfaceVideoSource)
            {
                reattachments.Add(new Reattachment(id, slot, replacement));
                continue;
            }

            DisposeAuxiliaries(slot);
            _slots.Remove(id);
        }

        return reattachments;
    }

    /// <summary>Recreates every persistent slot's surface layers on its replacement composition; a
    /// failed slot is fully torn down (auxiliaries included) rather than left half-attached.</summary>
    public void ReattachPersistent(IReadOnlyList<Reattachment> reattachments)
    {
        foreach (var pending in reattachments)
        {
            var recreated = new List<Layer>(pending.Captured.Layers.Count);
            try
            {
                var surfaceSource = (Compositor.ILayerSurfaceVideoSource)pending.Captured.Source;
                foreach (var old in pending.Captured.Layers)
                    recreated.Add(new Layer(
                        pending.Replacement.AddSurfaceLayer(surfaceSource.CreateLayerSurface(), old.Placement),
                        old.Placement));
                pending.Replacement.EnsurePumpStarted();
                _slots[pending.CompositionId] = pending.Captured with { Layers = recreated };
            }
            catch (Exception ex)
            {
                foreach (var layer in recreated)
                    MediaDiagnostics.SwallowDisposeErrors(layer.Slot.Dispose, "ShowSession: staged visualizer layer");
                _slots.Remove(pending.CompositionId);
                DisposeAuxiliaries(pending.Captured);
                MediaDiagnostics.LogWarning(
                    "ShowSession: persistent visualizer could not reattach to rebuilt composition '{0}' ({1}).",
                    pending.CompositionId, ex.Message);
            }
        }
    }

    /// <summary>Detaches one live visualizer: surface layers, active-clip tap routes, then the
    /// auxiliary registrations. The dictionary entry is removed by the caller FIRST so
    /// replacement/fade identity checks cannot tear down a newer source on the same composition.</summary>
    private void DisposeSlot(Slot slot)
    {
        foreach (var layer in slot.Layers)
            layer.Slot.Dispose();
        _detachTapFromActiveClips(slot.TapId);
        DisposeAuxiliaries(slot);
    }

    private void DisposeAuxiliaries(Slot slot)
    {
        _releaseTapRegistration(slot.TapId);
        if (slot.Source is IBusMetadataSink sink)
            _metadataHub.Detach(sink);
        if (slot.DisposeSource && slot.Source is IDisposable disposable)
            MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, "ShowSession: visualizer source");
    }
}
