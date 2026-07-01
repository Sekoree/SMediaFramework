using System.Text.Json;
using System.Text.Json.Serialization;
using S.Media.Core.Audio;

namespace S.Media.Session;

/// <summary>What a clip does when it reaches its (trimmed) end. Mirrors the GUI cue end behaviour
/// (<c>CueEndBehavior</c>); honoured by the playback runtime in the 8b convergence slice (carried by
/// the document until then).</summary>
public enum ClipEndBehavior
{
    Stop,
    FreezeLastFrame,
    Loop,
    FadeOutAndStop,
}

/// <summary>A clip's appearance on its composition canvas — where its video sits and how it fits. Defaults
/// are full-canvas, opaque, Cover fit, upright (a clip with no placement composites exactly as before).
/// <paramref name="Fit"/> is the fit mode within the dest rect (Cover/Contain/Letterbox/Center/Stretch/
/// FillWidth/FillHeight; null = Cover). Maps to the compositor's <c>VideoPlacementSpec</c>.</summary>
public sealed record ShowVideoPlacement(
    double DestX = 0,
    double DestY = 0,
    double DestWidth = 1,
    double DestHeight = 1,
    double Opacity = 1,
    string? Fit = null,
    double RotationDegrees = 0,
    double CropLeft = 0,
    double CropTop = 0,
    double CropRight = 0,
    double CropBottom = 0,
    ClipOutputMappingSpec? VideoFx = null);

/// <summary>One audio output a clip plays on (GUI per-cue audio routing — a group of <c>CueAudioRoute</c>s to
/// the same output line). Unlike a per-group <see cref="ShowAudioOutput"/>, this is carried on the clip so a
/// cue plays on exactly its routed outputs. <see cref="ChannelMatrix"/> is the N→M <see cref="ChannelMap"/>
/// array (length = output channels, each entry = the source channel feeding it, -1 = silence); null = stereo.</summary>
public sealed record ShowClipAudioRoute(
    string? DeviceId = null,
    int[]? ChannelMatrix = null,
    float Gain = 1f,
    int? SampleRate = null)
{
    public ChannelMap? ToChannelMap() => ChannelMatrix is { Length: > 0 } m ? new ChannelMap(m) : null;
}

/// <summary>
/// Binds a cue to the media it plays: when the cue fires, <see cref="MediaPath"/> is opened through the
/// session's <c>IMediaRegistry</c> (a bare path or a <c>scheme:</c> URI — D2) and played on the cue's group.
/// </summary>
/// <param name="AudioStreamIndex">Audio track selection (03 §6 multi-track): <c>null</c> = automatic,
/// <c>-1</c> (<see cref="S.Media.Players.MediaPlayerOpenOptions.DisabledStreamIndex"/>) = no audio, otherwise
/// the chosen stream index. Lets a multi-track clip (e.g. language stems) pick which track this cue plays.</param>
/// <param name="SubtitlePath">Backward-compatible single sidecar subtitle path. Prefer <paramref name="Subtitles"/>
/// for explicit none/one/many selection and embedded stream selection.</param>
/// <param name="Subtitles">Selected subtitle tracks. An empty/null list means none unless <paramref name="SubtitlePath"/>
/// is set. A null selection path uses <see cref="MediaPath"/> as the container; <see cref="ShowSubtitleSelection.StreamIndex"/>
/// selects an embedded stream.</param>
public sealed record ShowClipBinding(
    string CueId,
    string MediaPath,
    string? CompositionId = null,
    int LayerIndex = 0,
    int? AudioStreamIndex = null,
    string? SubtitlePath = null,
    IReadOnlyList<ShowSubtitleSelection>? Subtitles = null)
{
    public IReadOnlyList<ShowSubtitleSelection> GetSubtitleSelections()
    {
        if (Subtitles is { Count: > 0 })
            return Subtitles;
        return string.IsNullOrWhiteSpace(SubtitlePath)
            ? []
            : [new ShowSubtitleSelection(SubtitlePath)];
    }

    // --- Clip playback parameters (Phase 8a convergence) ----------------------------------------
    // Carried so a GUI media cue maps losslessly onto a ShowDocument. PlayClipAsync honours these at
    // runtime in the 8b playback slice; until then a clip opens head-to-tail (no trim/loop/fade).

    /// <summary>Trim from the source start (GUI <c>MediaCueNode.StartOffsetMs</c>). Zero = from the head.</summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>Trim from the source end (GUI <c>MediaCueNode.EndOffsetMs</c>). Zero = through the probed duration.</summary>
    public TimeSpan EndOffset { get; init; }

    /// <summary>Fade-in at clip start (GUI <c>FadeInMs</c>).</summary>
    public TimeSpan FadeIn { get; init; }

    /// <summary>Fade-out at clip end (GUI <c>FadeOutMs</c>).</summary>
    public TimeSpan FadeOut { get; init; }

    /// <summary>Loop the trimmed clip (GUI <c>MediaCueNode.Loop</c>, also implied by <see cref="ClipEndBehavior.Loop"/>).</summary>
    public bool Loop { get; init; }

    /// <summary>What happens when the clip reaches its (trimmed) end (GUI <c>MediaCueNode.EndBehavior</c>).</summary>
    public ClipEndBehavior EndBehavior { get; init; } = ClipEndBehavior.Stop;

    /// <summary>Where/how this clip's video sits on its <see cref="CompositionId"/> canvas (GUI
    /// <c>CueVideoPlacement</c>). Null ⇒ full-canvas, opaque, Cover (the prior hardcoded placement).</summary>
    public ShowVideoPlacement? Placement { get; init; }

    /// <summary>Per-clip audio output routing (GUI per-cue <c>CueAudioRoute</c>s, one entry per output line).
    /// Non-empty plays on exactly these outputs; an empty list is explicitly silent; <see langword="null"/>
    /// inherits the show/group outputs (including the standalone session's implicit master fallback).</summary>
    public IReadOnlyList<ShowClipAudioRoute>? AudioRoutes { get; init; }
}

/// <summary>A selected subtitle source. <paramref name="Path"/> null means the clip's media container;
/// <paramref name="StreamIndex"/> <c>-1</c> selects the best subtitle stream.</summary>
public sealed record ShowSubtitleSelection(string? Path = null, int StreamIndex = -1);

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
/// <see cref="Id"/>, <c>SourceId</c> == the clip binding's cue id). The first output of a group is its master
/// (drives the clock); the rest auto-slave.</summary>
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
