using System.Text.Json;
using System.Text.Json.Serialization;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// Per-instance visualizer settings. Serializes as the opaque config blob of the bus/compositor
/// registries' "projectm" kind (STJ source-generated, AOT-safe).
/// </summary>
public sealed record ProjectMOptions
{
    /// <summary>Directory scanned (recursively) for *.milk presets. Null/empty = projectM's built-in idle preset only.</summary>
    public string? PresetDirectory { get; init; }

    /// <summary>How long one preset plays before advancing (manual rotation - the playlist library stays off).</summary>
    public double PresetDurationSeconds { get; init; } = 30;

    /// <summary>Random order (true) or alphabetical rotation (false).</summary>
    public bool Shuffle { get; init; } = true;

    /// <summary>projectM beat sensitivity (its default is 1.0).</summary>
    public double BeatSensitivity { get; init; } = 1.0;

    /// <summary>Cross-fade seconds between presets.</summary>
    public double TransitionSeconds { get; init; } = 2.0;

    /// <summary>Sample rate the audio tap declares (the router resamples/maps into it).</summary>
    public int AudioSampleRate { get; init; } = 48_000;

    public static ProjectMOptions FromJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return new ProjectMOptions();
        try
        {
            return JsonSerializer.Deserialize(configJson, ProjectMOptionsJsonContext.Default.ProjectMOptions)
                   ?? new ProjectMOptions();
        }
        catch (JsonException)
        {
            return new ProjectMOptions();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, ProjectMOptionsJsonContext.Default.ProjectMOptions);
}

[JsonSerializable(typeof(ProjectMOptions))]
internal sealed partial class ProjectMOptionsJsonContext : JsonSerializerContext;
