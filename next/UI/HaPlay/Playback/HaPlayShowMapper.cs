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
/// <see cref="CueDefinition.GroupId"/>; each media cue maps to a <see cref="ShowClipBinding"/> with its
/// clip playback params (trim / fade / loop / end-behaviour) and its first composition placement; and
/// each <see cref="CueComposition"/> maps to a <see cref="ShowComposition"/> including its output-mapping
/// warp sections (affine + mesh).
/// <para>Deferred (tracked, surfaced inline where they bite): action/comment cues; nested groups beyond
/// one level and group fire modes; multi-composition cues (a clip binds one composition); corner-pin
/// (the framework section is affine +
/// mesh only — <see cref="CueOutputMappingSection.Corners"/> is dropped); and the GUI string cue number
/// (cues are renumbered 1..N by document order).</para>
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
                        && HaPlayPlaybackSession.TryGetOutputResolution(definition, out var w, out var h))
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
                        // One level of grouping today: the group's children inherit its id as their
                        // GroupId so GoAsync(groupId) fires them together. Nested groups + fire modes
                        // (FirstCueOnly / …) are a later slice.
                        Walk(group.Children, group.Id.ToString());
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
        if (ResolveMediaPath(media.Source) is not { } mediaPath)
            return null; // media cue with no resolvable source (unbound / text) — nothing to play yet.

        var placement = media.VideoPlacements.FirstOrDefault();
        return new ShowClipBinding(
            CueId: cueId,
            MediaPath: mediaPath,
            CompositionId: placement?.CompositionId.ToString(),
            LayerIndex: placement?.LayerIndex ?? 0,
            AudioStreamIndex: media.AudioTrackIndex)
        {
            StartOffset = TimeSpan.FromMilliseconds(media.StartOffsetMs),
            EndOffset = TimeSpan.FromMilliseconds(media.EndOffsetMs),
            FadeIn = TimeSpan.FromMilliseconds(media.FadeInMs),
            FadeOut = TimeSpan.FromMilliseconds(media.FadeOutMs),
            Loop = media.Loop || media.EndBehavior == CueEndBehavior.Loop,
            EndBehavior = MapEndBehavior(media.EndBehavior),
            // First placement's full appearance. The fit enum name maps straight to the framework's fit
            // string (MapFit lowercases it). Multi-placement remains deferred: a clip binds one composition.
            Placement = placement is null ? null : ToShowVideoPlacement(placement),
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
    {
        if (media.AudioRoutes is not { Count: > 0 } routes)
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
    /// sources to a <c>scheme:</c> URI. Text cues are deferred (no headless text source binding yet).</summary>
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
        // Corner-pin (section.Corners) has no framework equivalent yet (affine + mesh only) — deferred.
        MeshPoints: section.MeshPoints?.Select(p => new ClipMeshPoint(p.X, p.Y)).ToArray());
}
