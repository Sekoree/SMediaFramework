using S.Media.Session;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Playback;

/// <summary>
/// Maps a GUI <see cref="CueList"/> onto the framework's headless <see cref="ShowDocument"/> — the bridge
/// that lets the cue workspace run on <see cref="S.Media.Session.ShowSession"/> instead of the ported
/// <see cref="CuePlaybackEngine"/> (Phase 8 "full superset" convergence, slice 8a).
/// </summary>
/// <remarks>
/// Lossless today for the cue core: the media/group node tree flattens to ordered cues carrying a
/// <see cref="CueDefinition.GroupId"/> (nested groups collapse onto their outermost transport unit); each media
/// cue maps to a <see cref="ShowClipBinding"/> with its clip playback params (trim / fade / loop / end-behaviour)
/// and <em>all</em> of its composition placements (primary + <see cref="ShowClipBinding.ExtraPlacements"/>, fanned
/// out at play time); and each <see cref="CueComposition"/> maps to a <see cref="ShowComposition"/> including its
/// output-mapping warp sections (affine + mesh).
/// <para>Deferred (tracked, surfaced inline where they bite): action/comment cues; group fire modes are resolved
/// by the VM trigger plan, not the document; corner-pin (the framework section is affine + mesh only —
/// <see cref="CueOutputMappingSection.Corners"/> is dropped); and the GUI string cue number (cues are renumbered
/// 1..N by document order).</para>
/// </remarks>
public static class HaPlayShowMapper
{
    /// <summary>Resolves every composition/output binding to the mapping the runtime should use. Enabled
    /// bindings without persisted geometry receive the same implicit native-size tile shown by the layout
    /// editor; disabled mappings remain raw. This prevents the first layout save from appearing to resize an
    /// output that the editor already depicted as a tile.</summary>
    public static IReadOnlyDictionary<Guid, CueOutputMapping?> ResolveEffectiveVideoOutputMappings(
        CueList cueList,
        IReadOnlyList<OutputDefinition> outputs)
    {
        ArgumentNullException.ThrowIfNull(cueList);
        ArgumentNullException.ThrowIfNull(outputs);
        var result = new Dictionary<Guid, CueOutputMapping?>();
        var definitions = outputs.GroupBy(d => d.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var composition in cueList.Compositions)
        {
            var bindings = cueList.VideoOutputs
                .Where(binding => binding.CompositionId == composition.Id && binding.OutputLineId != Guid.Empty)
                .ToArray();
            if (bindings.Length == 0)
                continue;

            var layout = CompositionOutputLayoutViewModel.Build(
                composition.Width,
                composition.Height,
                bindings.Select(binding =>
                {
                    int? width = null;
                    int? height = null;
                    definitions.TryGetValue(binding.OutputLineId, out var definition);
                    if (definition is not null
                        && HaPlayPlaybackHelpers.TryGetOutputResolution(definition, out var w, out var h))
                    {
                        width = w;
                        height = h;
                    }

                    return (binding.OutputLineId, definition?.DisplayName ?? string.Empty, width, height, binding.Mapping);
                }));

            foreach (var binding in bindings)
            {
                if (!binding.MappingEnabled)
                    result[binding.Id] = null;
                else if (binding.Mapping is not null)
                    result[binding.Id] = binding.Mapping;
                else
                {
                    var item = layout.Items.First(i => i.OutputLineId == binding.OutputLineId);
                    result[binding.Id] = layout.ToMapping(item);
                }
            }
        }

        return result;
    }

    /// <summary>Builds a runnable <see cref="ShowDocument"/> from a GUI cue list. Pass the output definitions
    /// (<c>OutputManagement.DefinitionsSnapshot</c>) to resolve per-cue audio routes onto their real devices;
    /// omit them and clips fall back to the per-group/default output.</summary>
    public static ShowDocument ToShowDocument(CueList cueList, IReadOnlyList<OutputDefinition>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(cueList);

        var outputsById = outputs?.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First())
                          ?? new Dictionary<Guid, OutputDefinition>();
        var cues = new List<CueDefinition>();
        var clips = new List<ShowClipBinding>();
        var number = 0;

        void Walk(IEnumerable<CueNode> nodes, string? groupId)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case CueGroupNode group:
                        // A top-level group is one transport/clock unit: its cues share a SessionClock
                        // (so they seek/pause together and, when fired simultaneously, stay phase-locked).
                        // Nested subgroups collapse into their OUTERMOST ancestor (first non-null wins) so
                        // the whole tree moves as one unit rather than splitting across per-subgroup clocks.
                        // WHICH cues fire on GO — including per-subgroup fire modes (FirstCueOnly / …) — is
                        // resolved by the VM's trigger plan and fired by explicit cue id, so it needs no
                        // representation in the ShowDocument.
                        Walk(group.Children, groupId ?? group.Id.ToString());
                        break;

                    case MediaCueNode media:
                        var cueId = media.Id.ToString();
                        cues.Add(new CueDefinition(
                            Id: cueId,
                            Number: ++number,
                            Label: string.IsNullOrEmpty(media.Label) ? cueId : media.Label,
                            PreWait: TimeSpan.FromMilliseconds(media.PreWaitMs),
                            GroupId: groupId));

                        if (MapClip(cueId, media, outputsById) is { } binding)
                            clips.Add(binding);
                        break;

                    // ActionCueNode / CommentCueNode have no ShowDocument equivalent yet (deferred slice).
                }
            }
        }

        Walk(cueList.Nodes, groupId: null);

        var compositions = cueList.Compositions.Select(MapComposition).ToArray();

        return ShowDocument.Empty with
        {
            Cues = cues,
            Clips = clips,
            Compositions = compositions,
        };
    }

    private static ShowClipBinding? MapClip(
        string cueId, MediaCueNode media, IReadOnlyDictionary<Guid, OutputDefinition> outputsById)
    {
        // Text cues encode their render spec + duration into a `text:` URI so the ShowSession `text:` provider can
        // render + play them (NXT-06); every other source resolves to a path/scheme URI.
        var mediaPath = media.Source is TextPlaylistItem text
            ? TextSourceUri.Encode(text, media.DurationMs)
            : ResolveMediaPath(media.Source);
        if (mediaPath is null)
            return null; // media cue with no resolvable source (unbound) — nothing to play yet.

        // A cue may place its one decoded source onto several composition layers at once (PiP, the same feed in
        // two regions, or mirrored to a second canvas). Bound placements only (empty composition id = unbound),
        // ordered by layer index like the legacy engine. The first fills the binding's primary fields; the rest
        // become ExtraPlacements — ShowSession fans the video out to every one.
        var placements = media.VideoPlacements
            .Where(p => p.CompositionId != Guid.Empty)
            .OrderBy(p => p.LayerIndex)
            .ToList();
        var primary = placements.Count > 0 ? placements[0] : null;
        var extra = placements.Count > 1
            ? placements.Skip(1)
                .Select(p => new ShowClipPlacement(
                    p.CompositionId.ToString(), p.LayerIndex, ToShowVideoPlacement(p)))
                .ToArray()
            : null;
        return new ShowClipBinding(
            CueId: cueId,
            MediaPath: mediaPath,
            CompositionId: primary?.CompositionId.ToString(),
            LayerIndex: primary?.LayerIndex ?? 0,
            AudioStreamIndex: media.AudioTrackIndex)
        {
            StartOffset = TimeSpan.FromMilliseconds(media.StartOffsetMs),
            EndOffset = TimeSpan.FromMilliseconds(media.EndOffsetMs),
            FadeIn = TimeSpan.FromMilliseconds(media.FadeInMs),
            FadeOut = TimeSpan.FromMilliseconds(media.FadeOutMs),
            Loop = media.Loop || media.EndBehavior == CueEndBehavior.Loop,
            EndBehavior = MapEndBehavior(media.EndBehavior),
            // A text cue plays a held frame that never signals EOF, so end it at its duration via the time-based
            // monitor (EndAtDuration) rather than by source exhaustion — otherwise a resize/live-edit re-read ends
            // it early. Only when a positive duration is set; a 0-duration text cue holds until the next cue.
            EndAtDuration = media.Source is TextPlaylistItem && media.DurationMs > 0,
            // Primary placement's full appearance; the fit enum name maps straight to the framework's fit string
            // (MapFit lowercases it). Any additional placements ride along in ExtraPlacements.
            Placement = primary is null ? null : ToShowVideoPlacement(primary),
            ExtraPlacements = extra is { Length: > 0 } ? extra : null,
            // Per-cue audio routing → per-clip outputs (device + N→M channel map + gain), so the cue plays on
            // exactly its routed lines. Null when the cue declares no routes (then the group/default output).
            AudioRoutes = MapAudioRoutes(media, outputsById),
        };
    }

    /// <summary>GUI per-cue <see cref="CueAudioRoute"/>s → per-clip <see cref="ShowClipAudioRoute"/>s, one per
    /// output line: the line's PortAudio device, an N→M <see cref="ChannelMap"/> array (out-channel ← src-channel,
    /// unrouted = silent), and a line gain (mean of its routes' dB → linear). Muted routes are dropped; a fully
    /// muted line contributes no output. Returns an explicit empty list when the cue has no usable routes so
    /// HaPlay never falls back to an inferred/default device.</summary>
    private static IReadOnlyList<ShowClipAudioRoute>? MapAudioRoutes(
        MediaCueNode media, IReadOnlyDictionary<Guid, OutputDefinition> outputsById)
        => MapAudioRoutes(media.AudioRoutes, outputsById);

    /// <summary>Live-edit entry: map a cue's edited <see cref="CueAudioRoute"/>s with the current output
    /// definitions, for <c>ShowSession.ApplyActiveAudioRoutesAsync</c>. Same conversion + ordering as the load
    /// path so the <c>clip{i}</c> output order lines up with what the fire path attached.</summary>
    public static IReadOnlyList<ShowClipAudioRoute> MapActiveAudioRoutes(
        IReadOnlyList<CueAudioRoute> routes, IReadOnlyList<OutputDefinition>? outputs)
    {
        var outputsById = outputs?.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First())
                          ?? new Dictionary<Guid, OutputDefinition>();
        return MapAudioRoutes(routes, outputsById);
    }

    private static IReadOnlyList<ShowClipAudioRoute> MapAudioRoutes(
        IReadOnlyList<CueAudioRoute>? cueRoutes, IReadOnlyDictionary<Guid, OutputDefinition> outputsById)
    {
        if (cueRoutes is not { Count: > 0 } routes)
            return []; // HaPlay is manual-route-only: no cue routes means deliberately silent.

        var mapped = new List<ShowClipAudioRoute>();
        foreach (var line in routes.Where(r => !r.Muted && r.OutputChannel > 0).GroupBy(r => r.OutputLineId))
        {
            var lineRoutes = line.ToList();
            // Cue output channels are operator-facing and 1-based (1..N); ChannelMap is zero-based.
            // Treating the persisted value as an array index turned a normal stereo 1/2 route into
            // a three-channel [-1, L, R] output. PortAudio then rejected that format on a 2-channel
            // device, so the ShowSession cue faulted as soon as it was fired.
            var matrix = new int[lineRoutes.Max(r => r.OutputChannel)];
            Array.Fill(matrix, -1); // ChannelMap.Silence — channels with no route stay silent
            foreach (var r in lineRoutes)
                if (r.SourceChannel >= 0)
                    matrix[r.OutputChannel - 1] = r.SourceChannel;

            outputsById.TryGetValue(line.Key, out var def);
            var deviceId = (def as PortAudioOutputDefinition)?.EffectiveAudioBackendDeviceId;
            var sampleRate = def switch
            {
                PortAudioOutputDefinition pa => pa.SampleRate,
                NDIOutputDefinition ndi => ndi.AudioSampleRate,
                _ => 0,
            };
            var gain = (float)Math.Pow(10, lineRoutes.Average(r => r.GainDb) / 20.0);
            mapped.Add(new ShowClipAudioRoute(deviceId, matrix, gain, sampleRate > 0 ? sampleRate : null));
        }

        return mapped; // all invalid/muted routes is also explicitly silent, never an implicit default device.
    }

    /// <summary>GUI media source → a registry path / URI (D2). Files and images map to their path; live
    /// sources to a <c>scheme:</c> URI. Text cues are handled by the caller (encoded into a <c>text:</c> URI with
    /// their duration, since that needs the cue node, not just the source).</summary>
    private static string? ResolveMediaPath(PlaylistItem? source) => source switch
    {
        FilePlaylistItem f => f.Path,
        ImagePlaylistItem i => i.Path,
        NDIInputPlaylistItem n => $"ndi://{n.SourceName}",
        PortAudioInputPlaylistItem p => $"padev://{p.DeviceName}",
        _ => null,
    };

    private static ClipEndBehavior MapEndBehavior(CueEndBehavior behavior) => behavior switch
    {
        CueEndBehavior.Stop => ClipEndBehavior.Stop,
        CueEndBehavior.FreezeLastFrame => ClipEndBehavior.FreezeLastFrame,
        CueEndBehavior.Loop => ClipEndBehavior.Loop,
        CueEndBehavior.FadeOutAndStop => ClipEndBehavior.FadeOutAndStop,
        _ => ClipEndBehavior.Stop,
    };

    private static ShowComposition MapComposition(CueComposition composition) => new(
        Id: composition.Id.ToString(),
        Name: composition.Name,
        Width: composition.Width,
        Height: composition.Height,
        FrameRateNum: composition.FrameRateNum,
        FrameRateDen: composition.FrameRateDen,
        OutputMapping: composition is { VideoFxEnabled: true, VideoFx: { } fx } ? ToClipOutputMapping(fx) : null);

    /// <summary>Maps HaPlay's top-left-origin placement to the compositor's bottom-left destination axis.</summary>
    public static ShowVideoPlacement ToShowVideoPlacement(CueVideoPlacement placement) => new(
        placement.DestX,
        1.0 - placement.DestY - placement.DestHeight,
        placement.DestWidth,
        placement.DestHeight,
        placement.Opacity,
        placement.Position.ToString(),
        placement.RotationDegrees,
        placement.CropLeft,
        placement.CropTop,
        placement.CropRight,
        placement.CropBottom,
        placement.VideoFxEnabled ? ToClipOutputMapping(placement.VideoFx) : null);

    /// <summary>Maps a persisted HaPlay warp/FX model to the session runtime representation.</summary>
    public static ClipOutputMappingSpec? ToClipOutputMapping(CueOutputMapping? mapping) => mapping is null ? null : new(
        Sections: mapping.Sections.Select(MapSection).ToArray(),
        OutputWidth: mapping.OutputWidth,
        OutputHeight: mapping.OutputHeight);

    private static ClipOutputMappingSection MapSection(CueOutputMappingSection section) => new(
        Id: section.Id.ToString(),
        Enabled: section.Enabled,
        SrcX: section.SrcX, SrcY: section.SrcY, SrcWidth: section.SrcWidth, SrcHeight: section.SrcHeight,
        DestX: section.DestX, DestY: section.DestY, DestWidth: section.DestWidth, DestHeight: section.DestHeight,
        RotationDegrees: section.RotationDegrees,
        Opacity: section.Opacity,
        Brightness: section.Brightness,
        MeshColumns: section.MeshColumns,
        MeshRows: section.MeshRows,
        // section.Corners is Phase-3-reserved corner-pin (CueList: "ignored in Phase 1"): no editor produces it
        // and no compositor consumes it — the shipping path drops it too, so omitting it here is exact parity, not
        // a ShowSession regression. When Phase 3 lands, corners will bake to a fine MeshPoints grid (the GL warp is
        // already perspective-correct), so no framework change is needed — only this mapper + the editor.
        MeshPoints: section.MeshPoints?.Select(p => new ClipMeshPoint(p.X, p.Y)).ToArray());
}
