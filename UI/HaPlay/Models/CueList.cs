using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Phase D (§5.2) cue-list root persisted in <c>.haplaycues</c> files and optionally embedded in
/// projects. A cue list is a tree of groups and cues.
/// </summary>
public sealed record CueList
{
    public string Schema { get; init; } = "HaPlayCueList/v1";

    public string Name { get; init; } = "Cue List";

    /// <summary>How many upcoming file media cues to pre-open (§5.7). Default 4.</summary>
    public int PreRollCount { get; init; } = 4;

    /// <summary>List-level virtual output registry (VOut 1..N) for deterministic cue routing.</summary>
    public List<CueVirtualOutputChannel> VirtualOutputs { get; init; } = new();

    public List<CueNode> Nodes { get; init; } = new();
}

public sealed record CueVirtualOutputChannel
{
    public int Channel { get; init; }

    public string Label { get; init; } = string.Empty;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CueGroupNode), typeDiscriminator: "group")]
[JsonDerivedType(typeof(MediaCueNode), typeDiscriminator: "media")]
[JsonDerivedType(typeof(ActionCueNode), typeDiscriminator: "action")]
[JsonDerivedType(typeof(CommentCueNode), typeDiscriminator: "comment")]
public abstract record CueNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Operator-visible cue number label (e.g. "1", "3.5").</summary>
    public string Number { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public CueTriggerMode TriggerMode { get; init; } = CueTriggerMode.Manual;

    /// <summary>Delay from trigger to fire (milliseconds).</summary>
    public int PreWaitMs { get; init; }

    public string? Notes { get; init; }
}

public sealed record CueGroupNode : CueNode
{
    public CueGroupFireMode FireMode { get; init; } = CueGroupFireMode.FirstCueOnly;

    public List<CueNode> Children { get; init; } = new();

    /// <summary>Default virtual outputs inherited by child cues unless overridden.</summary>
    public List<int> DefaultVirtualOutputChannels { get; init; } = new();
}

public sealed record MediaCueNode : CueNode
{
    /// <summary>Media source entry (file or live input) aligned with player playlist item semantics.</summary>
    public PlaylistItem? Source { get; init; }

    public bool Loop { get; init; }

    public int StartOffsetMs { get; init; }

    public CueEndBehavior EndBehavior { get; init; } = CueEndBehavior.Stop;

    public int FadeInMs { get; init; }

    public int FadeOutMs { get; init; }

    /// <summary>Virtual outputs selected for this cue (VOut 1..N).</summary>
    public List<int> VirtualOutputChannels { get; init; } = new();

    /// <summary>Per-connection route overrides (input channel -> virtual output channel).</summary>
    public List<CueRouteConnectionOverride> RouteConnections { get; init; } = new();
}

public sealed record ActionCueNode : CueNode
{
    public CueActionKind ActionKind { get; init; } = CueActionKind.OscOut;

    /// <summary>Project endpoint registry id (OSC or MIDI target).</summary>
    public Guid? EndpointId { get; init; }

    public string AddressOrMessage { get; init; } = string.Empty;

    public List<string> Arguments { get; init; } = new();
}

public sealed record CommentCueNode : CueNode
{
    public string Text { get; init; } = string.Empty;
}

public sealed record CueRouteConnectionOverride
{
    public int InputChannel { get; init; }

    public int VirtualOutputChannel { get; init; }

    public double GainDb { get; init; }

    public bool Muted { get; init; }
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
[JsonSerializable(typeof(CueRouteConnectionOverride))]
[JsonSerializable(typeof(CueVirtualOutputChannel))]
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(FilePlaylistItem))]
[JsonSerializable(typeof(NDIInputPlaylistItem))]
[JsonSerializable(typeof(PortAudioInputPlaylistItem))]
internal partial class CueListJsonContext : JsonSerializerContext;
