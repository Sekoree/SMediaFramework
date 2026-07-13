using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using S.Media.Core.Diagnostics;

namespace HaPlay.Models;

/// <summary>
/// Per-machine settings that persist across app launches but live outside any project file
/// (§12.1 - sidebar state is per-machine, not part of <see cref="HaPlayProject"/>).
/// Stored under <c>%LocalAppData%/HaPlay/app-settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    private static readonly Lock FileGate = new();
    internal static string? FilePathOverride { get; set; }
    public bool SidebarCollapsed { get; set; }

    /// <summary>Workspace selected on last shutdown - restored on next launch.</summary>
    public string? LastSelectedWorkspace { get; set; }

    /// <summary>Phase E (§8.7) - last-known main window placement. Restored on next launch; ignored
    /// when the saved position would land off all visible screens (multi-monitor change between
    /// sessions). <see langword="null"/> means "use the window's design-time defaults".</summary>
    public WindowStateSnapshot? MainWindow { get; set; }

    /// <summary>Phase B (§12.2) - per-dialog-type size memory. Key is the dialog's
    /// <see cref="DialogStatePersister"/> id (e.g. <c>"AddNDIOutputDialog"</c>); value is the
    /// last-known size. Position is not persisted - dialogs always centre on their owner.</summary>
    public Dictionary<string, DialogSizeSnapshot> DialogSizes { get; set; } = new();

    /// <summary>Phase E (§8.6) - chrome theme. <see cref="AppThemeMode.System"/> defers to the OS
    /// setting; <see cref="AppThemeMode.Light"/> / <see cref="AppThemeMode.Dark"/> force the variant.
    /// Saved as a string ("system"/"light"/"dark") via the source-gen contract.</summary>
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    /// <summary>Phase E (§8.6) - Fluent density. <see cref="AppDensityMode.Compact"/> keeps the
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

    /// <summary>The projectM visualizer's *.milk preset folder (per-machine; the VIZ ▾ picker sets
    /// it). Null = auto-discover the dev build's fetched pack, else projectM's built-in idle preset.</summary>
    public string? VisualizerPresetDirectory { get; set; }

    /// <summary>Visualizer render width/height (per-machine). The REAL default is 1920x1080 - stored
    /// explicitly so the settings file says what actually happens (review H5: the old 0 sentinel was
    /// labelled "Match output" in the UI but resolved to a hardcoded 1080p). 0 in an older file still
    /// resolves to the same default.</summary>
    public int VisualizerWidth { get; set; } = 1920;

    public int VisualizerHeight { get; set; } = 1080;

    /// <summary>Visualizer target FPS (default 60; 0 in an older file resolves to 60).</summary>
    public int VisualizerFps { get; set; } = 60;

    /// <summary>HTTP remote API listener (per-machine, off by default). Requests require
    /// <see cref="RestApiAccessToken"/>; LAN binding is opt-in via <see cref="RestApiAllowLan"/>.</summary>
    public bool RestApiEnabled { get; set; }

    public int RestApiPort { get; set; } = 8990;

    public bool RestApiAllowLan { get; set; }

    public string? RestApiAccessToken { get; set; }

    /// <summary>Configurable cue-player transport and visualizer shortcuts.</summary>
    public CueHotkeyProfile CueHotkeys { get; set; } = new();

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

    /// <summary>THE write path for every settings writer (review H5): a serialized
    /// load-fresh → mutate → save under the file gate, so no writer can clobber another writer's
    /// fields with a stale whole-object snapshot (the bug that silently reverted visualizer values
    /// whenever a sidebar/theme/dialog save fired with its startup-time copy).</summary>
    public static void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (FileGate)
        {
            var settings = Load();
            mutate(settings);
            settings.Save();
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
                return Migrate(primary);

            var primaryExisted = File.Exists(path);
            if (TryLoadFrom(path + ".bak", out var backup))
            {
                if (primaryExisted)
                    MediaDiagnostics.LogWarning("AppSettings: primary settings file was unreadable; recovered from backup.");
                return Migrate(backup);
            }

            if (primaryExisted)
                MediaDiagnostics.LogWarning("AppSettings: settings file was unreadable and no usable backup exists; using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>Load-time migration: files written before the visualizer defaults became explicit carry
    /// literal zeros (the old "match output" sentinel) - coerce them to the real 1080p60 defaults so the
    /// dialog and renderer agree with what actually happens (review H5).</summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.VisualizerWidth <= 0 || s.VisualizerHeight <= 0)
        {
            s.VisualizerWidth = 1920;
            s.VisualizerHeight = 1080;
        }

        if (s.VisualizerFps <= 0)
            s.VisualizerFps = 60;
        s.CueHotkeys ??= new CueHotkeyProfile();
        return s;
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

                // Refresh the one-deep backup of the last KNOWN-GOOD primary before replacing it.
                RefreshBackup(path);

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

    /// <summary>Copies the current primary to <c>&lt;path&gt;.bak</c> so a later corrupt primary can be
    /// recovered (SET-01). Never overwrites the backup with a corrupt primary (a JSON parse failure means the
    /// existing backup is the last good copy - leave it), and never throws. Retries a transient share violation:
    /// on Windows an AV/indexer scan briefly locks the just-written primary, and a SILENTLY-skipped backup would
    /// let the next corrupt-primary load fall through to defaults instead of the backup (the 2026-07-09 win-x64
    /// "Actual: null" flake).</summary>
    private static void RefreshBackup(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // Read the primary ourselves so a transient LOCK (retry) is distinguishable from CORRUPTION
                // (leave the good backup) - TryLoadFrom collapses both into "false".
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings) is null)
                        return; // structurally empty - not a good primary; keep the existing backup
                }
                File.Copy(path, path + ".bak", overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return; // no primary yet (first save) - nothing to back up; don't retry
            }
            catch (JsonException)
            {
                return; // corrupt primary - do NOT clobber the last good backup
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    return; // give up - the backup is best-effort and Save must never throw
                Thread.Sleep(10 * (attempt + 1)); // let the transient AV/indexer lock clear, then retry
            }
        }
    }
}

/// <summary>Phase E (§8.7) - persisted window placement. Coordinates are in Avalonia pixel space and
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

/// <summary>Phase B (§12.2) - persisted size of a resizable dialog. Position is intentionally not
/// captured: dialogs centre on their owner so a saved point would land on the wrong monitor when the
/// main window moves between sessions.</summary>
public sealed class DialogSizeSnapshot
{
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>Phase E (§8.6) - chrome theme variant. <see cref="System"/> follows the OS preference.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Phase E (§8.6) - Fluent theme density. <see cref="Compact"/> matches the pre-§8.6 default
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
[JsonSerializable(typeof(CueHotkeyProfile))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
