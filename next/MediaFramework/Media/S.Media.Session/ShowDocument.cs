using System.Text.Json;
using System.Text.Json.Serialization;

namespace S.Media.Session;

/// <summary>
/// Binds a cue to the media it plays: when the cue fires, <see cref="MediaPath"/> is opened through the
/// session's <c>IMediaRegistry</c> (a bare path or a <c>scheme:</c> URI — D2) and played on the cue's group.
/// </summary>
/// <param name="AudioStreamIndex">Audio track selection (03 §6 multi-track): <c>null</c> = automatic,
/// <c>-1</c> (<see cref="S.Media.Players.MediaPlayerOpenOptions.DisabledStreamIndex"/>) = no audio, otherwise
/// the chosen stream index. Lets a multi-track clip (e.g. language stems) pick which track this cue plays.</param>
public sealed record ShowClipBinding(
    string CueId,
    string MediaPath,
    string? CompositionId = null,
    int LayerIndex = 0,
    int? AudioStreamIndex = null);

/// <summary>A composition canvas a clip's video can be placed onto (maps to a <c>ClipCompositionRuntime</c>).
/// <paramref name="OutputMapping"/> cuts the composited canvas into placed sections for the output (projector
/// keystone / multi-panel tiling) — affine sections composite headless on the CPU backend; mesh warp is GL.</summary>
public sealed record ShowComposition(
    string Id,
    string Name,
    int Width,
    int Height,
    int FrameRateNum = 30,
    int FrameRateDen = 1,
    ClipOutputMappingSpec? OutputMapping = null);

/// <summary>An audio output endpoint a transport group plays on (D11 per-group outputs; declare more than one
/// for a multi-output / multi-device group). <paramref name="DeviceId"/> null = the backend's default device;
/// the per-output N→M channel remap comes from the matching <see cref="OutputPatchRoute"/> (<c>OutputId</c> ==
/// <see cref="Id"/>). The first output of a group is its master (drives the clock); the rest auto-slave.</summary>
public sealed record ShowAudioOutput(
    string Id,
    string? DeviceId = null,
    string GroupId = "main"); // = ShowSession.DefaultGroup

/// <summary>
/// The headless, serializable definition of a show — cues, the media each cue plays, and the output patch.
/// Source-generated (<see cref="ShowDocumentJsonContext"/>) so it loads with no reflection (D10, AOT-safe),
/// and carries no Avalonia/UI state (the UI persists view-state separately on top of this).
/// </summary>
public sealed record ShowDocument(
    int Version,
    IReadOnlyList<CueDefinition> Cues,
    IReadOnlyList<ShowClipBinding> Clips,
    IReadOnlyList<ShowComposition> Compositions,
    IReadOnlyList<OutputPatchRoute> Outputs,
    IReadOnlyList<OutputPatchRoute> Routes,
    IReadOnlyList<string> Devices)
{
    /// <summary>Per-group audio output endpoints (D11). Empty ⇒ each group plays on one implicit master
    /// output (<see cref="ShowSession.MasterOutputId"/>) on the backend default device; declare entries to
    /// drive several outputs/devices per group (each with its own N→M route).</summary>
    public IReadOnlyList<ShowAudioOutput> AudioOutputs { get; init; } = [];

    /// <summary>An empty version-1 show.</summary>
    public static ShowDocument Empty { get; } = new(1, [], [], [], [], [], []);

    /// <summary>Serializes to indented JSON via the source-generated context (no reflection — D10).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ShowDocumentJsonContext.Default.ShowDocument);

    /// <summary>Loads a show from JSON via the source-generated context (headless, AOT-safe — D10).</summary>
    public static ShowDocument FromJson(string json) =>
        JsonSerializer.Deserialize(json, ShowDocumentJsonContext.Default.ShowDocument)
        ?? throw new InvalidOperationException("show document JSON was empty or invalid.");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ShowDocument))]
internal partial class ShowDocumentJsonContext : JsonSerializerContext;
