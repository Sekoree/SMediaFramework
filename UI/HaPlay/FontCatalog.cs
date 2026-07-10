using System.Collections.Generic;
using System.Linq;

namespace HaPlay;

/// <summary>
/// Font family names offered in the text-cue font dropdown: the installed system fonts (via Avalonia's
/// <see cref="Avalonia.Media.FontManager"/>) plus, on demand, whatever family a cue currently uses. Avalonia's
/// bundled default ("Inter") and any app-registered/embedded family are NOT in the OS system-font list, so a
/// plain dropdown would show blank for the default - <see cref="WithCurrent"/> pins the current value at the top
/// so it always displays and stays selectable.
/// </summary>
internal static class FontCatalog
{
    /// <summary>Installed system font family names, sorted. Empty when the font manager isn't available (headless
    /// tests). Loaded once - enumeration is not free.</summary>
    public static IReadOnlyList<string> SystemFamilies { get; } = Load();

    /// <summary>The system families with <paramref name="current"/> pinned at the front when it isn't already an
    /// installed family (e.g. the embedded "Inter" default, or a hand-typed family from an imported show).</summary>
    public static IReadOnlyList<string> WithCurrent(string? current)
    {
        if (string.IsNullOrWhiteSpace(current) ||
            SystemFamilies.Contains(current, System.StringComparer.OrdinalIgnoreCase))
            return SystemFamilies;
        return new[] { current }.Concat(SystemFamilies).ToArray();
    }

    private static IReadOnlyList<string> Load()
    {
        try
        {
            return Avalonia.Media.FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return []; // font manager not initialized (e.g. headless tests)
        }
    }
}
