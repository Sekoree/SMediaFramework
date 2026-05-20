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

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
