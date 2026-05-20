using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Persisted per-player state — playlist + transport flags + selected output lines (matched on load
/// by <see cref="OutputDefinition.DisplayName"/>). Written via <see cref="MediaPlayerConfigJsonContext"/>
/// so the path is NativeAOT-safe.
/// </summary>
public sealed record MediaPlayerConfig
{
    /// <summary>Schema tag — bump when the on-disk shape changes incompatibly.</summary>
    public string Schema { get; init; } = "HaPlayPlayerConfig/v1";

    public string Name { get; init; } = "Player";

    /// <summary>Phase C (§4.3.2) — all playlist tabs owned by this player.</summary>
    public List<PlaylistConfig> PlaylistTabs { get; init; } = new();

    /// <summary>Index into <see cref="PlaylistTabs"/> that was visible when the player was saved.</summary>
    public int SelectedPlaylistTabIndex { get; init; }

    /// <summary>Legacy v1 flat playlist fields. Kept so older player/project files still load cleanly.</summary>
    public List<string> PlaylistPaths { get; init; } = new();

    public string? MediaFilePath { get; init; }

    public string? SelectedPlaylistPath { get; init; }

    public string? FallbackImagePath { get; init; }

    public bool IsLooping { get; init; }

    public bool AutoAdvancePlaylist { get; init; }

    public bool HoldFallbackVideo { get; init; }

    public double MasterVolumeDb { get; init; }

    public bool MasterMuted { get; init; }

    public PlayerOutputPreset OutputPreset { get; init; } = PlayerOutputPreset.AsSource;

    public PlayerTransitionMode TransitionMode { get; init; } = PlayerTransitionMode.Cut;

    public int TransitionDurationMs { get; init; } = 500;

    /// <summary>Display names of output lines that were checked for this player when the config was saved.</summary>
    public List<string> SelectedOutputDisplayNames { get; init; } = new();

    public List<OutputGainConfig> OutputGains { get; init; } = new();
}

public sealed record PlaylistConfig
{
    public string Schema { get; init; } = "HaPlayPlaylist/v1";

    public string Name { get; init; } = "Set A";

    public List<string> Paths { get; init; } = new();

    public string? SelectedPath { get; init; }

    public bool IsLooping { get; init; }

    public bool AutoAdvance { get; init; }
}

public sealed record OutputGainConfig
{
    public string OutputDisplayName { get; init; } = string.Empty;

    public double GainDb { get; init; }

    public bool Muted { get; init; }

    /// <summary>Phase C (§4.3.4) — per-output channel-mix mode. Falls back to <see cref="AudioRouteMixMode.Stereo"/>
    /// for older configs so a missing field doesn't surprise the loader. When <see cref="MatrixCells"/> is
    /// non-empty, the matrix takes precedence and <see cref="MixMode"/> is the "preset that was last applied".</summary>
    public AudioRouteMixMode MixMode { get; init; } = AudioRouteMixMode.Stereo;

    /// <summary>Phase C (§4.3.4) — full N×M matrix cells. Empty means "fall back to <see cref="MixMode"/>".</summary>
    public List<AudioMatrixCellConfig> MatrixCells { get; init; } = new();
}

/// <summary>
/// Phase C (§4.3.4) first-cut channel routing mode. The full N×M cell-grid matrix needs the framework
/// "Per-cell channel-mix matrix" gap to land; until then these five fixed presets cover the common
/// stereo source → stereo sink reroutes via a single <c>ChannelMap</c> per route.
/// </summary>
public enum AudioRouteMixMode
{
    /// <summary>Identity stereo: source L → out L, source R → out R.</summary>
    Stereo,
    /// <summary>Swap: source L → out R, source R → out L.</summary>
    Swap,
    /// <summary>Mono-from-left: source L → out L + out R; source R discarded.</summary>
    MonoLeft,
    /// <summary>Mono-from-right: source R → out L + out R; source L discarded.</summary>
    MonoRight,
    /// <summary>Silence: both output channels zeroed (logical mute that survives gain changes).</summary>
    Silence,
}

public enum PlayerOutputPreset
{
    AsSource,
    Preset1080p60,
    Preset720p60,
    Custom,
}

public enum PlayerTransitionMode
{
    Cut,
    Fade,
    IdleImage,
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaPlayerConfig))]
[JsonSerializable(typeof(PlaylistConfig))]
[JsonSerializable(typeof(AudioMatrixCellConfig))]
internal partial class MediaPlayerConfigJsonContext : JsonSerializerContext;
