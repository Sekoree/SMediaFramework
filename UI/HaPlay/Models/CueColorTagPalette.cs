namespace HaPlay.Models;

/// <summary>Fixed 8-slot palette for cue color tags (Phase 5.8.1). Index 0 = no tag
/// (transparent / no fill); 1..7 map to high-contrast colors that read on both light
/// and dark themes. Operators set tags from the drawer swatch row.</summary>
public static class CueColorTagPalette
{
    public const int MaxIndex = 7;

    /// <summary>Hex string suitable for direct `Fill="…"` binding. Index 0 returns
    /// transparent so the tree's color strip column is invisible for untagged cues.</summary>
    public static string BrushHex(int index) => index switch
    {
        1 => "#E53935", // red
        2 => "#FB8C00", // orange
        3 => "#FDD835", // yellow
        4 => "#43A047", // green
        5 => "#1E88E5", // blue
        6 => "#8E24AA", // purple
        7 => "#6D4C41", // brown
        _ => "Transparent",
    };

    /// <summary>Operator-facing label for the right-click "Set color tag…" submenu.
    /// Unused for now (swatches show the color directly), but kept here so we have
    /// one canonical mapping when the context menu lands.</summary>
    public static string Name(int index) => index switch
    {
        1 => "Red",
        2 => "Orange",
        3 => "Yellow",
        4 => "Green",
        5 => "Blue",
        6 => "Purple",
        7 => "Brown",
        _ => "None",
    };
}
