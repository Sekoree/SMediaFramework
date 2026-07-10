using Avalonia.Media;

namespace HaPlay.Models;

/// <summary>One quick-pick tint choice shown in the player panel's swatch row. <see cref="Color"/> null = "None"
/// (clears the tint).</summary>
public sealed record PlayerTintSwatch(string Name, Color? Color)
{
    /// <summary>True for the "None" entry - the swatch renders an empty/✕ cell instead of a color.</summary>
    public bool IsNone => Color is null;

    /// <summary>The swatch fill (transparent for "None"). Kept here so the picker binds a brush directly.</summary>
    public IBrush Brush => Color is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
}

/// <summary>Predefined per-player tint colors - a subtle color-code applied to ALL of a deck's dockable panels
/// so they stay identifiable once split/floated apart. Shares the high-contrast hues of
/// <see cref="CueColorTagPalette"/> (they read on both light and dark themes); the panel applies them at a low
/// alpha as a wash, so the saturated value here is only used for the swatch itself. A custom color is picked
/// with the ColorPicker beside these swatches.</summary>
public static class PlayerTintPalette
{
    public static IReadOnlyList<PlayerTintSwatch> Swatches { get; } =
    [
        new("None", null),
        new("Red", Color.Parse("#E53935")),
        new("Orange", Color.Parse("#FB8C00")),
        new("Yellow", Color.Parse("#FDD835")),
        new("Green", Color.Parse("#43A047")),
        new("Blue", Color.Parse("#1E88E5")),
        new("Purple", Color.Parse("#8E24AA")),
        new("Brown", Color.Parse("#6D4C41")),
    ];
}
