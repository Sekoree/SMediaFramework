using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Top-level project file (§7 of the UI refactor plan). Captures every persistable piece of a HaPlay
/// session in one file so a show is one save/open away. Cue lists (§5) live under
/// <see cref="CueLists"/>. UI layout state intentionally lives in a
/// per-machine sidecar (not part of this record).
/// </summary>
public sealed record HaPlayProject
{
    /// <summary>Bump on every breaking field change so the loader can migrate (§9.4).</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Best-effort app version stamp — informational only.</summary>
    public string? HaPlayVersion { get; init; }

    /// <summary>All output definitions in display order. Identity is <see cref="OutputDefinition.Id"/>.</summary>
    public List<OutputDefinition> Outputs { get; init; } = new();

    public List<VirtualAudioChannelAssignment> VirtualAudioChannels { get; init; } = new();

    /// <summary>Per-player config (§4.5 will split this; Phase A keeps the existing <see cref="MediaPlayerConfig"/> shape).</summary>
    public List<MediaPlayerConfig> Players { get; init; } = new();

    public List<ActionEndpoint> ActionEndpoints { get; init; } = new();

    /// <summary>§8.2 — project-level shared headphones bus definitions. Players may reference one
    /// of these by id via <see cref="MediaPlayerConfig.HeadphonesCueSharedBusId"/> to share a
    /// monitor send across decks; otherwise they keep using a dedicated PortAudio line.</summary>
    public List<SharedHeadphonesBus> SharedHeadphonesBuses { get; init; } = new();

    public List<CueList> CueLists { get; init; } = new();

    public List<ControlGraphConfig> ControlGraphs { get; init; } = new();

    public ControlSystemConfig ControlSystem { get; init; } = new();

    /// <summary>Constant for callers that want to write SchemaVersion explicitly.</summary>
    public const int CurrentSchemaVersion = 3;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HaPlayProject))]
[JsonSerializable(typeof(OutputDefinition))]
[JsonSerializable(typeof(PortAudioOutputDefinition))]
[JsonSerializable(typeof(LocalVideoOutputDefinition))]
[JsonSerializable(typeof(NDIOutputDefinition))]
[JsonSerializable(typeof(VirtualAudioChannelAssignment))]
[JsonSerializable(typeof(MediaPlayerConfig))]
[JsonSerializable(typeof(PlaylistConfig))]
[JsonSerializable(typeof(OutputGainConfig))]
[JsonSerializable(typeof(InputChannelTrimConfig))]
[JsonSerializable(typeof(ActionEndpoint))]
[JsonSerializable(typeof(OscActionEndpoint))]
[JsonSerializable(typeof(MidiActionEndpoint))]
[JsonSerializable(typeof(SharedHeadphonesBus))]
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
[JsonSerializable(typeof(ControlGraphConfig))]
[JsonSerializable(typeof(ControlNodeConfig))]
[JsonSerializable(typeof(ControlConnectionConfig))]
[JsonSerializable(typeof(ControlNodeSettings))]
[JsonSerializable(typeof(PassthroughControlNodeSettings))]
[JsonSerializable(typeof(MidiInputControlNodeSettings))]
[JsonSerializable(typeof(OscInputControlNodeSettings))]
[JsonSerializable(typeof(MapRangeControlNodeSettings))]
[JsonSerializable(typeof(ScriptTransformControlNodeSettings))]
[JsonSerializable(typeof(OscOutputControlNodeSettings))]
[JsonSerializable(typeof(MidiOutputControlNodeSettings))]
[JsonSerializable(typeof(X32ChannelFaderControlNodeSettings))]
[JsonSerializable(typeof(X32CustomLayerConfig))]
[JsonSerializable(typeof(X32CustomLayerSlotConfig))]
[JsonSerializable(typeof(ControlSystemConfig))]
[JsonSerializable(typeof(ControlOscListenerConfig))]
[JsonSerializable(typeof(ControlMonitorOptions))]
[JsonSerializable(typeof(ControlDeviceInstanceConfig))]
[JsonSerializable(typeof(ControlDeviceBindingConfig))]
[JsonSerializable(typeof(ControlPeriodicOscSendConfig))]
[JsonSerializable(typeof(ControlOscArgumentConfig))]
[JsonSerializable(typeof(ControlLayerConfig))]
[JsonSerializable(typeof(ControlScriptConfig))]
[JsonSerializable(typeof(ControlScriptFailurePolicy))]
[JsonSerializable(typeof(ControlScriptTriggerConfig))]
internal partial class HaPlayProjectJsonContext : JsonSerializerContext;
