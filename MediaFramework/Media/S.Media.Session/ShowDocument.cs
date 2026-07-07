using System.Text.Json;
using System.Text.Json.Serialization;
using S.Media.Core.Audio;

namespace S.Media.Session;

/// <summary>What a clip does when it reaches its (trimmed) end. Mirrors the GUI cue end behaviour
/// (<c>CueEndBehavior</c>) and is honoured by the playback runtime (<see cref="ShowSession"/>'s clip
/// playback path resolves Loop / FreezeLastFrame / FadeOutAndStop against the clip's end window).</summary>
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

/// <summary>One composition placement of a clip's video: which composition canvas (<paramref name="CompositionId"/>),
/// which layer (<paramref name="LayerIndex"/>), and where/how the frame sits on it (<paramref name="Placement"/>).
/// A cue may place the SAME decoded source onto several compositions/layers at once — picture-in-picture, the
/// same feed in two regions, or mirrored to a second canvas — so <see cref="ShowClipBinding.GetPlacements"/>
/// returns every placement and <c>PlayClipAsync</c> fans the one clip's video out to each (decoded once).</summary>
public sealed record ShowClipPlacement(
    string CompositionId,
    int LayerIndex = 0,
    ShowVideoPlacement? Placement = null);

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
    /// <summary>Optional full source→output gain matrix. When present it supersedes
    /// <see cref="ChannelMatrix"/> and preserves multiple source contributions to one output channel plus
    /// per-cell gain. Values are linear gains before the route-wide <see cref="Gain"/> envelope.</summary>
    public IReadOnlyList<ShowAudioMatrixCell>? MatrixCells { get; init; }

    /// <summary>Declared output channel count for <see cref="MatrixCells"/>. This keeps muted/unrouted trailing
    /// channels in the device format; null derives the count from the highest cell.</summary>
    public int? MatrixOutputChannels { get; init; }

    [JsonIgnore]
    public bool HasGainMatrix => MatrixCells is { Count: > 0 };

    public ChannelMap? ToChannelMap() => ChannelMatrix is { Length: > 0 } m ? new ChannelMap(m) : null;

    internal float[,] ToGainMatrix(float routeScale)
    {
        if (MatrixCells is not { Count: > 0 } cells)
            return new float[0, 0];
        var sourceChannels = cells.Max(c => c.InputChannel) + 1;
        var outputChannels = MatrixOutputChannels is > 0
            ? MatrixOutputChannels.Value
            : cells.Max(c => c.OutputChannel) + 1;
        var gains = new float[sourceChannels, outputChannels];
        foreach (var cell in cells)
        {
            if (cell.InputChannel < 0 || cell.OutputChannel < 0 || cell.OutputChannel >= outputChannels)
                throw new ArgumentException("audio matrix cell indices are outside the declared matrix.");
            gains[cell.InputChannel, cell.OutputChannel] += cell.Gain * routeScale;
        }
        return gains;
    }
}

public sealed record ShowAudioMatrixCell(int InputChannel, int OutputChannel, float Gain);

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

    // --- Clip playback parameters -----------------------------------------------------------------
    // A GUI media cue maps losslessly onto a ShowDocument. The playback runtime honours these:
    // ShowSession opens the clip with StartOffset/EndOffset as its trim window, drives FadeIn/FadeOut on
    // the route, and resolves Loop/EndBehavior against the end window. All are validated at load
    // (ShowDocumentValidator: non-negative offsets/fades, DOC-01). Values are immutable per document.

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

    /// <summary>Run the end-of-clip monitor purely off the reported <see cref="StartOffset"/>→duration window even
    /// for a plain <see cref="ClipEndBehavior.Stop"/> with no trim/fade/loop. Set for a <em>held</em> source (a
    /// rendered text or still cue) that never signals EOF on its own: the clip is stopped at its duration by the
    /// time-based monitor instead of by source exhaustion, so a resize/live-edit re-read can't end it early.</summary>
    public bool EndAtDuration { get; init; }

    /// <summary>Monitor a plain <see cref="ClipEndBehavior.Stop"/> file clip with no trim/fade/loop for its
    /// natural end: release the clip and raise <c>ShowSession.ClipNaturallyEnded</c> when it plays through —
    /// at the duration out-point, or when its (finite, audio-clocked) playback stalls at source EOF short of
    /// the metadata duration. Opt-in per clip: set it for real file cues that drive cue auto-follow; leave it
    /// off for held/live sources (their clock legitimately idles while the clip is up) and for hosts that
    /// poll and advance themselves (the media-player deck).</summary>
    public bool NotifyNaturalEnd { get; init; }

    /// <summary>Where/how this clip's video sits on its <see cref="CompositionId"/> canvas (GUI
    /// <c>CueVideoPlacement</c>). Null ⇒ full-canvas, opaque, Cover (the prior hardcoded placement).</summary>
    public ShowVideoPlacement? Placement { get; init; }

    /// <summary>Composition placements <em>beyond</em> the primary (<see cref="CompositionId"/>/<see cref="LayerIndex"/>/
    /// <see cref="Placement"/>). When set, the clip's one decoded video is fanned to every placement here as well as
    /// the primary — each its own composition layer. Empty/null ⇒ the clip appears only on the primary composition.
    /// Use <see cref="GetPlacements"/> for the layer-ordered effective set.</summary>
    public IReadOnlyList<ShowClipPlacement>? ExtraPlacements { get; init; }

    /// <summary>The layer-ordered effective set of composition placements for this clip: the primary
    /// (<see cref="CompositionId"/>) plus any <see cref="ExtraPlacements"/>, sorted by layer index. Empty when the
    /// clip targets no composition (audio-only). The one decoded source is fanned to every entry at commit time.</summary>
    public IReadOnlyList<ShowClipPlacement> GetPlacements()
    {
        var primary = CompositionId is { } id ? new ShowClipPlacement(id, LayerIndex, Placement) : null;
        if (ExtraPlacements is not { Count: > 0 })
            return primary is null ? [] : [primary];
        var all = new List<ShowClipPlacement>(ExtraPlacements.Count + 1);
        if (primary is not null)
            all.Add(primary);
        all.AddRange(ExtraPlacements);
        all.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return all;
    }

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
    IReadOnlyList<OutputPatchRoute> Routes)
{
    /// <summary>Per-group audio output endpoints (D11). Empty ⇒ each group plays on one implicit master
    /// output (<see cref="ShowSession.MasterOutputId"/>) on the backend default device; declare entries to
    /// drive several outputs/devices per group (each with its own N→M route).</summary>
    public IReadOnlyList<ShowAudioOutput> AudioOutputs { get; init; } = [];

    /// <summary>An empty version-1 show.</summary>
    public static ShowDocument Empty { get; } = new(1, [], [], [], []);

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
