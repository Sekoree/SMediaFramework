namespace HaPlay.Models;

/// <summary>Standalone soundboards file payload (<c>.haplayboards</c>): one or more boards, so the
/// same format serves "save this board" and "save the whole collection".</summary>
public sealed record SoundboardsCollectionDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Generator { get; init; }

    public List<SoundboardConfig> Soundboards { get; init; } = [];
}

/// <summary>
/// One tile on a <see cref="SoundboardConfig"/> grid. A tile is "bound" once it has a
/// <see cref="FilePath"/>; unbound tiles are placeholders that only show up in edit mode.
/// </summary>
public sealed record SoundboardTileConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Grid cell (zero-based). Tiles keep their cell when others are hidden, so a touch
    /// operator's muscle memory survives outside edit mode.</summary>
    public int Row { get; init; }

    public int Column { get; init; }

    /// <summary>Absolute path of the sound file; null = unbound placeholder.</summary>
    public string? FilePath { get; init; }

    /// <summary>Optional display alias; null/blank = show the filename (without extension).</summary>
    public string? Label { get; init; }

    /// <summary>Target audio output line (<see cref="OutputDefinition.Id"/>);
    /// <see cref="Guid.Empty"/> = use the board default at play time.</summary>
    public Guid OutputLineId { get; init; }

    /// <summary>Linear volume 0..1 (1 = unity gain).</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>Fade-out duration applied when the tile is tapped while playing; 0 = stop instantly.</summary>
    public int FadeOutMs { get; init; }

    public bool Loop { get; init; }

    /// <summary>Cached duration from the add-time probe so the grid can label tiles without
    /// reopening decoders on project load. Refreshed whenever the file binding changes.</summary>
    public int? DurationMs { get; init; }
}

/// <summary>
/// One soundboard tab: a fixed grid of tiles plus the per-board defaults that pre-fill newly
/// bound tiles (fast "drop a folder of stingers on the grid" workflow).
/// </summary>
public sealed record SoundboardConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Soundboard";

    public int Rows { get; init; } = 4;

    public int Columns { get; init; } = 6;

    /// <summary>Defaults applied to a tile when a sound is bound to it (and used at play time for
    /// tiles whose own <see cref="SoundboardTileConfig.OutputLineId"/> is empty).</summary>
    public Guid DefaultOutputLineId { get; init; }

    public double DefaultVolume { get; init; } = 1.0;

    public int DefaultFadeOutMs { get; init; }

    public bool DefaultLoop { get; init; }

    public List<SoundboardTileConfig> Tiles { get; init; } = new();
}
