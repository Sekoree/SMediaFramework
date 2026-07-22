namespace HaViz.Core;

/// <summary>
/// User-facing configuration for one visualizer→NDI run. <see cref="Normalized"/> clamps to what
/// the renderer and NDI can actually do so the UI can bind freely without validating.
/// </summary>
public sealed record VizNdiSettings
{
    public string NdiName { get; init; } = "HaViz";

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public int Fps { get; init; } = 30;

    /// <summary>Directory containing .milk presets. Null/empty = projectM's built-in idle preset.</summary>
    public string? PresetDirectory { get; init; }

    public double PresetDurationSeconds { get; init; } = 15;

    public bool ShufflePresets { get; init; } = true;

    public double BeatSensitivity { get; init; } = 0.5;

    public double TransitionSeconds { get; init; } = 5;

    /// <summary>Clamped copy safe to hand to the engine. Resolution is bounded for mobile GPUs and
    /// forced even (NDI UYVY packing and the renderer both want even dimensions).</summary>
    public VizNdiSettings Normalized() => this with
    {
        NdiName = string.IsNullOrWhiteSpace(NdiName) ? "HaViz" : NdiName.Trim(),
        Width = Math.Clamp(Width, 160, 3840) & ~1,
        Height = Math.Clamp(Height, 120, 2160) & ~1,
        Fps = Math.Clamp(Fps, 1, 120),
        PresetDurationSeconds = Math.Clamp(PresetDurationSeconds, 5, 3600),
        BeatSensitivity = Math.Clamp(BeatSensitivity, 0, 5),
        TransitionSeconds = Math.Clamp(TransitionSeconds, 0, 30),
    };
}
