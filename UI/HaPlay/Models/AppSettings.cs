using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Per-machine settings that persist across app launches but live outside any project file
/// (§12.1 — sidebar state is per-machine, not part of <see cref="HaPlayProject"/>).
/// Stored under <c>%LocalAppData%/HaPlay/app-settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    public bool SidebarCollapsed { get; set; }

    /// <summary>Workspace selected on last shutdown — restored on next launch.</summary>
    public string? LastSelectedWorkspace { get; set; }

    /// <summary>Phase E (§8.7) — last-known main window placement. Restored on next launch; ignored
    /// when the saved position would land off all visible screens (multi-monitor change between
    /// sessions). <see langword="null"/> means "use the window's design-time defaults".</summary>
    public WindowStateSnapshot? MainWindow { get; set; }

    /// <summary>Phase B (§12.2) — per-dialog-type size memory. Key is the dialog's
    /// <see cref="DialogStatePersister"/> id (e.g. <c>"AddNDIOutputDialog"</c>); value is the
    /// last-known size. Position is not persisted — dialogs always centre on their owner.</summary>
    public Dictionary<string, DialogSizeSnapshot> DialogSizes { get; set; } = new();

    /// <summary>Phase E (§8.6) — chrome theme. <see cref="AppThemeMode.System"/> defers to the OS
    /// setting; <see cref="AppThemeMode.Light"/> / <see cref="AppThemeMode.Dark"/> force the variant.
    /// Saved as a string ("system"/"light"/"dark") via the source-gen contract.</summary>
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    /// <summary>Phase E (§8.6) — Fluent density. <see cref="AppDensityMode.Compact"/> keeps the
    /// tight pre-§8.6 spacing (the default), <see cref="AppDensityMode.Normal"/> opens it up.</summary>
    public AppDensityMode Density { get; set; } = AppDensityMode.Compact;

    /// <summary>
    /// When true, live NDI (and similar) video keeps native UYVY into local SDL outputs instead of
    /// converting to BGRA32 first. Requires correct full-range metadata on frames.
    /// </summary>
    public bool PreferLiveUyvyPassthrough { get; set; }

    /// <summary>HTTP remote API listener (per-machine, off by default — it is unauthenticated and
    /// binds all interfaces so show controllers on the LAN can reach it).</summary>
    public bool RestApiEnabled { get; set; }

    public int RestApiPort { get; set; } = 8990;

    private static string FilePath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "HaPlay", "app-settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new AppSettings();
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, this, AppSettingsJsonContext.Default.AppSettings);
        }
        catch
        {
            /* best effort — losing this file just resets sidebar state on next launch */
        }
    }
}

/// <summary>Phase E (§8.7) — persisted window placement. Coordinates are in Avalonia pixel space and
/// must be re-validated against the current <see cref="Avalonia.Controls.Screens"/> collection before
/// being applied (multi-monitor unplug between sessions can leave the saved point off-screen).</summary>
public sealed class WindowStateSnapshot
{
    public double Width { get; set; }
    public double Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>True when the window was maximized at save time. Restore re-applies size + position from
    /// the snapshot first (so the un-maximize size is remembered) and then sets
    /// <see cref="Avalonia.Controls.Window.WindowState"/> to <see cref="Avalonia.Controls.WindowState.Maximized"/>.</summary>
    public bool IsMaximized { get; set; }
}

/// <summary>Phase B (§12.2) — persisted size of a resizable dialog. Position is intentionally not
/// captured: dialogs centre on their owner so a saved point would land on the wrong monitor when the
/// main window moves between sessions.</summary>
public sealed class DialogSizeSnapshot
{
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>Phase E (§8.6) — chrome theme variant. <see cref="System"/> follows the OS preference.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Phase E (§8.6) — Fluent theme density. <see cref="Compact"/> matches the pre-§8.6 default
/// (tight padding), <see cref="Normal"/> opens spacing up for touch / accessibility.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppDensityMode>))]
public enum AppDensityMode
{
    Compact,
    Normal,
}


[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WindowStateSnapshot))]
[JsonSerializable(typeof(DialogSizeSnapshot))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
