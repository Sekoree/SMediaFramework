using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Cue-list root persisted in <c>.haplaycues</c> files. A cue list is a tree of groups and cues
/// plus its own list of compositions and outputs — the Cue Player is a self-contained playback
/// surface and does not borrow routing from MediaPlayer tabs.
/// </summary>
public sealed record CueList
{
    public string Schema { get; init; } = "HaPlayCueList/v3";

    public string Name { get; init; } = "Cue List";

    public int PreRollCount { get; init; } = 4;

    /// <summary>Trigger mode applied to cues created via the toolbar (Phase 5.8.2). Default
    /// <see cref="CueTriggerMode.Manual"/> so older lists load unchanged.</summary>
    public CueTriggerMode DefaultTriggerMode { get; init; } = CueTriggerMode.Manual;

    /// <summary>When true, the cue player re-runs the renumber pass after every insert/reorder
    /// so the operator's numbering stays sequential (Phase 5.8.2). Default off — preserves the
    /// pre-5.8 behavior where operators set numbers themselves.</summary>
    public bool AutoRenumberOnInsert { get; init; }

    /// <summary>Virtual canvases used by the cue player. Multiple video outputs may reference the
    /// same composition (fan-out: composition is rendered once, fed to every referencing output).</summary>
    public List<CueComposition> Compositions { get; init; } = new();

    /// <summary>Video output bindings — each pairs an output line id (from the shared
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
}

public sealed record CueVideoOutputBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Id of an output line in the shared <c>OutputManagementView</c> registry
    /// (matches <c>OutputDefinition.Id</c>). Empty when not yet picked.</summary>
    public Guid OutputLineId { get; init; }

    /// <summary>Composition (from <see cref="CueList.Compositions"/>) that feeds this output.</summary>
    public Guid CompositionId { get; init; }
}

public enum CueLayerPosition
{
    Cover,
    Letterbox,
    Center,
    FillWidth,
    FillHeight,
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

    /// <summary>Color tag index 0..7 — 0 = none, 1..7 map to a fixed palette in
    /// <c>CueColorTagPalette</c>. Default-safe for pre-5.8 files (loads as 0 / no tag).</summary>
    public int ColorTag { get; init; }
}

public sealed record CueGroupNode : CueNode
{
    public CueGroupFireMode FireMode { get; init; } = CueGroupFireMode.FirstCueOnly;

    public List<CueNode> Children { get; init; } = new();
}

public sealed record MediaCueNode : CueNode
{
    public PlaylistItem? Source { get; init; }

    public int DurationMs { get; init; }

    /// <summary>Cached probe result — whether the source has a decodable video stream. Defaults
    /// false; older saved cues (pre-Phase 5.1) load with this unset and the Video tab still shows
    /// until the operator re-probes by re-browsing the source.</summary>
    public bool HasVideo { get; init; }

    /// <summary>Cached probe result — whether the source has a decodable audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Source channel count probed once on add. 0 when unknown / no audio.</summary>
    public int AudioChannels { get; init; }

    /// <summary>True when the source's only video is an attached picture (e.g. MP3 with cover art).
    /// The Video tab still shows so the cover art can be placed into a composition, but with a
    /// hint that it's a still image.</summary>
    public bool VideoIsAttachedPicture { get; init; }

    /// <summary>Probed source frame rate (numerator / denominator). 0/0 when unknown or no video.</summary>
    public int SourceFrameRateNum { get; init; }

    public int SourceFrameRateDen { get; init; }

    public bool Loop { get; init; }

    public int StartOffsetMs { get; init; }

    public CueEndBehavior EndBehavior { get; init; } = CueEndBehavior.Stop;

    public int FadeInMs { get; init; }

    public int FadeOutMs { get; init; }

    /// <summary>Per-source-channel audio routing — picks a cue audio output + a device channel
    /// directly. Replaces the previous virtual-output + route-override model.</summary>
    public List<CueAudioRoute> AudioRoutes { get; init; } = new();

    /// <summary>Per-composition appearance — layer index, position preset, opacity.</summary>
    public List<CueVideoPlacement> VideoPlacements { get; init; } = new();
}

public sealed record ActionCueNode : CueNode
{
    public CueActionKind ActionKind { get; init; } = CueActionKind.OscOut;

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

    public CueLayerPosition Position { get; init; } = CueLayerPosition.Cover;

    public double Opacity { get; init; } = 1.0;
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
    OscOut,
    MidiOut,
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CueList))]
[JsonSerializable(typeof(CueNode))]
[JsonSerializable(typeof(CueGroupNode))]
[JsonSerializable(typeof(MediaCueNode))]
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
internal partial class CueListJsonContext : JsonSerializerContext;
