using System.Text.Json;
using Xunit;

namespace HaPlay.Tests;

public sealed class AppSettingsTests
{
    /// <summary>Phase E (§8.7) — a saved <see cref="WindowStateSnapshot"/> round-trips through the
    /// camelCase JSON contract without loss. Guards against renames on the snapshot fields silently
    /// breaking restore on next launch.</summary>
    [Fact]
    public void AppSettings_RoundTrips_MainWindowSnapshot()
    {
        var settings = new AppSettings
        {
            SidebarCollapsed = true,
            LastSelectedWorkspace = "outputs",
            MainWindow = new WindowStateSnapshot
            {
                Width = 1280,
                Height = 720,
                X = 100,
                Y = 80,
                IsMaximized = false,
            },
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });
        // The snapshot must survive a hard restart, so confirm the field names match what we expect on
        // disk. (Anything that renames these fields silently breaks restore on every existing install.)
        Assert.Contains("\"mainWindow\":", json);
        Assert.Contains("\"width\":1280", json);
        Assert.Contains("\"isMaximized\":false", json);

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.MainWindow);
        Assert.Equal(1280, loaded.MainWindow!.Width);
        Assert.Equal(720, loaded.MainWindow.Height);
        Assert.Equal(100, loaded.MainWindow.X);
        Assert.Equal(80, loaded.MainWindow.Y);
        Assert.False(loaded.MainWindow.IsMaximized);
    }

    /// <summary>Phase E (§8.6) — theme + density round-trip through JSON. Both default to the
    /// pre-§8.6 behaviour (System theme, Compact density) so legacy files don't surprise users.</summary>
    [Fact]
    public void AppSettings_RoundTrips_ThemeAndDensity()
    {
        var settings = new AppSettings
        {
            Theme = AppThemeMode.Dark,
            Density = AppDensityMode.Normal,
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });
        Assert.Contains("\"theme\":\"dark\"", json);
        Assert.Contains("\"density\":\"normal\"", json);

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });
        Assert.NotNull(loaded);
        Assert.Equal(AppThemeMode.Dark, loaded!.Theme);
        Assert.Equal(AppDensityMode.Normal, loaded.Density);
    }

    [Fact]
    public void AppSettings_Defaults_AreSystemThemeAndCompactDensity()
    {
        var fresh = new AppSettings();
        Assert.Equal(AppThemeMode.System, fresh.Theme);
        Assert.Equal(AppDensityMode.Compact, fresh.Density);
        // §8.3 — Players-workspace defaults to the pre-§8.3 single-player Tabs layout so an upgrade
        // doesn't surprise users with a re-arranged workspace.
        Assert.Equal(PlayersLayoutMode.Tabs, fresh.PlayersLayout);
    }

    /// <summary>§8.3 — PlayersLayout round-trips through JSON as a kebab-cased string.</summary>
    [Fact]
    public void AppSettings_RoundTrips_PlayersLayout()
    {
        var settings = new AppSettings { PlayersLayout = PlayersLayoutMode.Split };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });
        Assert.Contains("\"playersLayout\":\"split\"", json);

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });
        Assert.NotNull(loaded);
        Assert.Equal(PlayersLayoutMode.Split, loaded!.PlayersLayout);
    }

    /// <summary>Phase B (§12.2) — per-dialog size memory round-trips through JSON. Keys are dialog-type
    /// names, values are width/height pairs.</summary>
    [Fact]
    public void AppSettings_RoundTrips_DialogSizes()
    {
        var settings = new AppSettings
        {
            DialogSizes =
            {
                ["AddNDIOutputDialog"] = new DialogSizeSnapshot { Width = 600, Height = 700 },
                ["RebindMissingOutputsDialog"] = new DialogSizeSnapshot { Width = 720, Height = 480 },
            },
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        // Field name on the wire must stay 'dialogSizes' so old persisted state keeps loading.
        Assert.Contains("\"dialogSizes\":", json);

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.DialogSizes.Count);
        Assert.Equal(600, loaded.DialogSizes["AddNDIOutputDialog"].Width);
        Assert.Equal(480, loaded.DialogSizes["RebindMissingOutputsDialog"].Height);
    }

    /// <summary>Old <c>app-settings.json</c> files (pre-§8.7) don't carry the <c>mainWindow</c> field at
    /// all. They must load cleanly with <c>MainWindow == null</c> so the window code-behind falls back
    /// to design-time defaults rather than crashing.</summary>
    [Fact]
    public void AppSettings_Deserializes_LegacyWithoutMainWindow()
    {
        const string legacy = """{"sidebarCollapsed":true,"lastSelectedWorkspace":"players"}""";

        var loaded = JsonSerializer.Deserialize<AppSettings>(legacy, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.NotNull(loaded);
        Assert.True(loaded!.SidebarCollapsed);
        Assert.Equal("players", loaded.LastSelectedWorkspace);
        Assert.Null(loaded.MainWindow);
        // Legacy files predate the dialogSizes map; the default ctor seeds an empty dictionary so
        // dialog state still has somewhere to live on the first resize.
        Assert.NotNull(loaded.DialogSizes);
        Assert.Empty(loaded.DialogSizes);
    }
}
