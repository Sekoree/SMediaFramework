using System.Text.Json;
using Avalonia;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(1900, 900, 1.0, 928, 363)]
    [InlineData(1900, 900, 1.5, 448, 43)]
    [InlineData(-200, -100, 1.0, 0, 0)]
    public void MainWindowRestore_ClampsTheCompleteFrameInsideTheWorkingArea(
        int requestedX, int requestedY, double scaling, int expectedX, int expectedY)
    {
        var clamped = MainWindow.ClampWindowPosition(
            new PixelPoint(requestedX, requestedY),
            new PixelRect(0, 0, 1920, 1035),
            scaling,
            clientWidthDip: 960,
            clientHeightDip: 640);

        Assert.Equal(new PixelPoint(expectedX, expectedY), clamped);
    }

    [Fact]
    public void Save_RecoversFromBackup_WithoutOverwritingItWithCorruptPrimary()
    {
        var dir = Directory.CreateTempSubdirectory("haplay-settings-").FullName;
        var path = Path.Combine(dir, "app-settings.json");
        AppSettings.FilePathOverride = path;
        try
        {
            new AppSettings { LastSelectedWorkspace = "players" }.Save();
            new AppSettings { LastSelectedWorkspace = "cues" }.Save(); // backup now contains "players"
            File.WriteAllText(path, "{ corrupt");

            var recovered = AppSettings.Load();
            Assert.Equal("players", recovered.LastSelectedWorkspace);
            recovered.Save();

            File.Delete(path); // force the next load through the backup
            Assert.Equal("players", AppSettings.Load().LastSelectedWorkspace);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            AppSettings.FilePathOverride = null;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Phase E (§8.7) - a saved <see cref="WindowStateSnapshot"/> round-trips through the
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

    /// <summary>Phase E (§8.6) - theme + density round-trip through JSON. Both default to the
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
        Assert.Equal("Space", fresh.CueHotkeys.Go);
        Assert.Equal("Esc", fresh.CueHotkeys.StopThenPanic);
        Assert.Equal("Ctrl+Esc", fresh.CueHotkeys.PanicNow);
    }

    [Fact]
    public void AppSettings_RoundTripsCueHotkeys()
    {
        var settings = new AppSettings
        {
            CueHotkeys = new CueHotkeyProfile
            {
                Go = "G",
                StopThenPanic = "F12",
                NextVisualizerPreset = "Ctrl+N",
            },
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);

        Assert.NotNull(loaded);
        Assert.Equal("G", loaded!.CueHotkeys.Go);
        Assert.Equal("F12", loaded.CueHotkeys.StopThenPanic);
        Assert.Equal("Ctrl+N", loaded.CueHotkeys.NextVisualizerPreset);
    }

    /// <summary>UI rewrite P5: the PlayersLayout setting was removed with the deck grid. A settings
    /// file persisted by an older build (still carrying the key) must load without errors.</summary>
    [Fact]
    public void AppSettings_LegacyPlayersLayoutKey_IsIgnoredOnLoad()
    {
        var json = "{\"theme\":\"dark\",\"playersLayout\":\"split\"}";
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });
        Assert.NotNull(loaded);
        Assert.Equal(AppThemeMode.Dark, loaded!.Theme);
    }

    /// <summary>Phase B (§12.2) - per-dialog size memory round-trips through JSON. Keys are dialog-type
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
