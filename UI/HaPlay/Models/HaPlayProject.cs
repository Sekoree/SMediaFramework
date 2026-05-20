using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Top-level project file (§7 of the UI refactor plan). Captures every persistable piece of a HaPlay
/// session in one file so a show is one save/open away. Phase A persists outputs and players; cue lists
/// (§5) will join under <see cref="CueLists"/> in Phase D. UI layout state intentionally lives in a
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

    /// <summary>Per-player config (§4.5 will split this; Phase A keeps the existing <see cref="MediaPlayerConfig"/> shape).</summary>
    public List<MediaPlayerConfig> Players { get; init; } = new();

    /// <summary>Reserved for Phase D — cue lists. Always empty in Phase A files.</summary>
    public List<HaPlayCueListPlaceholder> CueLists { get; init; } = new();

    /// <summary>Constant for callers that want to write SchemaVersion explicitly.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Placeholder for the Phase D cue list type. Defined now so the JSON shape doesn't change when cues
/// land — adding fields to a sealed record is a non-breaking schema change; introducing a new top-level
/// array would require a migration step.
/// </summary>
public sealed record HaPlayCueListPlaceholder
{
    public string Name { get; init; } = string.Empty;
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
[JsonSerializable(typeof(MediaPlayerConfig))]
[JsonSerializable(typeof(PlaylistConfig))]
[JsonSerializable(typeof(OutputGainConfig))]
internal partial class HaPlayProjectJsonContext : JsonSerializerContext;
