using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>Bundle of every cue list in the cue player workspace, persisted in <c>.haplaycuelists</c> files.</summary>
public sealed record CueListsCollectionDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Generator { get; init; }

    public List<CueList> CueLists { get; init; } = [];
}

/// <summary>
/// Cue-list root persisted in <c>.haplaycues</c> files. A cue list is a tree of groups and cues
/// plus its own list of compositions and outputs - the Cue Player is a self-contained playback
/// surface and does not borrow routing from MediaPlayer tabs.
/// </summary>
public sealed record CueList
{
    public string Schema { get; init; } = "HaPlayCueList/v3";

    public string Name { get; init; } = "Cue List";

    /// <summary>Legacy persisted standby-window cap. Ignored by the current cue runtime; all
    /// upcoming standby targets are prepared.</summary>
    public int PreRollCount { get; init; }

    /// <summary>Legacy persisted standby-decoder cap. Ignored by the current cue runtime; decoder
    /// preparation uses as many decoders as the standby window needs.</summary>
    public int MaxPreparedDecoders { get; init; }

    /// <summary>Trigger mode applied to cues created via the toolbar (Phase 5.8.2). Default
    /// <see cref="CueTriggerMode.Manual"/> so older lists load unchanged.</summary>
    public CueTriggerMode DefaultTriggerMode { get; init; } = CueTriggerMode.Manual;

    /// <summary>When true, the cue player re-runs the renumber pass after every insert/reorder
    /// so the operator's numbering stays sequential (Phase 5.8.2). Default off - preserves the
    /// pre-5.8 behavior where operators set numbers themselves.</summary>
    public bool AutoRenumberOnInsert { get; init; }

    /// <summary>Virtual canvases used by the cue player. Multiple video outputs may reference the
    /// same composition (fan-out: composition is rendered once, fed to every referencing output).</summary>
    public List<CueComposition> Compositions { get; init; } = new();

    /// <summary>Video output bindings - each pairs an output line id (from the shared
    /// <c>OutputManagementView</c> registry) with the composition that feeds it. Audio outputs
    /// are referenced directly by id from <see cref="CueAudioRoute"/> entries, so no per-cue-list
    /// audio binding is needed.</summary>
    public List<CueVideoOutputBinding> VideoOutputs { get; init; } = new();

    public List<CueNode> Nodes { get; init; } = new();
}

public sealed record CueComposition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int FrameRateNum { get; init; } = 60;

    public int FrameRateDen { get; init; } = 1;

    /// <summary>Optional composition-level video FX mapping applied to the full canvas before it
    /// fans out to output mappings. Null = no extra composition stage.</summary>
    public CueOutputMapping? VideoFx { get; init; }

    /// <summary>Whether <see cref="VideoFx"/> is active. Geometry is retained while disabled.</summary>
    public bool VideoFxEnabled { get; init; }

    /// <summary>Runs a projectM audio visualizer on this composition as a persistent full-canvas layer.
    /// Because a cue composition persists across every cue fire, the visualizer runs CONTINUOUSLY while the
    /// cue list plays - each fired clip's audio feeds it via a session tap. Absent in older projects
    /// (deserializes false).</summary>
    public bool VisualizerEnabled { get; init; }

    /// <summary>Optional *.milk preset folder for this composition's visualizer (null = built-in idle preset).</summary>
    public string? VisualizerPresetDirectory { get; init; }
}

public sealed record CueVideoOutputBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Id of an output line in the shared <c>OutputManagementView</c> registry
    /// (matches <c>OutputDefinition.Id</c>). Empty when not yet picked.</summary>
    public Guid OutputLineId { get; init; }

    /// <summary>Composition (from <see cref="CueList.Compositions"/>) that feeds this output.</summary>
    public Guid CompositionId { get; init; }

    /// <summary>Optional output mapping (warp sections) for this output - the composited canvas is
    /// cut into sections placed individually in output space (projection onto uneven/multi-panel
    /// surfaces). Null = no mapping stage (identical pipeline and cost to before the feature).
    /// See Doc/HaPlay-Output-Mapping-Plan.md.</summary>
    public CueOutputMapping? Mapping { get; init; }

    /// <summary>Whether <see cref="Mapping"/> is active. The geometry is retained when this is false so
    /// toggling mapping off then on restores the exact configured slice instead of losing it to a null
    /// mapping. Mapping applies only when this is <c>true</c> <em>and</em> <see cref="Mapping"/> is
    /// non-null. Defaults <c>true</c> so pre-flag saves (which stored a mapping only when they wanted it
    /// active) load unchanged.</summary>
    public bool MappingEnabled { get; init; } = true;
}

/// <summary>Output mapping for one composition→output binding (Doc/HaPlay-Output-Mapping-Plan.md §3).</summary>
public sealed record CueOutputMapping
{
    /// <summary>Sections drawn back-to-front onto the output. Empty = nothing drawn (all black);
    /// use a single full-canvas section for identity.</summary>
    public List<CueOutputMappingSection> Sections { get; init; } = new();

    /// <summary>Output canvas size; null = composition size.</summary>
    public int? OutputWidth { get; init; }

    public int? OutputHeight { get; init; }

    /// <summary>A fresh identity mapping: one full-canvas section.</summary>
    public static CueOutputMapping Identity() => new()
    {
        Sections = { CueOutputMappingSection.FullCanvas() },
    };
}

/// <summary>One mapping section: a normalized source slice of the composition canvas plus an
/// affine destination placement (output pixels, rotation around the destination center).</summary>
public sealed record CueOutputMappingSection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>Source slice, normalized [0,1] canvas coordinates.</summary>
    public double SrcX { get; init; }

    public double SrcY { get; init; }

    public double SrcWidth { get; init; } = 1.0;

    public double SrcHeight { get; init; } = 1.0;

    /// <summary>Destination placement in output pixels. Width/height ≤ 0 = natural slice size.</summary>
    public double DestX { get; init; }

    public double DestY { get; init; }

    public double DestWidth { get; init; }

    public double DestHeight { get; init; }

    /// <summary>Rotation around the destination rect center, degrees clockwise.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>Per-section alpha multiplier [0,1].</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Per-section brightness [0,1] - panel brightness matching.</summary>
    public double Brightness { get; init; } = 1.0;

    /// <summary>Reserved for Phase 3 corner-pin (TL, TR, BR, BL in output pixels); ignored in Phase 1.</summary>
    public List<CuePoint>? Corners { get; init; }

    /// <summary>Mesh warp control grid columns (Phase 4 - projection onto non-flat surfaces);
    /// 0 = no mesh, otherwise ≥ 2 with <see cref="MeshRows"/> and a matching
    /// <see cref="MeshPoints"/> count.</summary>
    public int MeshColumns { get; init; }

    public int MeshRows { get; init; }

    /// <summary>Row-major mesh control points in normalized dest-rect space ((0,0) = the un-warped
    /// rect's TL, (1,1) = BR; values may overshoot [0,1]). Relative storage means moving/scaling/
    /// rotating the section carries its warp along. An identity grid renders as pure affine.</summary>
    public List<CuePoint>? MeshPoints { get; init; }

    /// <summary>The identity control grid for <paramref name="columns"/>×<paramref name="rows"/> -
    /// every point on its un-warped grid position.</summary>
    public static List<CuePoint> IdentityMeshPoints(int columns, int rows)
    {
        var points = new List<CuePoint>(columns * rows);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
                points.Add(new CuePoint(c / (double)(columns - 1), r / (double)(rows - 1)));
        }

        return points;
    }

    public static CueOutputMappingSection FullCanvas() => new() { Name = "Full canvas" };
}

public sealed record CuePoint(double X, double Y);

public enum CueLayerPosition
{
    Cover,
    Letterbox,
    Center,
    FillWidth,
    FillHeight,
    Stretch,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CueGroupNode), typeDiscriminator: "group")]
[JsonDerivedType(typeof(MediaCueNode), typeDiscriminator: "media")]
[JsonDerivedType(typeof(ActionCueNode), typeDiscriminator: "action")]
[JsonDerivedType(typeof(CommentCueNode), typeDiscriminator: "comment")]
public abstract record CueNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Number { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public CueTriggerMode TriggerMode { get; init; } = CueTriggerMode.Manual;

    public int PreWaitMs { get; init; }

    public string? Notes { get; init; }

    /// <summary>Color tag index 0..7 - 0 = none, 1..7 map to a fixed palette in
    /// <c>CueColorTagPalette</c>. Default-safe for pre-5.8 files (loads as 0 / no tag).</summary>
    public int ColorTag { get; init; }
}

public sealed record CueGroupNode : CueNode
{
    public CueGroupFireMode FireMode { get; init; } = CueGroupFireMode.FirstCueOnly;

    public List<CueNode> Children { get; init; } = new();
}

/// <summary>
/// One subtitle track selected for a media cue. Exactly one of <see cref="StreamIndex"/> (an embedded container
/// subtitle stream - see <c>MediaStreamInfo.Index</c>) or <see cref="Path"/> (a sidecar file) identifies the
/// source. The optional overrides apply to text formats that support styling (ASS, or any format FFmpeg decodes
/// to ASS events); they are ignored for bitmap (PGS/DVB) subtitles.
/// </summary>
public sealed record CueSubtitleSelection
{
    /// <summary>Embedded container stream index; <c>null</c> for a sidecar selection.</summary>
    public int? StreamIndex { get; init; }

    /// <summary>Sidecar subtitle file path; <c>null</c> for an embedded selection.</summary>
    public string? Path { get; init; }

    /// <summary>Display label for the picker (language / title / codec).</summary>
    public string? Label { get; init; }

    /// <summary>Override font family (libass fallback family); <c>null</c> keeps the document's styling.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font size scale (1.0 = document default); <c>null</c> keeps the document's sizing.</summary>
    public double? FontScale { get; init; }

    /// <summary>libass numpad alignment 1–9 (e.g. 2 = bottom-center); <c>null</c> keeps the document's alignment.</summary>
    public int? Alignment { get; init; }

    /// <summary>True for an embedded container stream, false for a sidecar file.</summary>
    public bool IsEmbedded => StreamIndex.HasValue;
}

public sealed record MediaCueNode : CueNode
{
    public PlaylistItem? Source { get; init; }

    public int DurationMs { get; init; }

    /// <summary>Cached probe result - whether the source has a decodable video stream. Defaults
    /// false; older saved cues (pre-Phase 5.1) load with this unset and the Video tab still shows
    /// until the operator re-probes by re-browsing the source.</summary>
    public bool HasVideo { get; init; }

    /// <summary>Cached probe result - whether the source has a decodable audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Source channel count probed once on add. 0 when unknown / no audio.</summary>
    public int AudioChannels { get; init; }

    /// <summary>
    /// Explicit audio track for multi-track sources (container stream index, see
    /// <c>MediaStreamInfo.Index</c>). <c>null</c> = automatic election. The demuxer falls back to
    /// automatic when the index is stale, so an old choice can never make a cue unplayable.
    /// </summary>
    public int? AudioTrackIndex { get; init; }

    /// <summary>Content signature of the chosen audio track at pick time (codec/language/channels).
    /// Guards <see cref="AudioTrackIndex"/> against re-muxed files whose stream indices shifted -
    /// on mismatch the engine re-resolves by signature or falls back to automatic.</summary>
    public string? AudioTrackSignature { get; init; }

    /// <summary>True when the source's only video is an attached picture (e.g. MP3 with cover art).
    /// The Video tab still shows so the cover art can be placed into a composition, but with a
    /// hint that it's a still image.</summary>
    public bool VideoIsAttachedPicture { get; init; }

    /// <summary>Subtitle tracks to render over this cue's video - none / one / many. Each is an embedded
    /// container stream (<see cref="CueSubtitleSelection.StreamIndex"/>) or a sidecar file
    /// (<see cref="CueSubtitleSelection.Path"/>), with optional font/placement overrides. Empty = no subtitles.</summary>
    public IReadOnlyList<CueSubtitleSelection> Subtitles { get; init; } = [];

    /// <summary>Probed source frame rate (numerator / denominator). 0/0 when unknown or no video.</summary>
    public int SourceFrameRateNum { get; init; }

    public int SourceFrameRateDen { get; init; }

    /// <summary>Probed source video pixel dimensions. 0 when unknown / no video. Used to size a new
    /// composition placement to the source (actual size, scaled down to fit the canvas).</summary>
    public int SourceVideoWidth { get; init; }

    public int SourceVideoHeight { get; init; }

    public bool Loop { get; init; }

    public int StartOffsetMs { get; init; }

    /// <summary>Amount trimmed from the end of the source. 0 means play through the probed duration.</summary>
    public int EndOffsetMs { get; init; }

    public CueEndBehavior EndBehavior { get; init; } = CueEndBehavior.Stop;

    public int FadeInMs { get; init; }

    public int FadeOutMs { get; init; }

    /// <summary>Legacy persisted per-cue pre-roll opt-out. Ignored by the current cue runtime.</summary>
    public bool DisablePreRoll { get; init; }

    /// <summary>Per-source-channel audio routing - picks a cue audio output + a device channel
    /// directly. Replaces the previous virtual-output + route-override model.</summary>
    public List<CueAudioRoute> AudioRoutes { get; init; } = new();

    /// <summary>Per-composition appearance - layer index, position preset, opacity.</summary>
    public List<CueVideoPlacement> VideoPlacements { get; init; } = new();
}

public sealed record ActionCueNode : CueNode
{
    public CueActionKind ActionKind { get; init; } = CueActionKind.OSCOut;

    public Guid? EndpointId { get; init; }

    public string AddressOrMessage { get; init; } = string.Empty;

    public List<string> Arguments { get; init; } = new();
}

public sealed record CommentCueNode : CueNode
{
    public string Text { get; init; } = string.Empty;
}

public sealed record CueAudioRoute
{
    public int SourceChannel { get; init; }

    /// <summary>Id of an audio-capable output line in the shared <c>OutputManagementView</c> registry
    /// (matches <c>OutputDefinition.Id</c>). Empty when no output picked yet.</summary>
    public Guid OutputLineId { get; init; }

    public int OutputChannel { get; init; }

    public double GainDb { get; init; }

    public bool Muted { get; init; }
}

public sealed record CueVideoPlacement
{
    public Guid CompositionId { get; init; }

    public int LayerIndex { get; init; }

    /// <summary>Fit of the (cropped) source within its destination rectangle.</summary>
    public CueLayerPosition Position { get; init; } = CueLayerPosition.Cover;

    public double Opacity { get; init; } = 1.0;

    /// <summary>Destination rectangle on the composition canvas, normalized to [0,1].
    /// Defaults to the full canvas - older cues load unchanged.</summary>
    public double DestX { get; init; }

    public double DestY { get; init; }

    public double DestWidth { get; init; } = 1.0;

    public double DestHeight { get; init; } = 1.0;

    /// <summary>Per-edge source crop insets as fractions [0,1). Default 0 = no trim.</summary>
    public double CropLeft { get; init; }

    public double CropTop { get; init; }

    public double CropRight { get; init; }

    public double CropBottom { get; init; }

    /// <summary>Clockwise rotation (degrees) of this layer about its destination-rect centre. Default 0
    /// = upright (older cues load unchanged). The rotated image overflows its dest rect, as expected.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>Optional per-placement video FX mapping. Sections sample this source video and are
    /// then placed inside the normal destination rectangle.</summary>
    public CueOutputMapping? VideoFx { get; init; }

    /// <summary>Whether <see cref="VideoFx"/> is active. Geometry is retained while disabled.</summary>
    public bool VideoFxEnabled { get; init; }
}

public enum CueTriggerMode
{
    Manual,
    AutoFollow,
    AutoContinue,
}

public enum CueGroupFireMode
{
    FirstCueOnly,
    FireAllSimultaneously,
    ArmedList,
}

public enum CueEndBehavior
{
    Stop,
    FreezeLastFrame,
    Loop,
    FadeOutAndStop,
}

public enum CueActionKind
{
    OSCOut,
    MIDIOut,
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CueList))]
[JsonSerializable(typeof(CueNode))]
[JsonSerializable(typeof(CueGroupNode))]
[JsonSerializable(typeof(MediaCueNode))]
[JsonSerializable(typeof(CueSubtitleSelection))]
[JsonSerializable(typeof(ActionCueNode))]
[JsonSerializable(typeof(CommentCueNode))]
[JsonSerializable(typeof(CueComposition))]
[JsonSerializable(typeof(CueVideoOutputBinding))]
[JsonSerializable(typeof(CueAudioRoute))]
[JsonSerializable(typeof(CueVideoPlacement))]
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(FilePlaylistItem))]
[JsonSerializable(typeof(NDIInputPlaylistItem))]
[JsonSerializable(typeof(PortAudioInputPlaylistItem))]
[JsonSerializable(typeof(ImagePlaylistItem))]
[JsonSerializable(typeof(SubtitlePlaylistItem))]
[JsonSerializable(typeof(TextPlaylistItem))]
[JsonSerializable(typeof(YouTubePlaylistItem))]
[JsonSerializable(typeof(MMDPlaylistItem))]
[JsonSerializable(typeof(CueListsCollectionDocument))]
[JsonSerializable(typeof(CueCompositionsDocument))]
[JsonSerializable(typeof(List<CueList>))]
internal partial class CueListJsonContext : JsonSerializerContext;
