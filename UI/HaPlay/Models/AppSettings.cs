using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using S.Media.Core.Diagnostics;

namespace HaPlay.Models;

/// <summary>
/// Per-machine settings that persist across app launches but live outside any project file
/// (§12.1 — sidebar state is per-machine, not part of <see cref="HaPlayProject"/>).
/// Stored under <c>%LocalAppData%/HaPlay/app-settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    private static readonly Lock FileGate = new();
    internal static string? FilePathOverride { get; set; }
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

    /// <summary>Base control theme (overall chrome look). <see cref="AppBaseTheme.Classic"/> is the in-repo
    /// Windows-Classic skin (light only); <see cref="AppBaseTheme.Simple"/> and <see cref="AppBaseTheme.Fluent"/>
    /// are Avalonia's built-in themes and honour the <see cref="Theme"/> light/dark variant. Saved as
    /// "classic"/"simple"/"fluent".</summary>
    public AppBaseTheme BaseTheme { get; set; } = AppBaseTheme.Classic;

    /// <summary>
    /// When true, live NDI (and similar) video keeps native UYVY into local SDL outputs instead of
    /// converting to BGRA32 first. Requires correct full-range metadata on frames.
    /// </summary>
    public bool PreferLiveUyvyPassthrough { get; set; }

    /// <summary>HTTP remote API listener (per-machine, off by default). Requests require
    /// <see cref="RestApiAccessToken"/>; LAN binding is opt-in via <see cref="RestApiAllowLan"/>.</summary>
    public bool RestApiEnabled { get; set; }

    public int RestApiPort { get; set; } = 8990;

    public bool RestApiAllowLan { get; set; }

    public string? RestApiAccessToken { get; set; }

    private static string FilePath
    {
        get
        {
            if (FilePathOverride is { Length: > 0 } overridden)
                return overridden;
            if (Environment.GetEnvironmentVariable("HAPLAY_SETTINGS_PATH") is { Length: > 0 } environmentPath)
                return Path.GetFullPath(environmentPath);
            return Path.Combine(HaPlayStoragePaths.LocalAppRoot, "app-settings.json");
        }
    }

    /// <summary>Loads settings, recovering from the one-deep backup when the primary file is corrupt
    /// (SET-01). A missing file is a clean first-run (no log); an unreadable file is logged.</summary>
    public static AppSettings Load()
    {
        lock (FileGate)
        {
            var path = FilePath;
            if (TryLoadFrom(path, out var primary))
                return primary;

            var primaryExisted = File.Exists(path);
            if (TryLoadFrom(path + ".bak", out var backup))
            {
                if (primaryExisted)
                    MediaDiagnostics.LogWarning("AppSettings: primary settings file was unreadable; recovered from backup.");
                return backup;
            }

            if (primaryExisted)
                MediaDiagnostics.LogWarning("AppSettings: settings file was unreadable and no usable backup exists; using defaults.");
            return new AppSettings();
        }
    }

    private static bool TryLoadFrom(string path, [NotNullWhen(true)] out AppSettings? settings)
    {
        settings = null;
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings);
            return settings is not null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Persists settings atomically (temp file → flush → move), keeping one <c>.bak</c> of the
    /// previous good file so a partial/corrupt write is recoverable (SET-01). Never throws.</summary>
    public void Save()
    {
        lock (FileGate)
        {
            var path = FilePath;
            string? temp = null;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, this, AppSettingsJsonContext.Default.AppSettings);
                    stream.Flush(flushToDisk: true);
                }

                // Keep one backup of the last KNOWN-GOOD primary. Never overwrite a valid backup with a corrupt
                // primary recovered from that backup; if the final move then failed, both copies would be lost.
                if (TryLoadFrom(path, out _))
                {
                    try { File.Copy(path, path + ".bak", overwrite: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* backup is best-effort */ }
                }

                File.Move(temp, path, overwrite: true);
                temp = null;
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning($"AppSettings.Save failed ({ex.GetType().Name}: {ex.Message}); previous settings retained.");
            }
            finally
            {
                if (temp is not null)
                {
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
                }
            }
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

/// <summary>Base control theme (the overall chrome look). <see cref="Classic"/> is light-only; <see cref="Simple"/>
/// and <see cref="Fluent"/> are variant-aware (they honour <see cref="AppThemeMode"/>) and <see cref="Fluent"/>
/// additionally supports <see cref="AppDensityMode"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppBaseTheme>))]
public enum AppBaseTheme
{
    Classic,
    Simple,
    Fluent,
}


[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WindowStateSnapshot))]
[JsonSerializable(typeof(DialogSizeSnapshot))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
