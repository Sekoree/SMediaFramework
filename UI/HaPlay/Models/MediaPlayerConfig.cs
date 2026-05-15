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

    public List<string> PlaylistPaths { get; init; } = new();

    public string? MediaFilePath { get; init; }

    public string? SelectedPlaylistPath { get; init; }

    public string? FallbackImagePath { get; init; }

    public bool IsLooping { get; init; }

    public bool HoldFallbackVideo { get; init; }

    /// <summary>Display names of output lines that were checked for this player when the config was saved.</summary>
    public List<string> SelectedOutputDisplayNames { get; init; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaPlayerConfig))]
internal partial class MediaPlayerConfigJsonContext : JsonSerializerContext;
